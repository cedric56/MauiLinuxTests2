using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Essentials.AppModel;

/// <summary>
/// How the desktop environment associates the running window with the generated desktop entry.
/// </summary>
/// <param name="DesktopFileId">
/// Desktop file id, without the <c>.desktop</c> extension.
/// On Wayland it must equal the window's <c>app_id</c>.
/// </param>
/// <param name="StartupWmClass">
/// X11 <c>WM_CLASS</c> of the window, written as <c>StartupWMClass</c>; <see langword="null"/> to omit it.
/// </param>
/// <param name="Icon">Icon name or path written as <c>Icon</c> in a generated entry; <see langword="null"/> to omit it.</param>
public readonly record struct LinuxDesktopIdentity(string DesktopFileId, string? StartupWmClass, string? Icon = null);

/// <summary>
/// UI-toolkit specific services needed by <see cref="LinuxAppActions"/>.
/// Implemented once per backend (Avalonia, MAUI Gtk4, ...).
/// </summary>
public interface ILinuxAppActionsHost
{
    /// <summary>
    /// Prepares the main window and returns the identity the desktop entry must use.
    /// </summary>
    /// <param name="uniqueId">
    /// A fresh identifier generated for this registration ([0-9a-f]{32}).
    /// A host that can rename its window class (e.g. Avalonia on X11) may apply it,
    /// which forces the shell to reload the actions; other hosts return their fixed id.
    /// </param>
    Task<LinuxDesktopIdentity> PrepareAsync(string uniqueId);

    /// <summary>
    /// Called once the desktop entry for <paramref name="identity"/> has been written,
    /// so the host can make the shell re-match the window with it. Runs in the background.
    /// </summary>
    Task DesktopEntryPublishedAsync(LinuxDesktopIdentity identity) => Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread.
    /// </summary>
    void BeginInvokeOnMainThread(Action action);
}

/// <summary>
/// MAUI Gtk4 backend for <see cref="LinuxAppActions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Like the Avalonia host, every registration gives the main window a new identity
/// (Wayland <c>app_id</c> through <c>gdk_wayland_toplevel_set_application_id</c>, X11
/// <c>WM_CLASS</c> through <c>XSetClassHint</c>). The application's single desktop entry
/// (<c>&lt;app id&gt;.desktop</c>, written by the MAUI GTK4 backend) is updated with that
/// StartupWMClass and the actions; the shell re-associates the window with it, which is
/// what makes GNOME show the updated actions on right click without restarting the app.
/// </para>
/// <para>
/// When the identity can't be changed (unknown GDK backend, symbol missing), the same entry
/// is updated with the window's own class; the shell may only pick the actions up later.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class Gtk4LinuxAppActionsHost : ILinuxAppActionsHost
{
    static readonly TimeSpan s_mainWindowTimeout = TimeSpan.FromSeconds(10);

    readonly Func<Action, bool> _dispatch;

    /// <param name="dispatch">Runs an action on the UI thread (e.g. a captured <c>IDispatcher.Dispatch</c>).</param>
    /// <remarks>
    /// No application id is needed: the window class (StartupWMClass) is read from the window itself.
    /// </remarks>
    public Gtk4LinuxAppActionsHost(Func<Action, bool> dispatch)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    // Delays after which the window class is applied again, once the entry is written (see DesktopEntryPublishedAsync).
    // GNOME Shell reloads the applications 5 s after the last change (ShellAppCache
    // DEFAULT_TIMEOUT_SECONDS), then rebuilds its cache in a thread: re-match just after,
    // and a few more times in case the reload took longer (busy session, slow disk).
    static readonly TimeSpan[] s_rematchTimes =
    {
        TimeSpan.FromSeconds(5.5), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15),
    };

    static readonly TimeSpan s_toggleInterval = TimeSpan.FromMilliseconds(250);

    // Class of the window before the first registration renamed it: names the backend's entry.
    string? _originalClass;

    // Class given to the window by the latest registration.
    volatile string? _currentClass;

    public async Task<LinuxDesktopIdentity> PrepareAsync(string uniqueId)
    {
        // When SetAsync runs at startup, the window may not exist yet.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? windowClass;
        while ((windowClass = await RunOnUiThreadAsync(GtkWindowIdentity.GetWmClass).ConfigureAwait(false)) is null)
        {
            if (stopwatch.Elapsed >= s_mainWindowTimeout)
                throw new TimeoutException("The main window was not created in time.");

            await Task.Delay(50).ConfigureAwait(false);
        }

        // After a first registration the window already has a generated class.
        _originalClass ??= windowClass;

        var renamed = await RunOnUiThreadAsync(() => GtkWindowIdentity.TrySet(uniqueId)).ConfigureAwait(false) == true;

        // The single entry is the one named after the original class: the MAUI GTK4 backend
        // writes <app id>.desktop at startup, and GTK uses the app id as window class.
        if (!renamed)
        {
            _currentClass = null;
            return new LinuxDesktopIdentity(_originalClass, _originalClass);
        }

        // StartupWMClass must be the class the window really has now, as seen by the shell:
        // read it back (X11 WM_CLASS / Wayland app_id) rather than assuming it.
        var wmClass = await RunOnUiThreadAsync(GtkWindowIdentity.GetWmClass).ConfigureAwait(false) ?? uniqueId;
        _currentClass = wmClass;

        return new LinuxDesktopIdentity(_originalClass, wmClass);
    }

    /// <summary>
    /// Makes GNOME Shell re-match the window with the updated entry, so the new actions
    /// show on right click without restarting the app.
    /// </summary>
    /// <remarks>
    /// <para>What GNOME Shell does (shell-app-system.c, shell-app-cache.c, shell-window-tracker.c):</para>
    /// <list type="bullet">
    /// <item>it reloads desktop entries 5 s after the last change in the applications directories;</item>
    /// <item>it then rebuilds an app only if its Name/Exec/Icon/Comment changed (hence the Exec revision),
    /// but keeps windows attached to the old app object, with the old actions;</item>
    /// <item>it re-matches a window only when its class changes, and mutter reports a change only
    /// when the value differs (<c>meta_window_set_wm_class</c>): setting the same class again does nothing.</item>
    /// </list>
    /// <para>
    /// So once GNOME has reloaded, the class is switched to a temporary value and back: two real
    /// changes, and the window is matched again with the rebuilt app and its new actions.
    /// </para>
    /// </remarks>
    public async Task DesktopEntryPublishedAsync(LinuxDesktopIdentity identity)
    {
        var wmClass = identity.StartupWmClass;
        if (wmClass is null || wmClass != _currentClass)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var time in s_rematchTimes)
        {
            var wait = time - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait).ConfigureAwait(false);

            // A newer registration renamed the window meanwhile.
            if (wmClass != _currentClass)
                return;

            // Separate steps: on X11 mutter reads WM_CLASS when it handles the PropertyNotify,
            // so two changes in a row could both be read as the final value (no change seen).
            await RunOnUiThreadAsync(() => GtkWindowIdentity.TrySet(wmClass + "-refresh")).ConfigureAwait(false);
            await Task.Delay(s_toggleInterval).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => GtkWindowIdentity.TrySet(wmClass)).ConfigureAwait(false);
        }
    }

    public void BeginInvokeOnMainThread(Action action) => _dispatch(action);

    Task<T> RunOnUiThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatch(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!queued)
            tcs.TrySetException(new InvalidOperationException("Unable to dispatch to the UI thread."));

        return tcs.Task;
    }
}

/// <summary>
/// Changes the identity (Wayland app_id / X11 WM_CLASS) of the active GTK4 window.
/// </summary>
[SupportedOSPlatform("linux")]
static class GtkWindowIdentity
{
    const string GtkLibrary = "libgtk-4.so.1";
    const string GObjectLibrary = "libgobject-2.0.so.0";
    const string X11Library = "libX11.so.6";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetApplicationIdFn(IntPtr toplevel, [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nuint GetXidFn(IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr GetXDisplayFn(IntPtr display);

    [StructLayout(LayoutKind.Sequential)]
    struct XClassHint
    {
        public IntPtr ResName;
        public IntPtr ResClass;
    }

    /// <summary>
    /// Must run on the UI thread. Returns null when there is no window yet,
    /// false when the identity can't be changed on this backend.
    /// </summary>
    public static bool? TrySet(string id)
    {
        if (Gio.Application.GetDefault() is not Gtk.Application application || application.GetActiveWindow() is not { } window)
            return null;

        var surface = gtk_native_get_surface(window.Handle.DangerousGetHandle());
        if (surface == IntPtr.Zero)
            return null; // not realized yet

        // GdkWaylandToplevel / GdkX11Toplevel (a GdkX11Surface subclass): the types are only
        // registered when GTK is built with (and running on) that backend.
        if (IsInstanceOf(surface, "GdkWaylandToplevel"))
            return TrySetWayland(surface, id);

        if (IsInstanceOf(surface, "GdkX11Surface"))
            return TrySetX11(surface, id);

        return false;
    }

    // Last app_id given through gdk_wayland_toplevel_set_application_id (Wayland has no getter).
    static string? s_waylandAppId;

    /// <summary>
    /// The class the shell sees for the active window: X11 WM_CLASS (res_class) or Wayland app_id.
    /// Must run on the UI thread. Returns null when there is no window or it can't be determined.
    /// </summary>
    public static string? GetWmClass()
    {
        if (Gio.Application.GetDefault() is not Gtk.Application application || application.GetActiveWindow() is not { } window)
            return null;

        var surface = gtk_native_get_surface(window.Handle.DangerousGetHandle());
        if (surface == IntPtr.Zero)
            return null;

        if (IsInstanceOf(surface, "GdkX11Surface"))
            return GetX11Class(surface);

        if (IsInstanceOf(surface, "GdkWaylandToplevel"))
        {
            // GTK uses the GApplication id as app_id, or the program name without one.
            return s_waylandAppId
                ?? NullIfEmpty(Marshal.PtrToStringUTF8(g_application_get_application_id(application.Handle.DangerousGetHandle())))
                ?? NullIfEmpty(Marshal.PtrToStringUTF8(g_get_prgname()));
        }

        return null;
    }

    static string? GetX11Class(IntPtr surface)
    {
        if (!TryGetExport(GtkLibrary, "gdk_x11_surface_get_xid", out GetXidFn? getXid) ||
            !TryGetExport(GtkLibrary, "gdk_x11_display_get_xdisplay", out GetXDisplayFn? getXDisplay))
        {
            return null;
        }

        var xdisplay = getXDisplay(gdk_surface_get_display(surface));
        var xid = getXid(surface);
        if (xdisplay == IntPtr.Zero || xid == 0)
            return null;

        var hint = new XClassHint();
        if (XGetClassHint(xdisplay, xid, ref hint) == 0)
            return null;

        try
        {
            // StartupWMClass is matched against res_class (res_name as a fallback).
            return NullIfEmpty(Marshal.PtrToStringUTF8(hint.ResClass)) ?? NullIfEmpty(Marshal.PtrToStringUTF8(hint.ResName));
        }
        finally
        {
            if (hint.ResName != IntPtr.Zero)
                XFree(hint.ResName);
            if (hint.ResClass != IntPtr.Zero)
                XFree(hint.ResClass);
        }
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static bool IsInstanceOf(IntPtr instance, string typeName)
    {
        var type = g_type_from_name(typeName);
        return type != 0 && g_type_check_instance_is_a(instance, type);
    }

    static bool TrySetWayland(IntPtr toplevel, string id)
    {
        if (!TryGetExport(GtkLibrary, "gdk_wayland_toplevel_set_application_id", out SetApplicationIdFn? setApplicationId))
            return false;

        setApplicationId(toplevel, id);
        s_waylandAppId = id;
        return true;
    }

    static bool TrySetX11(IntPtr surface, string id)
    {
        if (!TryGetExport(GtkLibrary, "gdk_x11_surface_get_xid", out GetXidFn? getXid) ||
            !TryGetExport(GtkLibrary, "gdk_x11_display_get_xdisplay", out GetXDisplayFn? getXDisplay))
        {
            return false;
        }

        var xdisplay = getXDisplay(gdk_surface_get_display(surface));
        var xid = getXid(surface);
        if (xdisplay == IntPtr.Zero || xid == 0)
            return false;

        var hint = new XClassHint
        {
            ResName = Marshal.StringToCoTaskMemUTF8(id),
            ResClass = Marshal.StringToCoTaskMemUTF8(id),
        };
        try
        {
            XSetClassHint(xdisplay, xid, ref hint);
            XFlush(xdisplay);
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(hint.ResName);
            Marshal.FreeCoTaskMem(hint.ResClass);
        }
    }

    static bool TryGetExport<T>(string library, string name, [NotNullWhen(true)] out T? function) where T : Delegate
    {
        function = null;
        if (!NativeLibrary.TryLoad(library, out var handle) || !NativeLibrary.TryGetExport(handle, name, out var address))
            return false;

        function = Marshal.GetDelegateForFunctionPointer<T>(address);
        return true;
    }

    [DllImport(GtkLibrary)]
    static extern IntPtr gtk_native_get_surface(IntPtr native);

    [DllImport(GtkLibrary)]
    static extern IntPtr gdk_surface_get_display(IntPtr surface);

    [DllImport(GObjectLibrary)]
    static extern nuint g_type_from_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(GObjectLibrary)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool g_type_check_instance_is_a(IntPtr instance, nuint type);

    [DllImport(X11Library)]
    static extern int XSetClassHint(IntPtr display, nuint window, ref XClassHint hint);

    [DllImport(X11Library)]
    static extern int XFlush(IntPtr display);

    [DllImport(X11Library)]
    static extern int XGetClassHint(IntPtr display, nuint window, ref XClassHint hint);

    [DllImport(X11Library)]
    static extern int XFree(IntPtr data);

    [DllImport("libgio-2.0.so.0")]
    static extern IntPtr g_application_get_application_id(IntPtr application);

    [DllImport("libglib-2.0.so.0")]
    static extern IntPtr g_get_prgname();
}

/// <summary>
/// <see cref="IAppActions"/> for Linux desktops: actions are published as
/// <c>[Desktop Action]</c> groups of a desktop entry and routed back to the
/// running process over the D-Bus session bus.
/// </summary>
public sealed partial class LinuxAppActions : IAppActions, IDisposable
{
    readonly ILinuxAppActionsHost _host;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly string _storePath = Path.Combine(GetDataHome(), GetApplicationId(), "appactions.json");

    IReadOnlyList<AppAction> _actions = Array.Empty<AppAction>();
    Registration? _current;

    /// <remarks>
    /// The actions are saved and published again when the app starts, like the persistent
    /// shortcuts of Android and iOS: the MAUI GTK4 backend rewrites the desktop entry at every
    /// startup, which removes them. Create this instance at startup (on the UI thread) so the
    /// actions are back as soon as the app runs.
    /// </remarks>
    public LinuxAppActions(ILinuxAppActionsHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        var saved = LoadActions();
        if (saved.Count > 0 && IsSupported)
        {
            _actions = saved;
            _ = RestoreAsync(saved);
        }
    }

    async Task RestoreAsync(IReadOnlyList<AppAction> actions)
    {
        try
        {
            // Waits for the main window (and so for the backend to have rewritten its entry).
            await SetAsync(actions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LinuxAppActions] restoring the saved actions failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public event EventHandler<AppActionEventArgs>? AppActionActivated;

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsLinux() && DBusAddress.Session is not null;

    /// <inheritdoc />
    public Task<IEnumerable<AppAction>> GetAsync() => Task.FromResult<IEnumerable<AppAction>>(_actions);

    /// <inheritdoc />
    public async Task SetAsync(IEnumerable<AppAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        if (!OperatingSystem.IsLinux() || !IsSupported)
            throw new FeatureNotSupportedException("App actions require Linux and a D-Bus session bus.");

        var list = actions.ToList();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Remove the previous registration (desktop entry + bus name),
            // otherwise every call leaves a stale launcher entry behind.
            _current?.Dispose();
            _current = null;
            _actions = Array.Empty<AppAction>();

            var registration = new Registration(_host, list, OnActivated);
            try
            {
                await registration.InitializeAsync().ConfigureAwait(false);
            }
            catch
            {
                registration.Dispose();
                throw;
            }

            _current = registration;
            _actions = list;
            SaveActions(list);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ----- Persistence -----

    sealed record SavedAction(string Id, string Title, string? Subtitle, string? Icon);

    [System.Text.Json.Serialization.JsonSerializable(typeof(List<SavedAction>))]
    sealed partial class SavedActionsJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }

    List<AppAction> LoadActions()
    {
        try
        {
            if (!File.Exists(_storePath))
                return new List<AppAction>();

            var saved = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(_storePath), SavedActionsJsonContext.Default.ListSavedAction);
            return saved?.Select(a => new AppAction(a.Id, a.Title, a.Subtitle, a.Icon)).ToList() ?? new List<AppAction>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new List<AppAction>();
        }
    }

    void SaveActions(IReadOnlyList<AppAction> actions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var saved = actions.Select(a => new SavedAction(a.Id, a.Title, a.Subtitle, string.Empty)).ToList();
            var temp = _storePath + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(saved, SavedActionsJsonContext.Default.ListSavedAction));
            File.Move(temp, _storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[LinuxAppActions] saving the actions failed: {ex.Message}");
        }
    }

    static string GetDataHome()
    {
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome))
            return xdgDataHome;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    // <ApplicationId> (MauiApplicationId metadata generated by the MAUI GTK4 backend), else the assembly name.
    static string GetApplicationId()
    {
        var entry = Assembly.GetEntryAssembly();
        var id = entry?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MauiApplicationId" && !string.IsNullOrWhiteSpace(a.Value))?.Value
            ?? entry?.GetName().Name
            ?? "maui-app";

        return Regex.Replace(id, @"[^A-Za-z0-9._-]+", "-");
    }

    /// <summary>
    /// Removes the desktop entry and releases the D-Bus name.
    /// </summary>
    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
    }

    // Reads the event at invocation time, so handlers subscribed after SetAsync are notified.
    void OnActivated(AppAction action) =>
        AppActionActivated?.Invoke(this, new AppActionEventArgs(action));

    /// <summary>
    /// One published set of actions: D-Bus object + bus name + desktop entry.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed class Registration : IPathMethodHandler, IDisposable
    {
        const string InterfaceName = "com.essentials.AppActions";
        const string MethodName = "OnAppAction";
        // Written in the [Desktop Entry] group of every desktop file touched by app actions.
        const string MarkerKey = "X-Essentials-AppActions";
        const string IdsKey = "X-Essentials-AppActions-Ids";
        const string ModeGenerated = "generated"; // files of older versions, one per registration: removed

        // Exec prefix carrying the registration id (see UpdateMainGroup).
        const string ExecRevisionPrefix = "env MAUI_APP_ACTIONS_REVISION=";

        static ReadOnlySpan<byte> InterfaceNameUtf8 => "com.essentials.AppActions"u8;
        static ReadOnlySpan<byte> MethodNameUtf8 => "OnAppAction"u8;

        // Lets `gdbus call` (and D-Feet, busctl...) discover the method signature.
        static readonly ReadOnlyMemory<byte> s_introspectionXml =
            """
            <interface name="com.essentials.AppActions">
              <method name="OnAppAction">
                <arg direction="in" type="s" name="id"/>
              </method>
            </interface>

            """u8.ToArray();

        readonly record struct SanitizedAction(string Id, AppAction Action);

        readonly ILinuxAppActionsHost _host;
        readonly IReadOnlyList<AppAction> _actions;
        readonly Action<AppAction> _onActivated;

        // Unique per registration: avoids collisions between instances and registrations.
        readonly string _uid = Guid.NewGuid().ToString("N");

        IReadOnlyList<SanitizedAction> _sanitizedActions = Array.Empty<SanitizedAction>();
        DBusConnection? _connection;
        string? _desktopFile;
        int _disposed;

        public Registration(ILinuxAppActionsHost host, IReadOnlyList<AppAction> actions, Action<AppAction> onActivated)
        {
            _host = host;
            _actions = actions;
            _onActivated = onActivated;
        }

        bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        string ObjectPath => "/com/essentials/AppActions/" + _uid;
        string BusName => InterfaceName + _uid;

        // IPathMethodHandler
        public string Path => ObjectPath;
        public bool HandlesChildPaths => false;

        public async Task InitializeAsync()
        {
            // Hook cleanup first so a partial initialization is still cleaned up.
            // ProcessExit is raised on normal exit and on SIGTERM, for any UI toolkit.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            var exec = BuildLaunchCommand();
            var identity = await _host.PrepareAsync(_uid).ConfigureAwait(false);

            _sanitizedActions = _actions
                .Select((a, i) => new SanitizedAction(CreateActionId(a.Title, i), a))
                .ToList();

            // Expose the D-Bus object before publishing the desktop entry,
            // so actions are reachable as soon as they appear in the shell.
            await RegisterDBusServiceAsync().ConfigureAwait(false);
            await WriteDesktopFileAsync(exec, identity).ConfigureAwait(false);

            // Not awaited: the host may need a few seconds for the shell to catch up.
            _ = NotifyPublishedAsync(identity);
        }

        async Task NotifyPublishedAsync(LinuxDesktopIdentity identity)
        {
            try
            {
                await _host.DesktopEntryPublishedAsync(identity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxAppActions] DesktopEntryPublishedAsync failed: {ex.Message}");
            }
        }

        async Task RegisterDBusServiceAsync()
        {
            var address = DBusAddress.Session
                ?? throw new InvalidOperationException("D-Bus session bus is not available (DBUS_SESSION_BUS_ADDRESS is not set)");

            _connection = new DBusConnection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            _connection.AddMethodHandler(this);
            await _connection.RequestNameAsync(BusName, RequestNameOptions.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Called on the D-Bus connection thread for every method call on <see cref="Path"/>.
        /// Calls that are not answered here get an UnknownMethod error when the context is disposed.
        /// </summary>
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(new[] { s_introspectionXml });
                return default;
            }

            var request = context.Request;
            if (request.Member.SequenceEqual(MethodNameUtf8)
                && (!request.InterfaceIsSet || request.Interface.SequenceEqual(InterfaceNameUtf8))
                && request.Signature.SequenceEqual("s"u8))
            {
                var reader = request.GetBodyReader();
                var id = reader.ReadString();

                Activate(id);

                if (!context.NoReplyExpected)
                {
                    using var writer = context.CreateReplyWriter(null);
                    context.Reply(writer.CreateMessage());
                }
            }

            return default;
        }

        void Activate(string id)
        {
            if (IsDisposed)
                return;

            foreach (var entry in _sanitizedActions)
            {
                if (entry.Id == id)
                {
                    var action = entry.Action;
                    _host.BeginInvokeOnMainThread(() => _onActivated(action));
                    return;
                }
            }
        }

        /// <summary>
        /// Updates the application's single desktop entry: StartupWMClass and actions.
        /// </summary>
        /// <remarks>
        /// There is always exactly one entry, named by the host (with the MAUI GTK4 backend: the
        /// &lt;app id&gt;.desktop it writes at startup). It is updated in place, never duplicated,
        /// and kept when the app exits. Keys written by others (Name, Icon...) are preserved.
        /// </remarks>
        async Task WriteDesktopFileAsync(string exec, LinuxDesktopIdentity identity)
        {
            var name = Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Unable to determine application name from entry assembly");

            var directory = System.IO.Path.Combine(GetDataHome(), "applications");
            Directory.CreateDirectory(directory);

            var desktopId = Regex.Replace(identity.DesktopFileId, @"[^A-Za-z0-9._-]+", "-");
            var path = System.IO.Path.Combine(directory, desktopId + ".desktop");

            RemoveGeneratedEntries(directory, path);

            var exists = File.Exists(path);
            var lines = exists
                ? RemoveAppActions(File.ReadAllLines(path)).Lines
                : NewEntry(name, exec, identity);

            var content = new StringBuilder();
            foreach (var line in UpdateMainGroup(lines, identity))
                content.Append(line).Append('\n');

            foreach (var group in BuildActionGroups())
                content.Append('\n').Append(group);

            var fileMode = exists
                ? File.GetUnixFileMode(path)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute; // some desktops only trust executable launchers

            await WriteAtomicallyAsync(path, content.ToString(), fileMode).ConfigureAwait(false);
            _desktopFile = path;
        }

        static List<string> NewEntry(string name, string exec, LinuxDesktopIdentity identity)
        {
            var lines = new List<string>
            {
                "[Desktop Entry]",
                "Type=Application",
                "Name=" + EscapeDesktopValue(name),
                "Exec=" + EscapeDesktopValue(exec),
                "Categories=Utility;",
                "StartupNotify=true",
            };

            if (!string.IsNullOrWhiteSpace(identity.Icon))
                lines.Add("Icon=" + EscapeDesktopValue(identity.Icon));

            return lines;
        }

        /// <summary>
        /// Sets StartupWMClass (and X-GNOME-WMClass when present), the actions list and
        /// the registration revision in the [Desktop Entry] group.
        /// </summary>
        /// <remarks>
        /// GNOME Shell reloads an application only when its Name, Exec, Icon or Comment change,
        /// never for StartupWMClass or Actions: it would keep showing the old actions. The Exec
        /// line therefore carries the registration id in a harmless environment variable
        /// (<c>env MAUI_APP_ACTIONS_REVISION=&lt;id&gt; …</c>), which makes GNOME reload the entry.
        /// </remarks>
        List<string> UpdateMainGroup(List<string> lines, LinuxDesktopIdentity identity)
        {
            var wmClass = string.IsNullOrWhiteSpace(identity.StartupWmClass) ? null : EscapeDesktopValue(identity.StartupWmClass);
            var ourIds = _sanitizedActions.Select(a => a.Id).ToList();

            var result = new List<string>();
            var inMainGroup = false;
            var mainGroupEnd = -1;
            var hasStartupWmClass = false;
            var hasActions = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    if (inMainGroup)
                        mainGroupEnd = result.Count;
                    inMainGroup = trimmed == "[Desktop Entry]";
                    result.Add(line);
                    continue;
                }

                if (inMainGroup)
                {
                    if (TryGetValue(trimmed, "StartupWMClass", out _))
                    {
                        hasStartupWmClass = true;
                        if (wmClass is not null)
                            result.Add("StartupWMClass=" + wmClass);
                        continue;
                    }

                    if (TryGetValue(trimmed, "X-GNOME-WMClass", out _))
                    {
                        if (wmClass is not null)
                            result.Add("X-GNOME-WMClass=" + wmClass);
                        continue;
                    }

                    if (TryGetValue(trimmed, "Exec", out var currentExec))
                    {
                        result.Add($"Exec={ExecRevisionPrefix}{_uid} {StripExecRevision(currentExec)}");
                        continue;
                    }

                    // Existing actions of the entry are kept, ours are appended.
                    if (TryGetValue(trimmed, "Actions", out var ownActions))
                    {
                        hasActions = true;
                        var all = SplitList(ownActions).Concat(ourIds).ToList();
                        if (all.Count > 0)
                            result.Add("Actions=" + string.Join(';', all) + ";");
                        continue;
                    }
                }

                result.Add(line);
            }

            if (inMainGroup)
                mainGroupEnd = result.Count;

            // New keys go at the end of [Desktop Entry], before the blank lines separating the next group.
            while (mainGroupEnd > 0 && result[mainGroupEnd - 1].Trim().Length == 0)
                mainGroupEnd--;

            var added = new List<string>();
            if (!hasStartupWmClass && wmClass is not null)
                added.Add("StartupWMClass=" + wmClass);
            added.Add($"{MarkerKey}=true");
            if (ourIds.Count > 0)
            {
                added.Add($"{IdsKey}={string.Join(';', ourIds)};");
                if (!hasActions)
                    added.Add($"Actions={string.Join(';', ourIds)};");
            }

            result.InsertRange(Math.Max(mainGroupEnd, 0), added);
            return result;
        }

        static string StripExecRevision(string exec)
        {
            if (!exec.StartsWith(ExecRevisionPrefix, StringComparison.Ordinal))
                return exec;

            var space = exec.IndexOf(' ', ExecRevisionPrefix.Length);
            return space < 0 ? exec : exec[(space + 1)..];
        }

        /// <summary>One [Desktop Action] group per action.</summary>
        List<string> BuildActionGroups()
        {
            var groups = new List<string>();

            foreach (var entry in _sanitizedActions)
            {
                // gdbus parses arguments as GVariant text: a string must be single-quoted.
                // Ids only contain [a-z0-9-], so no further escaping is required.
                var actionExec =
                    $"gdbus call --session --dest {BusName} --object-path {ObjectPath} " +
                    $"--method {InterfaceName}.{MethodName} {QuoteExecArg($"'{entry.Id}'")}";

                groups.Add(new StringBuilder()
                    .Append("[Desktop Action ").Append(entry.Id).Append("]\n")
                    .Append("Name=").Append(EscapeDesktopValue(entry.Action.Title ?? entry.Id)).Append('\n')
                    .Append("Exec=").Append(EscapeDesktopValue(actionExec)).Append('\n')
                    .ToString());
            }

            return groups;
        }

        /// <summary>
        /// Deletes the per-registration entries written by older versions (one file per SetAsync),
        /// so only the application's entry remains.
        /// </summary>
        static void RemoveGeneratedEntries(string directory, string keep)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.desktop"))
            {
                if (file == keep)
                    continue;

                try
                {
                    if (File.ReadLines(file).Take(64).Any(l => l.Trim() == $"{MarkerKey}={ModeGenerated}"))
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// Removes the actions and keys app actions added to a desktop entry.
        /// Returns the remaining lines and whether the file had been touched by app actions.
        /// </summary>
        static (List<string> Lines, string? Mode) RemoveAppActions(IEnumerable<string> lines)
        {
            var all = lines.ToList();
            string? mode = null;
            var ourIds = new HashSet<string>(StringComparer.Ordinal);

            var inMainGroup = false;
            foreach (var line in all)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                    inMainGroup = trimmed == "[Desktop Entry]";
                else if (inMainGroup && TryGetValue(trimmed, MarkerKey, out var value))
                    mode = value;
                else if (inMainGroup && TryGetValue(trimmed, IdsKey, out var ids))
                    ourIds.UnionWith(SplitList(ids));
            }

            if (mode is null)
                return (all, null);

            var result = new List<string>();
            var skipGroup = false;
            inMainGroup = false;

            foreach (var line in all)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    inMainGroup = trimmed == "[Desktop Entry]";
                    skipGroup = trimmed.StartsWith("[Desktop Action ", StringComparison.Ordinal)
                        && ourIds.Contains(trimmed["[Desktop Action ".Length..^1]);

                    // Drop the blank line that separated a removed group.
                    if (skipGroup && result.Count > 0 && result[^1].Trim().Length == 0)
                        result.RemoveAt(result.Count - 1);
                }

                if (skipGroup)
                    continue;

                if (inMainGroup)
                {
                    if (TryGetValue(trimmed, MarkerKey, out _) || TryGetValue(trimmed, IdsKey, out _))
                        continue;

                    if (TryGetValue(trimmed, "Actions", out var actions))
                    {
                        var remaining = SplitList(actions).Where(id => !ourIds.Contains(id)).ToList();
                        if (remaining.Count > 0)
                            result.Add("Actions=" + string.Join(';', remaining) + ";");
                        continue;
                    }
                }

                result.Add(line);
            }

            // Trailing blank lines left by removed groups.
            while (result.Count > 0 && result[^1].Trim().Length == 0)
                result.RemoveAt(result.Count - 1);

            return (result, mode);
        }

        static bool TryGetValue(string line, string key, out string value)
        {
            value = string.Empty;
            if (!line.StartsWith(key, StringComparison.Ordinal))
                return false;

            var rest = line.AsSpan(key.Length).TrimStart();
            if (rest.Length == 0 || rest[0] != '=')
                return false;

            value = rest[1..].Trim().ToString();
            return true;
        }

        static IEnumerable<string> SplitList(string value) =>
            value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        async Task WriteAtomicallyAsync(string path, string content, UnixFileMode mode)
        {
            // Write then rename, so the shell's file monitor never reads a partial file.
            var temp = path + "." + _uid + ".tmp";
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false)).ConfigureAwait(false);
            File.SetUnixFileMode(temp, mode);
            File.Move(temp, path, overwrite: true);
        }

        void OnProcessExit(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            // The desktop entry is kept: it is the application's entry, updated by the next SetAsync.

            // Closing the connection also releases the bus name.
            _connection?.Dispose();
            _connection = null;
        }

        /// <summary>
        /// Command line used by the launcher to start the application.
        /// Handles apphost / self-contained / single-file executables
        /// and framework-dependent apps started with "dotnet App.dll".
        /// </summary>
        static string BuildLaunchCommand()
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to determine the current process path");

            if (System.IO.Path.GetFileNameWithoutExtension(processPath) == "dotnet")
            {
#pragma warning disable IL3000 // Only reached when running through the dotnet muxer, never single-file
                var entryAssembly = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
                if (string.IsNullOrEmpty(entryAssembly))
                    throw new InvalidOperationException("Unable to determine the entry assembly path");

                return $"{QuoteExecArg(processPath)} {QuoteExecArg(entryAssembly)}";
            }

            return QuoteExecArg(processPath);
        }

        static string GetDataHome()
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome) && System.IO.Path.IsPathRooted(xdgDataHome))
                return xdgDataHome;

            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        static string CreateActionId(string? title, int index)
        {
            // Stable sanitized ID, valid as a Desktop Action identifier ([A-Za-z0-9-])
            var sanitized = Regex.Replace((title ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", "-")
                .Trim('-');

            if (string.IsNullOrWhiteSpace(sanitized))
                sanitized = "action";

            return $"{sanitized}-{index}";
        }

        /// <summary>
        /// Quotes one argument of a desktop entry Exec key
        /// (Desktop Entry Specification, "The Exec key").
        /// </summary>
        static string QuoteExecArg(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                if (c is '"' or '`' or '$' or '\\')
                    sb.Append('\\');
                sb.Append(c);
            }

            // '%' introduces field codes and must be doubled
            return sb.Append('"').ToString().Replace("%", "%%");
        }

        /// <summary>
        /// Escapes a value of type string/localestring in a desktop entry.
        /// Applied to Exec after quoting, as required by the specification.
        /// </summary>
        static string EscapeDesktopValue(string value) =>
            value.Replace("\\", "\\\\")
                 .Replace("\n", "\\n")
                 .Replace("\r", "\\r")
                 .Replace("\t", "\\t");
    }
}