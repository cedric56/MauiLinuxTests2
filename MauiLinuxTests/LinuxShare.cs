using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Platforms;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.DataTransfer;

/// <summary>
/// Share on Linux. There is no system share sheet, so the share targets available
/// on this machine are discovered and offered in a chooser dialog
/// (kdialog on KDE, zenity on GNOME/Ubuntu and other desktops):
/// <list type="bullet">
/// <item>Email: xdg-desktop-portal Email portal, or xdg-email.</item>
/// <item>Phones via KDE Connect (<c>kdeconnect-cli</c>) or GSConnect (GNOME Shell, D-Bus).</item>
/// <item>Paired Bluetooth devices supporting Object Push, via <c>bluedevil-sendfile</c> (KDE)
/// or <c>bluetooth-sendto</c> (GNOME).</item>
/// <item>"Open with…": the xdg-desktop-portal OpenURI application chooser (one link or one file).</item>
/// </list>
/// </summary>
/// <remarks>No UI toolkit dependency: works for both the Gtk4 and the Avalonia backends.</remarks>
public class LinuxShare : IShare
{
    const string EmailInterface = "org.freedesktop.portal.Email";
    const string OpenUriInterface = "org.freedesktop.portal.OpenURI";

    const string GSConnectBusName = "org.gnome.Shell.Extensions.GSConnect";
    const string GSConnectObjectPath = "/org/gnome/Shell/Extensions/GSConnect";
    const string GSConnectDeviceInterface = "org.gnome.Shell.Extensions.GSConnect.Device";

    const string BluezBusName = "org.bluez";
    const string BluezDeviceInterface = "org.bluez.Device1";
    const string ObexObjectPushUuid = "00001105-0000-1000-8000-00805f9b34fb";

    // The Email portal answers once the mail client is launched, not when the mail is sent.
    static readonly TimeSpan s_emailResponseTimeout = TimeSpan.FromSeconds(30);
    // "Open with…" waits for the user to pick an application.
    static readonly TimeSpan s_openWithResponseTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan s_discoveryTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan s_sendTimeout = TimeSpan.FromSeconds(30);

    readonly Func<string>? _parentWindow;

    /// <param name="parentWindow">
    /// Optional provider of the portal parent window identifier
    /// ("x11:&lt;XID in hex&gt;" or "wayland:&lt;exported handle&gt;"), so the
    /// portal dialogs are placed over the app window. Empty when not provided.
    /// </param>
    public LinuxShare(Func<string>? parentWindow = null)
    {
        _parentWindow = parentWindow;
    }

    static Desktop CurrentDesktop => DesktopImplementation.Current;
    static bool IsKdeLike => CurrentDesktop == Desktop.KDE || CurrentDesktop == Desktop.LXQt;

    // ----- IShare -----

    /// <inheritdoc />
    public Task RequestAsync(ShareTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Uri))
            throw new ArgumentException("A text or an URI is required", nameof(request));

        var subject = !string.IsNullOrWhiteSpace(request.Subject) ? request.Subject : request.Title;
        return ShareAsync(new ShareContent(request.Title, subject, Blank(request.Text), Blank(request.Uri), Array.Empty<string>()));
    }

    /// <inheritdoc />
    public Task RequestAsync(ShareFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.File, nameof(request.File));

        return ShareAsync(new ShareContent(request.Title, request.Title, null, null, new[] { request.File.FullPath }));
    }

    /// <inheritdoc />
    public Task RequestAsync(ShareMultipleFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var files = (request.Files ?? Enumerable.Empty<ShareFile>()).Select(f => f.FullPath).ToArray();
        if (files.Length == 0)
            throw new ArgumentException("At least one file is required", nameof(request));

        return ShareAsync(new ShareContent(request.Title, request.Title, null, null, files));
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // ----- Share flow -----

    sealed record ShareContent(string? Title, string? Subject, string? Text, string? Uri, IReadOnlyList<string> Files)
    {
        public bool HasFiles => Files.Count > 0;

        /// <summary>Only a link: share it as a link rather than as text.</summary>
        public bool IsLinkOnly => !HasFiles && Text is null && Uri is not null;

        /// <summary>Text then link, like the other platforms.</summary>
        public string? Body => Text is null ? Uri : Uri is null ? Text : $"{Text}\n{Uri}";
    }

    sealed record ShareTarget(string Id, string Label, Func<ShareContent, Task> SendAsync);

    async Task ShareAsync(ShareContent content)
    {
        foreach (var file in content.Files)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException("The file to share doesn't exist", file);
        }

        var targets = await DiscoverTargetsAsync(content).ConfigureAwait(false);

        var target = await ChooseTargetAsync(content.Title ?? "Share", targets).ConfigureAwait(false);
        if (target is null)
            return; // like the other platforms: dismissing the share UI isn't an error

        await target.SendAsync(content).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the share targets available on this machine. Email comes first:
    /// it is used directly when no chooser dialog is installed.
    /// </summary>
    async Task<IReadOnlyList<ShareTarget>> DiscoverTargetsAsync(ShareContent content)
    {
        var targets = new List<ShareTarget>
        {
            new("email", "Email", ComposeEmailAsync),
        };

        // Discovery runs in parallel; each source is optional and bounded.
        var kdeConnect = DiscoverKdeConnectAsync();
        var gsConnect = DiscoverGSConnectAsync();
        var bluetooth = content.HasFiles ? DiscoverBluetoothAsync() : Task.FromResult<IReadOnlyList<ShareTarget>>(Array.Empty<ShareTarget>());

        targets.AddRange(await kdeConnect.ConfigureAwait(false));
        targets.AddRange(await gsConnect.ConfigureAwait(false));
        targets.AddRange(await bluetooth.ConfigureAwait(false));

        // The portal application chooser opens one link or one file.
        if (XdgDesktopPortal.IsSessionBusAvailable && (content.IsLinkOnly || content.Files.Count == 1))
            targets.Add(new("open-with", "Open with…", OpenWithAsync));

        return targets;
    }

    // ----- Chooser -----

    /// <summary>
    /// Asks which target to use, with the desktop's own dialog tool.
    /// Returns null when the user cancels.
    /// </summary>
    static async Task<ShareTarget?> ChooseTargetAsync(string title, IReadOnlyList<ShareTarget> targets)
    {
        if (targets.Count == 1)
            return targets[0];

        // KDE ships kdialog, GNOME/Ubuntu ship zenity; use the other one if missing.
        var choosers = IsKdeLike
            ? new[] { "kdialog", "zenity" }
            : new[] { "zenity", "kdialog" };

        foreach (var chooser in choosers)
        {
            if (!Tools.Exists(chooser))
                continue;

            var args = chooser == "kdialog"
                ? KdialogMenuArguments(title, targets)
                : ZenityListArguments(title, targets);

            var result = await Tools.RunAsync(chooser, args, timeout: null).ConfigureAwait(false);
            if (result is null)
                continue;

            if (result.ExitCode != 0)
                return null; // cancelled or closed

            var id = result.Output.Trim();
            return targets.FirstOrDefault(t => t.Id == id);
        }

        Debug.WriteLine($"[LinuxShare] no chooser dialog found ({Tools.InstallHint(IsKdeLike ? "kdialog" : "zenity")}), using email");
        return targets[0];
    }

    static IEnumerable<string> KdialogMenuArguments(string title, IReadOnlyList<ShareTarget> targets)
    {
        yield return "--title";
        yield return title;
        yield return "--menu";
        yield return "Share with:";
        foreach (var target in targets)
        {
            yield return target.Id;
            yield return target.Label;
        }
    }

    static IEnumerable<string> ZenityListArguments(string title, IReadOnlyList<ShareTarget> targets)
    {
        foreach (var arg in new[]
        {
            "--list", "--title", title, "--text", "Share with:",
            "--column", "Id", "--column", "Target",
            "--hide-column", "1", "--print-column", "1", "--hide-header",
            "--width", "420", "--height", "360",
        })
        {
            yield return arg;
        }

        foreach (var target in targets)
        {
            yield return target.Id;
            yield return target.Label;
        }
    }

    // ----- Email -----

    async Task ComposeEmailAsync(ShareContent content)
    {
        if (XdgDesktopPortal.IsSessionBusAvailable)
        {
            try
            {
                await ComposeEmailWithPortalAsync(content).ConfigureAwait(false);
                return;
            }
            catch (DBusErrorReplyException ex) when (XdgDesktopPortal.IsPortalMissing(ex))
            {
                Debug.WriteLine($"[LinuxShare] Email portal not available, using xdg-email: {ex.Message}");
            }
            catch (DBusConnectionException ex)
            {
                Debug.WriteLine($"[LinuxShare] session bus not reachable, using xdg-email: {ex.Message}");
            }
        }

        await ComposeEmailWithXdgEmailAsync(content).ConfigureAwait(false);
    }

    async Task ComposeEmailWithPortalAsync(ShareContent content)
    {
        var token = XdgDesktopPortal.NewHandleToken();
        var parentWindow = _parentWindow?.Invoke() ?? string.Empty;

        var response = await XdgDesktopPortal.RequestAsync(
            token,
            connection =>
            {
                // The file descriptors are owned by the message and closed once it is sent.
                var handles = content.Files.Select(f => File.OpenHandle(f, FileMode.Open, FileAccess.Read)).ToList();
                try
                {
                    return CreateComposeEmailMessage(connection, parentWindow, token, content.Subject, content.Body, handles);
                }
                catch
                {
                    handles.ForEach(h => h.Dispose());
                    throw;
                }
            },
            s_emailResponseTimeout).ConfigureAwait(false);

        // null: no Response in time, or a portal too old to report it.
        if (response is null or XdgDesktopPortal.ResponseSuccess or XdgDesktopPortal.ResponseCancelled)
            return;

        throw new InvalidOperationException("The email client could not be launched by xdg-desktop-portal (is an email client installed?).");
    }

    // ComposeEmail(s parent_window, a{sv} options) -> o handle
    static MessageBuffer CreateComposeEmailMessage(
        DBusConnection connection,
        string parentWindow,
        string token,
        string? subject,
        string? body,
        IReadOnlyList<SafeFileHandle> attachments)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: XdgDesktopPortal.BusName,
                path: XdgDesktopPortal.ObjectPath,
                @interface: EmailInterface,
                member: "ComposeEmail",
                signature: "sa{sv}");

            writer.WriteString(parentWindow);

            var options = writer.WriteDictionaryStart();
            WriteStringOption(ref writer, "handle_token", token);
            WriteStringOption(ref writer, "subject", subject);
            WriteStringOption(ref writer, "body", body);
            WriteStringOption(ref writer, "activation_token", Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN"));

            if (attachments.Count > 0)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("attachment_fds");
                writer.WriteSignature("ah");
                var fds = writer.WriteArrayStart(DBusType.UnixFd);
                foreach (var handle in attachments)
                    writer.WriteHandle(handle);
                writer.WriteArrayEnd(fds);
            }

            writer.WriteDictionaryEnd(options);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    static async Task ComposeEmailWithXdgEmailAsync(ShareContent content)
    {
        var args = new List<string>();

        if (content.Subject is not null)
            args.AddRange(new[] { "--subject", content.Subject });

        if (content.Body is not null)
            args.AddRange(new[] { "--body", content.Body });

        foreach (var file in content.Files)
            args.AddRange(new[] { "--attach", Path.GetFullPath(file) });

        // xdg-email exits once the mail client is launched; 3 = tool not found, 4 = action failed.
        var result = await Tools.RunAsync("xdg-email", args, s_sendTimeout).ConfigureAwait(false)
            ?? throw new FeatureNotSupportedException($"Sharing by email requires xdg-desktop-portal or xdg-email ({Tools.InstallHint("xdg-utils")}).");

        result.ThrowIfFailed("xdg-email");
    }

    // ----- Open with… (OpenURI portal) -----

    async Task OpenWithAsync(ShareContent content)
    {
        var token = XdgDesktopPortal.NewHandleToken();
        var parentWindow = _parentWindow?.Invoke() ?? string.Empty;

        var response = await XdgDesktopPortal.RequestAsync(
            token,
            connection => content.HasFiles
                ? CreateOpenFileMessage(connection, parentWindow, token, content.Files[0])
                : CreateOpenUriMessage(connection, parentWindow, token, content.Uri!),
            s_openWithResponseTimeout).ConfigureAwait(false);

        if (response == XdgDesktopPortal.ResponseOther)
            throw new InvalidOperationException("No application could open the shared content.");
    }

    // OpenURI(s parent_window, s uri, a{sv} options) -> o handle
    static MessageBuffer CreateOpenUriMessage(DBusConnection connection, string parentWindow, string token, string uri)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: XdgDesktopPortal.BusName,
                path: XdgDesktopPortal.ObjectPath,
                @interface: OpenUriInterface,
                member: "OpenURI",
                signature: "ssa{sv}");

            writer.WriteString(parentWindow);
            writer.WriteString(uri);
            WriteOpenOptions(ref writer, token);

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // OpenFile(s parent_window, h fd, a{sv} options) -> o handle
    static MessageBuffer CreateOpenFileMessage(DBusConnection connection, string parentWindow, string token, string file)
    {
        var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read);
        try
        {
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(
                    destination: XdgDesktopPortal.BusName,
                    path: XdgDesktopPortal.ObjectPath,
                    @interface: OpenUriInterface,
                    member: "OpenFile",
                    signature: "sha{sv}");

                writer.WriteString(parentWindow);
                writer.WriteHandle(handle); // owned by the message from here
                WriteOpenOptions(ref writer, token);

                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    static void WriteOpenOptions(ref MessageWriter writer, string token)
    {
        var options = writer.WriteDictionaryStart();
        WriteStringOption(ref writer, "handle_token", token);
        WriteStringOption(ref writer, "activation_token", Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN"));

        // Always show the application chooser.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("ask");
        writer.WriteVariantBool(true);

        writer.WriteDictionaryEnd(options);
    }

    static void WriteStringOption(ref MessageWriter writer, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    // ----- KDE Connect (kdeconnect-cli) -----

    static async Task<IReadOnlyList<ShareTarget>> DiscoverKdeConnectAsync()
    {
        if (!Tools.Exists("kdeconnect-cli"))
            return Array.Empty<ShareTarget>();

        // One "<id> <name>" line per paired and reachable device.
        var result = await Tools.RunAsync("kdeconnect-cli", new[] { "--list-available", "--id-name-only" }, s_discoveryTimeout).ConfigureAwait(false);
        if (result is null || result.ExitCode != 0)
            return Array.Empty<ShareTarget>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new ShareTarget(
                "kdeconnect:" + parts[0],
                $"{parts[1]} (KDE Connect)",
                content => ShareWithKdeConnectAsync(parts[0], content)))
            .ToList();
    }

    static async Task ShareWithKdeConnectAsync(string deviceId, ShareContent content)
    {
        var args = new List<string> { "--device", deviceId };

        if (content.HasFiles)
        {
            // --share takes the first file, the others are positional arguments.
            args.Add("--share");
            args.AddRange(content.Files.Select(Path.GetFullPath));
        }
        else if (content.IsLinkOnly)
        {
            args.AddRange(new[] { "--share", content.Uri! });
        }
        else
        {
            args.AddRange(new[] { "--share-text", content.Body! });
        }

        var result = await Tools.RunAsync("kdeconnect-cli", args, s_sendTimeout).ConfigureAwait(false)
            ?? throw new FeatureNotSupportedException("kdeconnect-cli is not available.");

        result.ThrowIfFailed("kdeconnect-cli");
    }

    // ----- GSConnect (GNOME Shell implementation of KDE Connect, D-Bus) -----

    static async Task<IReadOnlyList<ShareTarget>> DiscoverGSConnectAsync()
    {
        if (!XdgDesktopPortal.IsSessionBusAvailable)
            return Array.Empty<ShareTarget>();

        // NoAutoStart: only use GSConnect if it is already running.
        var objects = await DBusObjects.GetManagedObjectsAsync(
            DBusConnection.Session, GSConnectBusName, GSConnectObjectPath, GSConnectDeviceInterface, s_discoveryTimeout).ConfigureAwait(false);

        return objects
            .Where(o => o.Properties.GetBool("Connected") && o.Properties.GetBool("Paired"))
            .Select(o => new ShareTarget(
                "gsconnect:" + o.Path,
                $"{o.Properties.GetString("Name") ?? "Device"} (GSConnect)",
                content => ShareWithGSConnectAsync(o.Path, content)))
            .ToList();
    }

    static async Task ShareWithGSConnectAsync(string devicePath, ShareContent content)
    {
        var connection = DBusConnection.Session;

        if (content.HasFiles)
        {
            foreach (var file in content.Files)
                await ActivateAsync("shareFile", Path.GetFullPath(file)).ConfigureAwait(false);
        }
        else if (content.IsLinkOnly)
        {
            await ActivateAsync("shareUri", content.Uri!).ConfigureAwait(false);
        }
        else
        {
            await ActivateAsync("shareText", content.Body!).ConfigureAwait(false);
        }

        // org.gtk.Actions.Activate(s action_name, av parameter, a{sv} platform_data)
        Task ActivateAsync(string action, string value) =>
            connection.CallMethodAsync(CreateActivateMessage(connection, devicePath, action, value));
    }

    static MessageBuffer CreateActivateMessage(DBusConnection connection, string devicePath, string action, string value)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: GSConnectBusName,
            path: devicePath,
            @interface: "org.gtk.Actions",
            member: "Activate",
            signature: "sava{sv}");

        writer.WriteString(action);

        var parameters = writer.WriteArrayStart(DBusType.Variant);
        if (action == "shareFile")
        {
            // (s path, b open-when-received)
            writer.WriteSignature("(sb)");
            writer.WriteStructureStart();
            writer.WriteString(value);
            writer.WriteBool(false);
        }
        else
        {
            writer.WriteVariantString(value);
        }
        writer.WriteArrayEnd(parameters);

        var platformData = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(platformData);

        return writer.CreateMessage();
    }

    // ----- Bluetooth (BlueZ + bluedevil-sendfile / bluetooth-sendto) -----

    static async Task<IReadOnlyList<ShareTarget>> DiscoverBluetoothAsync()
    {
        // KDE: BlueDevil's wizard; GNOME/Ubuntu: gnome-bluetooth's sender. Either one works anywhere.
        var senders = IsKdeLike
            ? new[] { "bluedevil-sendfile", "bluetooth-sendto" }
            : new[] { "bluetooth-sendto", "bluedevil-sendfile" };

        var sender = senders.FirstOrDefault(Tools.Exists);
        if (sender is null || DBusAddress.System is null)
            return Array.Empty<ShareTarget>();

        var devices = await DBusObjects.GetManagedObjectsAsync(
            DBusConnection.System, BluezBusName, "/", BluezDeviceInterface, s_discoveryTimeout).ConfigureAwait(false);

        // Paired devices able to receive files (OBEX Object Push).
        return devices
            .Where(d => d.Properties.GetBool("Paired")
                     && d.Properties.GetStrings("UUIDs").Contains(ObexObjectPushUuid, StringComparer.OrdinalIgnoreCase))
            .Select(d => (Address: d.Properties.GetString("Address"), Name: d.Properties.GetString("Alias") ?? d.Properties.GetString("Name")))
            .Where(d => d.Address is not null)
            .Select(d => new ShareTarget(
                "bluetooth:" + d.Address,
                $"{d.Name ?? d.Address} (Bluetooth)",
                content => SendWithBluetoothAsync(sender, d.Address!, d.Name, content)))
            .ToList();
    }

    static async Task SendWithBluetoothAsync(string sender, string address, string? name, ShareContent content)
    {
        var args = new List<string>();

        if (sender == "bluedevil-sendfile")
        {
            args.AddRange(new[] { "--kio", "bluetooth://" + address });
            foreach (var file in content.Files)
                args.AddRange(new[] { "--files", Path.GetFullPath(file) });
        }
        else
        {
            args.Add("--device=" + address);
            if (name is not null)
                args.Add("--name=" + name);
            args.AddRange(content.Files.Select(Path.GetFullPath));
        }

        // Both tools show their own progress window and exit when the transfer ends.
        var result = await Tools.RunAsync(sender, args, timeout: null).ConfigureAwait(false)
            ?? throw new FeatureNotSupportedException($"{sender} is not available.");

        result.ThrowIfFailed(sender);
    }

    // ----- Helpers -----

    /// <summary>Reads org.freedesktop.DBus.ObjectManager.GetManagedObjects for one interface.</summary>
    static class DBusObjects
    {
        public sealed record ManagedObject(string Path, Dictionary<string, VariantValue> Properties);

        public static async Task<IReadOnlyList<ManagedObject>> GetManagedObjectsAsync(
            DBusConnection connection, string busName, string objectPath, string interfaceName, TimeSpan timeout)
        {
            try
            {
                var call = connection.CallMethodAsync(
                    CreateMessage(connection, busName, objectPath),
                    (Message m, object? state) => Read(m, (string)state!),
                    interfaceName);

                var completed = await Task.WhenAny(call, Task.Delay(timeout)).ConfigureAwait(false);
                return completed == call ? await call.ConfigureAwait(false) : Array.Empty<ManagedObject>();
            }
            catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException)
            {
                // Service not running / not installed
                Debug.WriteLine($"[LinuxShare] {busName} not available: {ex.Message}");
                return Array.Empty<ManagedObject>();
            }
        }

        static MessageBuffer CreateMessage(DBusConnection connection, string busName, string objectPath)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: busName,
                path: objectPath,
                @interface: "org.freedesktop.DBus.ObjectManager",
                member: "GetManagedObjects",
                flags: MessageFlags.NoAutoStart);
            return writer.CreateMessage();
        }

        // a{oa{sa{sv}}}
        static IReadOnlyList<ManagedObject> Read(Message message, string interfaceName)
        {
            var result = new List<ManagedObject>();
            var reader = message.GetBodyReader();

            var objects = reader.ReadDictionaryStart();
            while (reader.HasNext(objects))
            {
                var path = reader.ReadObjectPathAsString();

                var interfaces = reader.ReadDictionaryStart();
                while (reader.HasNext(interfaces))
                {
                    var name = reader.ReadString();
                    var properties = reader.ReadDictionaryOfStringToVariantValue();
                    if (name == interfaceName)
                        result.Add(new ManagedObject(path, properties));
                }
            }

            return result;
        }
    }

    /// <summary>External command line tools.</summary>
    static class Tools
    {
        public sealed record Result(int ExitCode, string Output, string Error)
        {
            public void ThrowIfFailed(string tool)
            {
                if (ExitCode != 0)
                    throw new InvalidOperationException($"{tool} failed ({ExitCode}): {Error.Trim()}");
            }
        }

        public static bool Exists(string tool)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
                return false;

            return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, tool)));
        }

        /// <summary>
        /// Runs a tool and captures its output. Returns null when the tool isn't installed.
        /// </summary>
        public static async Task<Result?> RunAsync(string tool, IEnumerable<string> args, TimeSpan? timeout)
        {
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception)
            {
                return null;
            }

            if (process is null)
                return null;

            using (process)
            {
                // Read both streams so a chatty child can't block on a full pipe.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    return new Result(-1, string.Empty, $"{tool} timed out");
                }

                return new Result(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
        }

        /// <summary>Install command for the current distribution, for error messages.</summary>
        public static string InstallHint(string package)
        {
            var distribution = DistributionImplementation.Current;

            if (distribution == Distribution.Ubuntu || distribution == Distribution.Debian ||
                distribution == Distribution.LinuxMint || distribution == Distribution.PopOS ||
                distribution == Distribution.ElementaryOS || distribution == Distribution.KaliLinux)
                return $"sudo apt install {package}";

            if (distribution == Distribution.Fedora || distribution == Distribution.RHEL ||
                distribution == Distribution.CentOS || distribution == Distribution.RockyLinux ||
                distribution == Distribution.AlmaLinux)
                return $"sudo dnf install {package}";

            if (distribution == Distribution.ArchLinux || distribution == Distribution.Manjaro)
                return $"sudo pacman -S {package}";

            if (distribution == Distribution.OpenSUSE)
                return $"sudo zypper install {package}";

            if (distribution == Distribution.Alpine)
                return $"sudo apk add {package}";

            return $"install the '{package}' package";
        }
    }
}

static class VariantValueExtensions
{
    public static bool GetBool(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.Bool && value.GetBool();

    public static string? GetString(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.String ? value.GetString() : null;

    public static string[] GetStrings(this Dictionary<string, VariantValue> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Type == VariantValueType.Array && value.ItemType == VariantValueType.String
            ? value.GetArray<string>()
            : Array.Empty<string>();
}