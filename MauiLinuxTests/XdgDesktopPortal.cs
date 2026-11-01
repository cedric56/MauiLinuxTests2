using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials;

/// <summary>
/// Helpers for xdg-desktop-portal (<c>org.freedesktop.portal.Desktop</c>) on the session bus.
/// Portals work for sandboxed (Flatpak/Snap) and unsandboxed apps.
/// No UI toolkit dependency.
/// </summary>
internal static class XdgDesktopPortal
{
    public const string BusName = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";

    const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>Request::Response: the request is carried out.</summary>
    public const uint ResponseSuccess = 0;
    /// <summary>Request::Response: the user cancelled the interaction.</summary>
    public const uint ResponseCancelled = 1;
    /// <summary>Request::Response: the interaction was ended in some other way.</summary>
    public const uint ResponseOther = 2;

    public static bool IsSessionBusAvailable => OperatingSystem.IsLinux() && DBusAddress.Session is not null;

    /// <summary>
    /// A token for the <c>handle_token</c> option (a valid object path element).
    /// </summary>
    public static string NewHandleToken() => "maui_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether the error means the portal (or this portal interface) isn't available.
    /// </summary>
    public static bool IsPortalMissing(DBusErrorReplyException ex) =>
        ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
                     or "org.freedesktop.DBus.Error.UnknownMethod"
                     or "org.freedesktop.DBus.Error.UnknownInterface"
                     or "org.freedesktop.DBus.Error.UnknownObject";

    /// <summary>
    /// Calls a portal method that returns a Request handle and waits for its Response.
    /// </summary>
    /// <param name="handleToken">The token the message passes as the <c>handle_token</c> option.</param>
    /// <param name="createMessage">Builds the method call.</param>
    /// <param name="timeout">How long to wait for the Response.</param>
    /// <returns>
    /// The response code, or <see langword="null"/> when no Response was received in time
    /// or the portal is too old (before 0.9) to honor <c>handle_token</c>.
    /// </returns>
    public static async Task<uint?> RequestAsync(string handleToken, Func<DBusConnection, MessageBuffer> createMessage, TimeSpan timeout)
    {
        var connection = await GetConnectionAsync().ConfigureAwait(false);

        var response = await RequestAsync(connection, handleToken, createMessage, timeout).ConfigureAwait(false);
        return response?.Code;
    }

    /// <summary>
    /// Same as <see cref="RequestAsync(string, Func{DBusConnection, MessageBuffer}, TimeSpan)"/>
    /// on a given (connected) connection, also returning the Response results.
    /// </summary>
    public static async Task<PortalResponse?> RequestAsync(DBusConnection connection, string handleToken, Func<DBusConnection, MessageBuffer> createMessage, TimeSpan timeout)
    {
        // Subscribe before calling, on the path the portal will use
        // (/org/freedesktop/portal/desktop/request/SENDER/TOKEN), so the Response can't be missed.
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var requestPath = $"{ObjectPath}/request/{sender}/{handleToken}";

        var response = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = RequestInterface,
            Member = "Response",
            Path = requestPath,
        };

        // Response(u response, a{sv} results)
        using var subscription = await connection.AddMatchAsync(
            rule,
            static (Message message, object? state) =>
            {
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return new PortalResponse(code, results);
            },
            static (Exception? ex, PortalResponse value, object? readerState, object? handlerState) =>
            {
                var tcs = (TaskCompletionSource<PortalResponse>)handlerState!;
                if (ex is not null)
                    tcs.TrySetException(ex);
                else
                    tcs.TrySetResult(value);
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: response,
            emitOnCapturedContext: false).ConfigureAwait(false);

        var handlePath = await connection.CallMethodAsync(
            createMessage(connection),
            static (Message m, object? state) => m.GetBodyReader().ReadObjectPathAsString()).ConfigureAwait(false);

        if (handlePath != requestPath)
            return null;

        var completed = await Task.WhenAny(response.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == response.Task ? await response.Task.ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Calls a portal method and reads its reply.
    /// </summary>
    public static async Task<T> CallAsync<T>(Func<DBusConnection, MessageBuffer> createMessage, MessageValueReader<T> reader)
    {
        var connection = await GetConnectionAsync().ConfigureAwait(false);
        return await connection.CallMethodAsync(createMessage(connection), reader).ConfigureAwait(false);
    }

    // ----- Connection -----

    const string RegistryInterface = "org.freedesktop.host.portal.Registry";

    /// <summary>
    /// The application id of an unsandboxed app: the <c>MauiApplicationId</c> assembly metadata
    /// the MAUI GTK4 backend generates from &lt;ApplicationId&gt; (also the basename of the
    /// .desktop file it writes). Null when unknown.
    /// </summary>
    public static string? HostApplicationId { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MauiApplicationId" && !string.IsNullOrWhiteSpace(a.Value))?.Value;

    static bool IsSandboxed =>
        System.IO.File.Exists("/.flatpak-info") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"));

    /// <summary>
    /// Tells xdg-desktop-portal (1.19+) which application an unsandboxed process is.
    /// Without it the app has an empty id: permission dialogs say "An app wants to…", and the
    /// answer is stored for every unidentified app (so a previous "allow"/"deny" given to any
    /// unsandboxed app is reused silently, without dialog).
    /// </summary>
    static async Task RegisterHostAppAsync(DBusConnection connection)
    {
        if (IsSandboxed || HostApplicationId is not { } appId)
            return;

        try
        {
            await connection.CallMethodAsync(CreateRegisterMessage(connection, appId)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            // Older portal (no registry) or app id without a matching .desktop file.
            System.Diagnostics.Debug.WriteLine($"[XdgDesktopPortal] host app registration failed: {ex.Message}");
        }
    }

    // Register(s app_id, a{sv} options)
    static MessageBuffer CreateRegisterMessage(DBusConnection connection, string appId)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusName,
            path: ObjectPath,
            @interface: RegistryInterface,
            member: "Register",
            signature: "sa{sv}");

        writer.WriteString(appId);
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(options);

        return writer.CreateMessage();
    }

    static readonly SemaphoreSlim s_connectionGate = new(1, 1);
    static DBusConnection? s_connection;

    /// <summary>
    /// The session bus connection used for portal calls.
    /// </summary>
    /// <remarks>
    /// Not <see cref="DBusConnection.Session"/>: that shared connection auto-connects, and
    /// auto-connect connections don't expose <see cref="DBusConnection.UniqueName"/>
    /// ("Method cannot be used on autoconnect connections"), which is needed to compute
    /// the Request object path. This one is connected explicitly, once, and recreated
    /// if the bus connection is lost.
    /// </remarks>
    public static async Task<DBusConnection> GetConnectionAsync()
    {
        if (Volatile.Read(ref s_connection) is { } current)
            return current;

        await s_connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s_connection is { } existing)
                return existing;

            var address = DBusAddress.Session
                ?? throw new InvalidOperationException("D-Bus session bus is not available (DBUS_SESSION_BUS_ADDRESS is not set).");

            var connection = new DBusConnection(address);
            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);

                // Must be the first portal call made by the connection.
                await RegisterHostAppAsync(connection).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            // Forget it once disconnected, so the next call reconnects.
            _ = connection.DisconnectedAsync().ContinueWith(
                _ => Interlocked.CompareExchange(ref s_connection, null, connection),
                TaskScheduler.Default);

            Volatile.Write(ref s_connection, connection);
            return connection;
        }
        finally
        {
            s_connectionGate.Release();
        }
    }

    /// <summary>
    /// Reads a portal property (org.freedesktop.DBus.Properties.Get).
    /// </summary>
    public static Task<VariantValue> GetPropertyAsync(string interfaceName, string propertyName) =>
        CallAsync(
            connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: BusName,
                    path: ObjectPath,
                    @interface: "org.freedesktop.DBus.Properties",
                    member: "Get",
                    signature: "ss");
                writer.WriteString(interfaceName);
                writer.WriteString(propertyName);
                return writer.CreateMessage();
            },
            static (Message m, object? state) => m.GetBodyReader().ReadVariantValue());

    /// <summary>
    /// Runs <paramref name="task"/> from a synchronous member without deadlocking the UI
    /// thread, and gives up after <paramref name="timeout"/>.
    /// </summary>
    public static T WaitBounded<T>(Func<Task<T>> task, TimeSpan timeout, T fallback)
    {
        try
        {
            var probe = Task.Run(task);
            return probe.Wait(timeout) ? probe.Result : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>A portal Request::Response: the response code and its results vardict.</summary>
internal sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);