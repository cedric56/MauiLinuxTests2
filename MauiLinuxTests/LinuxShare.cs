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
/// <item>Phones via KDE Connect or GSConnect (GNOME Shell): their own device picker dialogs
/// (<c>kdeconnect-handler</c>, GSConnect's DeviceChooser) when they support the content,
/// one entry per device otherwise.</item>
/// <item>SMS through the phone (KDE Connect, GSConnect) for text, MMS with the files (KDE Connect).</item>
/// <item>Bluetooth: BlueDevil's send wizard (KDE), or one entry per paired device with
/// <c>bluetooth-sendto</c> (GNOME).</item>
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
        var kdeConnect = DiscoverKdeConnectAsync(content);
        var gsConnect = DiscoverGSConnectAsync(content);
        var bluetooth = content.HasFiles ? DiscoverBluetoothAsync() : Task.FromResult<IReadOnlyList<ShareTarget>>(Array.Empty<ShareTarget>());

        var sms = DiscoverSmsAsync(content);
        var phoneSetup = DiscoverPhoneSetupAsync();

        targets.AddRange(await kdeConnect.ConfigureAwait(false));
        targets.AddRange(await gsConnect.ConfigureAwait(false));
        targets.AddRange(await sms.ConfigureAwait(false));
        targets.AddRange(await phoneSetup.ConfigureAwait(false));
        targets.AddRange(await bluetooth.ConfigureAwait(false));

        // The portal application chooser opens one link or one file.
        if (XdgDesktopPortal.IsSessionBusAvailable && (content.IsLinkOnly || content.Files.Count == 1))
            targets.Add(new("open-with", "Open with…", OpenWithAsync));

        return targets;
    }

    // ----- Dialogs (kdialog on KDE, zenity elsewhere) -----

    /// <summary>
    /// Asks which target to use, with the desktop's own dialog tool.
    /// Returns null when the user cancels.
    /// </summary>
    static async Task<ShareTarget?> ChooseTargetAsync(string title, IReadOnlyList<ShareTarget> targets)
    {
        if (targets.Count == 1)
            return targets[0];

        var choice = await Dialogs.PickAsync(title, "Share with:", targets.Select(t => (t.Id, t.Label)).ToList()).ConfigureAwait(false);
        if (choice.Cancelled)
            return null;

        if (choice.Value is null)
        {
            Debug.WriteLine($"[LinuxShare] no chooser dialog found ({Tools.InstallHint(IsKdeLike ? "kdialog" : "zenity")}), using email");
            return targets[0];
        }

        return targets.FirstOrDefault(t => t.Id == choice.Value);
    }

    /// <summary>Dialogs shown with the desktop's own tool: kdialog (KDE) or zenity (GNOME/Ubuntu, others).</summary>
    static class Dialogs
    {
        /// <summary>Result: <c>Cancelled</c> when the user closed the dialog, <c>Value</c> null when no dialog tool exists.</summary>
        public readonly record struct Choice(bool Cancelled, string? Value);

        static string[] Order => IsKdeLike ? new[] { "kdialog", "zenity" } : new[] { "zenity", "kdialog" };

        public static async Task<Choice> PickAsync(string title, string prompt, IReadOnlyList<(string Id, string Label)> items)
        {
            foreach (var tool in Order)
            {
                if (!Tools.Exists(tool))
                    continue;

                var args = new List<string>();
                if (tool == "kdialog")
                {
                    args.AddRange(new[] { "--title", title, "--menu", prompt });
                }
                else
                {
                    args.AddRange(new[]
                    {
                        "--list", "--title", title, "--text", prompt,
                        "--column", "Id", "--column", "Item",
                        "--hide-column", "1", "--print-column", "1", "--hide-header",
                        "--width", "420", "--height", "360",
                    });
                }

                foreach (var (id, label) in items)
                    args.AddRange(new[] { id, label });

                var result = await Tools.RunAsync(tool, args, timeout: null).ConfigureAwait(false);
                if (result is null)
                    continue;

                return result.ExitCode != 0 ? new Choice(true, null) : new Choice(false, result.Output.Trim());
            }

            return new Choice(false, null);
        }

        public static async Task InfoAsync(string title, string text)
        {
            foreach (var tool in Order)
            {
                if (!Tools.Exists(tool))
                    continue;

                var args = tool == "kdialog"
                    ? new[] { "--title", title, "--msgbox", text }
                    : new[] { "--info", "--title", title, "--text", text, "--width", "420" };

                if (await Tools.RunAsync(tool, args, timeout: null).ConfigureAwait(false) is not null)
                    return;
            }

            Debug.WriteLine($"[LinuxShare] {title}: {text}");
        }

        public static async Task<Choice> AskTextAsync(string title, string prompt)
        {
            foreach (var tool in Order)
            {
                if (!Tools.Exists(tool))
                    continue;

                var args = tool == "kdialog"
                    ? new[] { "--title", title, "--inputbox", prompt }
                    : new[] { "--entry", "--title", title, "--text", prompt };

                var result = await Tools.RunAsync(tool, args, timeout: null).ConfigureAwait(false);
                if (result is null)
                    continue;

                return result.ExitCode != 0 ? new Choice(true, null) : new Choice(false, result.Output.Trim());
            }

            return new Choice(false, null);
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
    static MessageBuffer CreateOpenUriMessage(DBusConnection connection, string parentWindow, string token, string uri, bool ask = true)
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
            WriteOpenOptions(ref writer, token, ask);

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

    static void WriteOpenOptions(ref MessageWriter writer, string token, bool ask = true)
    {
        var options = writer.WriteDictionaryStart();
        WriteStringOption(ref writer, "handle_token", token);
        WriteStringOption(ref writer, "activation_token", Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN"));

        // "Open with…": always show the application chooser.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("ask");
        writer.WriteVariantBool(ask);

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

    // ----- KDE Connect -----

    /// <summary>
    /// Files and links: one "KDE Connect" entry opening <c>kdeconnect-handler</c>, KDE Connect's own
    /// dialog to pick the device. Text: the handler only takes URLs, so one entry per reachable
    /// device, sent with <c>kdeconnect-cli --share-text</c>.
    /// </summary>
    static async Task<IReadOnlyList<ShareTarget>> DiscoverKdeConnectAsync(ShareContent content)
    {
        var devices = await ListKdeConnectDevicesAsync().ConfigureAwait(false);
        var usesHandler = (content.HasFiles || content.IsLinkOnly) && Tools.Exists("kdeconnect-handler");

        // The handler lists the paired and reachable devices itself; without kdeconnect-cli we
        // can't tell whether there is one, so the entry is offered anyway.
        if (usesHandler && (devices is null || devices.Count > 0))
            return new[] { new ShareTarget("kdeconnect", "Phone… (KDE Connect)", ShareWithKdeConnectHandlerAsync) };

        return (devices ?? new List<(string Id, string Name)>())
            .Select(d => new ShareTarget(
                "kdeconnect:" + d.Id,
                $"{d.Name} (KDE Connect)",
                c => ShareWithKdeConnectCliAsync(d.Id, c)))
            .ToList();
    }

    /// <summary>Paired and reachable devices, or null when kdeconnect-cli isn't installed.</summary>
    static async Task<List<(string Id, string Name)>?> ListKdeConnectDevicesAsync()
    {
        if (!Tools.Exists("kdeconnect-cli"))
            return null;

        // One "<id> <name>" line per paired and reachable device.
        var result = await Tools.RunAsync("kdeconnect-cli", new[] { "--list-available", "--id-name-only" }, s_discoveryTimeout).ConfigureAwait(false);
        if (result is null || result.ExitCode != 0)
            return new List<(string Id, string Name)>();

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    // kdeconnect-handler <url>...: device picker dialog; exits 1 when cancelled.
    static async Task ShareWithKdeConnectHandlerAsync(ShareContent content)
    {
        var args = content.HasFiles
            ? content.Files.Select(Path.GetFullPath).ToList()
            : new List<string> { content.Uri! };

        var result = await Tools.RunAsync("kdeconnect-handler", args, timeout: null).ConfigureAwait(false)
            ?? throw new FeatureNotSupportedException("kdeconnect-handler is not available.");

        // Exit code 1 is also "dialog cancelled": like the other platforms, not an error.
        if (result.ExitCode is not (0 or 1))
            result.ThrowIfFailed("kdeconnect-handler");
    }

    static async Task ShareWithKdeConnectCliAsync(string deviceId, ShareContent content)
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

    /// <summary>
    /// Files: one "GSConnect" entry opening GSConnect's own device chooser dialog
    /// (<c>org.freedesktop.Application.Open</c> with file URIs). Links and text: the chooser only
    /// takes files, so one entry per connected device, sent through its actions.
    /// </summary>
    static async Task<IReadOnlyList<ShareTarget>> DiscoverGSConnectAsync(ShareContent content)
    {
        if (!XdgDesktopPortal.IsSessionBusAvailable)
            return Array.Empty<ShareTarget>();

        // NoAutoStart: only use GSConnect if it is already running.
        var devices = (await DBusObjects.GetManagedObjectsAsync(
                DBusConnection.Session, GSConnectBusName, GSConnectObjectPath, GSConnectDeviceInterface, s_discoveryTimeout).ConfigureAwait(false))
            .Where(o => o.Properties.GetBool("Connected") && o.Properties.GetBool("Paired"))
            .ToList();

        if (devices.Count == 0)
            return Array.Empty<ShareTarget>();

        if (content.HasFiles)
            return new[] { new ShareTarget("gsconnect", "Phone… (GSConnect)", OpenGSConnectChooserAsync) };

        return devices
            .Select(o => new ShareTarget(
                "gsconnect:" + o.Path,
                $"{o.Properties.GetString("Name") ?? "Device"} (GSConnect)",
                c => ShareWithGSConnectAsync(o.Path, c)))
            .ToList();
    }

    // org.freedesktop.Application.Open(as uris, s hint, a{sv} platform_data):
    // GSConnect shows its DeviceChooser for every file:// URI.
    static Task OpenGSConnectChooserAsync(ShareContent content)
    {
        var connection = DBusConnection.Session;
        var uris = content.Files.Select(f => new Uri(Path.GetFullPath(f)).AbsoluteUri).ToArray();
        return connection.CallMethodAsync(CreateOpenMessage(connection, GSConnectBusName, GSConnectObjectPath, uris));
    }

    static MessageBuffer CreateOpenMessage(DBusConnection connection, string busName, string path, string[] uris)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: busName,
            path: path,
            @interface: "org.freedesktop.Application",
            member: "Open",
            signature: "assa{sv}");

        var array = writer.WriteArrayStart(DBusType.String);
        foreach (var uri in uris)
            writer.WriteString(uri);
        writer.WriteArrayEnd(array);

        writer.WriteString(string.Empty); // hint

        var platformData = writer.WriteDictionaryStart();
        if (Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN") is { Length: > 0 } token)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("activation-token");
            writer.WriteVariantString(token);
        }
        writer.WriteDictionaryEnd(platformData);

        return writer.CreateMessage();
    }

    static async Task ShareWithGSConnectAsync(string devicePath, ShareContent content) =>
        await ShareWithGSConnectAsync(devicePath, content, textAction: null).ConfigureAwait(false);

    /// <param name="textAction">Action used for text (and links): shareText/shareUri by default, shareSms for SMS.</param>
    static async Task ShareWithGSConnectAsync(string devicePath, ShareContent content, string? textAction)
    {
        var connection = DBusConnection.Session;

        if (content.HasFiles)
        {
            foreach (var file in content.Files)
                await ActivateAsync("shareFile", Path.GetFullPath(file)).ConfigureAwait(false);
        }
        else if (textAction is not null)
        {
            await ActivateAsync(textAction, content.Body!).ConfigureAwait(false);
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

    // ----- Phone link setup (KDE Connect / GSConnect not installed) -----

    const string GSConnectUuid = "gsconnect@andyholmes.github.io";
    const string ShellExtensionsBusName = "org.gnome.Shell.Extensions";
    const string ShellExtensionsPath = "/org/gnome/Shell/Extensions";
    const string KdeConnectAppStreamId = "org.kde.kdeconnect";

    // GNOME Shell extension state ACTIVE (js/misc/extensionUtils.js ExtensionState), serialized as a double.
    const double ExtensionStateEnabled = 1;

    static bool IsGnomeShell =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
            .Split(':').Any(d => d.Equals("GNOME", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Offered at every opening of the share dialog as long as no phone can be used:
    /// <list type="bullet">
    /// <item>nothing installed: install GSConnect on GNOME Shell (GNOME Shell's own dialog),
    /// KDE Connect elsewhere (software center: Discover, GNOME Software...);</item>
    /// <item>GSConnect installed but inactive: enable it;</item>
    /// <item>installed but no paired phone reachable: open the pairing UI
    /// (KDE Connect app, GSConnect preferences).</item>
    /// </list>
    /// Nothing is remembered between openings: the entry disappears once a phone is available.
    /// </summary>
    async Task<IReadOnlyList<ShareTarget>> DiscoverPhoneSetupAsync()
    {
        if (IsKdeConnectInstalled())
        {
            // Unknown without kdeconnect-cli: the handler's own dialog then shows the devices.
            var devices = await ListKdeConnectDevicesAsync().ConfigureAwait(false);
            if (devices is not null && devices.Count == 0 && Tools.Exists("kdeconnect-app"))
                return new[] { new ShareTarget("setup:kdeconnect-pair", "Pair a phone with KDE Connect…", _ => OpenKdeConnectPairingAsync()) };

            return Array.Empty<ShareTarget>();
        }

        if (IsGnomeShell && XdgDesktopPortal.IsSessionBusAvailable)
        {
            var state = await GetExtensionStateAsync(GSConnectUuid).ConfigureAwait(false);
            if (state == ExtensionStateEnabled)
            {
                var devices = await DBusObjects.GetManagedObjectsAsync(
                    DBusConnection.Session, GSConnectBusName, GSConnectObjectPath, GSConnectDeviceInterface, s_discoveryTimeout).ConfigureAwait(false);

                return devices.Any(o => o.Properties.GetBool("Connected") && o.Properties.GetBool("Paired"))
                    ? Array.Empty<ShareTarget>()
                    : new[] { new ShareTarget("setup:gsconnect-pair", "Pair a phone with GSConnect…", _ => OpenGSConnectPairingAsync()) };
            }

            if (state is not null)
                return new[] { new ShareTarget("setup:gsconnect-enable", "Enable GSConnect to share with your phone…", _ => EnableGSConnectAsync()) };

            return new[] { new ShareTarget("setup:gsconnect", "Install GSConnect to share with your phone…", _ => InstallGSConnectAsync()) };
        }

        return new[] { new ShareTarget("setup:kdeconnect", "Install KDE Connect to share with your phone…", _ => InstallKdeConnectAsync()) };
    }

    // The KDE Connect app lists the nearby devices and pairs them.
    static async Task OpenKdeConnectPairingAsync()
    {
        if (!Tools.Start("kdeconnect-app", Array.Empty<string>()))
            throw new FeatureNotSupportedException("kdeconnect-app is not available.");

        await ShowPairingHelpAsync("KDE Connect").ConfigureAwait(false);
    }

    // GSConnect's preferences window lists the nearby devices and pairs them.
    static async Task OpenGSConnectPairingAsync()
    {
        var connection = DBusConnection.Session;
        await connection.CallMethodAsync(CreateOpenExtensionPrefsMessage(connection, GSConnectUuid)).ConfigureAwait(false);
        await ShowPairingHelpAsync("GSConnect").ConfigureAwait(false);
    }

    // OpenExtensionPrefs(s uuid, s parent_window, a{sv} options)
    static MessageBuffer CreateOpenExtensionPrefsMessage(DBusConnection connection, string uuid)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: ShellExtensionsBusName,
            path: ShellExtensionsPath,
            @interface: "org.gnome.Shell.Extensions",
            member: "OpenExtensionPrefs",
            signature: "ssa{sv}");
        writer.WriteString(uuid);
        writer.WriteString(string.Empty);
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }

    static bool IsKdeConnectInstalled() =>
        Tools.Exists("kdeconnect-cli") || Tools.Exists("kdeconnect-app") || Tools.Exists("kdeconnect-handler");

    /// <summary>GNOME Shell extension state, or null when the extension isn't installed.</summary>
    static async Task<double?> GetExtensionStateAsync(string uuid)
    {
        try
        {
            var connection = DBusConnection.Session;
            var info = await connection.CallMethodAsync(
                CreateShellExtensionsMessage(connection, "GetExtensionInfo", uuid),
                static (Message m, object? state) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue())
                .WaitAsync(s_discoveryTimeout).ConfigureAwait(false);

            if (info.Count == 0)
                return null;

            return info.TryGetValue("state", out var value) && value.Type == VariantValueType.Double ? value.GetDouble() : 0;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// GNOME Shell downloads the extension from extensions.gnome.org after its own confirmation dialog.
    /// </summary>
    static async Task InstallGSConnectAsync()
    {
        var connection = DBusConnection.Session;
        var result = await connection.CallMethodAsync(
            CreateShellExtensionsMessage(connection, "InstallRemoteExtension", GSConnectUuid),
            static (Message m, object? state) => m.GetBodyReader().ReadString()).ConfigureAwait(false);

        if (result != "successful")
            return; // cancelled in GNOME Shell's dialog

        await OpenGSConnectPairingAsync().ConfigureAwait(false);
    }

    static async Task EnableGSConnectAsync()
    {
        var connection = DBusConnection.Session;
        var enabled = await connection.CallMethodAsync(
            CreateShellExtensionsMessage(connection, "EnableExtension", GSConnectUuid),
            static (Message m, object? state) => m.GetBodyReader().ReadBool()).ConfigureAwait(false);

        if (!enabled)
            throw new InvalidOperationException("GNOME Shell could not enable GSConnect.");

        await OpenGSConnectPairingAsync().ConfigureAwait(false);
    }

    static MessageBuffer CreateShellExtensionsMessage(DBusConnection connection, string method, string uuid)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: ShellExtensionsBusName,
            path: ShellExtensionsPath,
            @interface: "org.gnome.Shell.Extensions",
            member: method,
            signature: "s");
        writer.WriteString(uuid);
        return writer.CreateMessage();
    }

    /// <summary>
    /// Opens KDE Connect's page in the software center (<c>appstream://</c>, handled by Discover and
    /// GNOME Software) through the OpenURI portal; shows the install command when none is installed.
    /// </summary>
    async Task InstallKdeConnectAsync()
    {
        uint? response = null;
        if (XdgDesktopPortal.IsSessionBusAvailable)
        {
            try
            {
                var token = XdgDesktopPortal.NewHandleToken();
                var parentWindow = _parentWindow?.Invoke() ?? string.Empty;
                response = await XdgDesktopPortal.RequestAsync(
                    token,
                    connection => CreateOpenUriMessage(connection, parentWindow, token, $"appstream://{KdeConnectAppStreamId}", ask: false),
                    s_openWithResponseTimeout).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException ex)
            {
                Debug.WriteLine($"[LinuxShare] no software center for appstream:// ({ex.Message})");
            }
        }

        if (response is null or XdgDesktopPortal.ResponseOther)
        {
            await Dialogs.InfoAsync("KDE Connect",
                "Install KDE Connect with your software center, or in a terminal:\n\n" +
                Tools.InstallHint(KdeConnectPackageName()) +
                "\n\nThen install KDE Connect on your phone and pair it.").ConfigureAwait(false);
            return;
        }

        await ShowPairingHelpAsync("KDE Connect").ConfigureAwait(false);
    }

    static string KdeConnectPackageName()
    {
        var distribution = DistributionImplementation.Current;
        if (distribution == Distribution.Fedora || distribution == Distribution.RHEL ||
            distribution == Distribution.CentOS || distribution == Distribution.RockyLinux ||
            distribution == Distribution.AlmaLinux)
            return "kde-connect";

        if (distribution == Distribution.OpenSUSE)
            return "kdeconnect-kde";

        return "kdeconnect";
    }

    static Task ShowPairingHelpAsync(string app) =>
        Dialogs.InfoAsync(app,
            $"Install the KDE Connect app on your phone (Google Play, F-Droid or the App Store), " +
            $"connect both devices to the same network, then pair them from {app}. " +
            $"Your phone will then be offered when sharing.");

    // ----- SMS / MMS through the phone (KDE Connect, GSConnect) -----

    const string KdeConnectBusName = "org.kde.kdeconnect";
    const string KdeConnectConversationsInterface = "org.kde.kdeconnect.device.conversations";

    /// <summary>
    /// <list type="bullet">
    /// <item>Text or link: "SMS…" opens the phone's messaging UI with the message filled in:
    /// the KDE Connect SMS app (<c>kdeconnect-sms --message</c>) or GSConnect's conversation chooser
    /// (<c>shareSms</c> action). The user picks the conversation there.</item>
    /// <item>Files, automatic (KDE Connect): "MMS…" asks for the recipient (contacts synced by
    /// KDE Connect, or a number) and sends the files with <c>sendWithoutConversation</c>.</item>
    /// <item>Files, manual (KDE Connect): "SMS app (attach the files)" opens the SMS app; its
    /// attach button has to be used, files can't be passed to it.</item>
    /// </list>
    /// </summary>
    static async Task<IReadOnlyList<ShareTarget>> DiscoverSmsAsync(ShareContent content)
    {
        var targets = new List<ShareTarget>();

        var kdeDevices = await ListKdeConnectSmsDevicesAsync().ConfigureAwait(false);
        var hasSmsApp = Tools.Exists("kdeconnect-sms");

        foreach (var device in kdeDevices)
        {
            var suffix = kdeDevices.Count > 1 ? $" via {device.Name}" : string.Empty;

            // The phone doesn't expose SMS (plugin disabled or SMS permission not granted in the
            // KDE Connect app): still offered, so the user learns how to enable it.
            if (!device.SmsReady)
            {
                targets.Add(new($"sms-setup:{device.Id}", $"{(content.HasFiles ? "MMS" : "SMS")}…{suffix} (KDE Connect, enable SMS on the phone)",
                    _ => Dialogs.InfoAsync("KDE Connect",
                        $"{device.Name} doesn't allow sending SMS yet. In the KDE Connect app on the phone, open the " +
                        "plugin settings, enable \"SMS\" and grant the SMS and contacts permissions; " +
                        "also check that the SMS plugin is enabled for the phone in KDE Connect on this computer.")));
                continue;
            }

            if (content.HasFiles)
            {
                targets.Add(new($"mms:{device.Id}", $"MMS…{suffix} (KDE Connect)",
                    c => SendMmsWithKdeConnectAsync(device.Id, device.Name, c)));

                if (hasSmsApp)
                    targets.Add(new($"smsapp:{device.Id}", $"SMS app, attach the files yourself{suffix} (KDE Connect)",
                        c => OpenKdeConnectSmsAppAsync(device.Id, c.Body ?? string.Empty)));
            }
            else if (hasSmsApp)
            {
                targets.Add(new($"sms:{device.Id}", $"SMS…{suffix} (KDE Connect)",
                    c => OpenKdeConnectSmsAppAsync(device.Id, c.Body!)));
            }
        }

        // GSConnect: text only.
        if (!content.HasFiles && XdgDesktopPortal.IsSessionBusAvailable)
        {
            var devices = (await DBusObjects.GetManagedObjectsAsync(
                    DBusConnection.Session, GSConnectBusName, GSConnectObjectPath, GSConnectDeviceInterface, s_discoveryTimeout).ConfigureAwait(false))
                .Where(o => o.Properties.GetBool("Connected") && o.Properties.GetBool("Paired"))
                .ToList();

            foreach (var device in devices)
            {
                var name = device.Properties.GetString("Name") ?? "Device";
                var suffix = devices.Count > 1 ? $" via {name}" : string.Empty;
                targets.Add(new($"gsms:{device.Path}", $"SMS…{suffix} (GSConnect)",
                    c => ShareWithGSConnectAsync(device.Path, c, "shareSms")));
            }
        }

        return targets;
    }

    /// <summary>
    /// Reachable KDE Connect devices, and whether their SMS plugin is loaded. It is loaded only
    /// when enabled on both sides and the phone app has the SMS permission.
    /// </summary>
    static async Task<List<(string Id, string Name, bool SmsReady)>> ListKdeConnectSmsDevicesAsync()
    {
        var devices = await ListKdeConnectDevicesAsync().ConfigureAwait(false);
        if (devices is null || devices.Count == 0 || !XdgDesktopPortal.IsSessionBusAvailable)
            return new List<(string Id, string Name, bool SmsReady)>();

        var result = new List<(string Id, string Name, bool SmsReady)>();
        foreach (var device in devices)
        {
            bool? hasSms = null;
            try
            {
                var connection = DBusConnection.Session;
                hasSms = await connection.CallMethodAsync(
                    CreateHasPluginMessage(connection, device.Id, "kdeconnect_sms"),
                    static (Message m, object? state) => m.GetBodyReader().ReadBool()).WaitAsync(s_discoveryTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
            {
                // Unknown (older daemon, slow bus): offer SMS, the daemon reports a failure if needed.
                Debug.WriteLine($"[LinuxShare] hasPlugin(kdeconnect_sms) failed for {device.Id}: {ex.Message}");
            }

            result.Add((device.Id, device.Name, hasSms != false));
        }

        return result;
    }

    // org.kde.kdeconnect.device.hasPlugin(s name) -> b
    static MessageBuffer CreateHasPluginMessage(DBusConnection connection, string deviceId, string plugin)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: KdeConnectBusName,
            path: $"/modules/kdeconnect/devices/{deviceId}",
            @interface: "org.kde.kdeconnect.device",
            member: "hasPlugin",
            signature: "s");
        writer.WriteString(plugin);
        return writer.CreateMessage();
    }

    static async Task OpenKdeConnectSmsAppAsync(string deviceId, string message)
    {
        var args = new List<string> { "--device", deviceId };
        if (message.Length > 0)
            args.AddRange(new[] { "--message", message });

        // The SMS app is a regular window: don't wait for it to be closed.
        if (!Tools.Start("kdeconnect-sms", args))
            throw new FeatureNotSupportedException("kdeconnect-sms is not available.");

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the recipient, then sends the files (and text) as an MMS through the phone.
    /// </summary>
    static async Task SendMmsWithKdeConnectAsync(string deviceId, string deviceName, ShareContent content)
    {
        var contacts = ReadKdeConnectContacts(deviceId);

        string? number;
        if (contacts.Count > 0)
        {
            var items = new List<(string Id, string Label)> { ("manual", "Enter a phone number…") };
            items.AddRange(contacts.Select((c, i) => ($"contact:{i}", $"{c.Name} — {c.Number}")));

            var choice = await Dialogs.PickAsync("MMS", $"Recipient (contacts of {deviceName}):", items).ConfigureAwait(false);
            if (choice.Cancelled)
                return;

            number = choice.Value is { } id && id.StartsWith("contact:", StringComparison.Ordinal)
                ? contacts[int.Parse(id["contact:".Length..], System.Globalization.CultureInfo.InvariantCulture)].Number
                : null;
        }
        else
        {
            number = null;
        }

        if (number is null)
        {
            var input = await Dialogs.AskTextAsync("MMS", "Recipient phone number(s), separated by spaces:").ConfigureAwait(false);
            if (input.Cancelled)
                return;

            number = input.Value;
        }

        var addresses = (number ?? string.Empty).Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0)
            throw new InvalidOperationException("No recipient phone number was given.");

        var connection = DBusConnection.Session;
        await connection.CallMethodAsync(CreateSendMmsMessage(
            connection, deviceId, addresses, content.Body ?? string.Empty, content.Files.Select(Path.GetFullPath).ToList())).ConfigureAwait(false);
    }

    /// <summary>
    /// org.kde.kdeconnect.device.conversations.sendWithoutConversation(av addresses, s message, av attachmentUrls):
    /// each address is a variant holding a <c>(s)</c> struct (ConversationAddress), each attachment
    /// a variant holding the local file path, read and base64-encoded by the daemon.
    /// </summary>
    static MessageBuffer CreateSendMmsMessage(DBusConnection connection, string deviceId, IReadOnlyList<string> addresses, string message, IReadOnlyList<string> files)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: KdeConnectBusName,
            path: $"/modules/kdeconnect/devices/{deviceId}",
            @interface: KdeConnectConversationsInterface,
            member: "sendWithoutConversation",
            signature: "avsav");

        var addressArray = writer.WriteArrayStart(DBusType.Variant);
        foreach (var address in addresses)
        {
            writer.WriteSignature("(s)");
            writer.WriteStructureStart();
            writer.WriteString(address);
        }
        writer.WriteArrayEnd(addressArray);

        writer.WriteString(message);

        var fileArray = writer.WriteArrayStart(DBusType.Variant);
        foreach (var file in files)
            writer.WriteVariantString(file);
        writer.WriteArrayEnd(fileArray);

        return writer.CreateMessage();
    }

    /// <summary>
    /// Contacts synced by KDE Connect's contacts plugin:
    /// <c>~/.local/share/kpeoplevcard/kdeconnect-&lt;device id&gt;/*.vcf</c>.
    /// </summary>
    static List<(string Name, string Number)> ReadKdeConnectContacts(string deviceId)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        var directory = Path.Combine(dataHome, "kpeoplevcard", "kdeconnect-" + deviceId);
        var contacts = new List<(string Name, string Number)>();
        if (!Directory.Exists(directory))
            return contacts;

        foreach (var file in Directory.EnumerateFiles(directory, "*.vcf"))
        {
            try
            {
                string? name = null;
                var numbers = new List<string>();
                foreach (var line in File.ReadLines(file))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;

                    var key = line[..colon];
                    var value = line[(colon + 1)..].Trim();
                    if (key == "FN" || key.StartsWith("FN;", StringComparison.Ordinal))
                        name = value.Replace("\\,", ",");
                    else if ((key == "TEL" || key.StartsWith("TEL;", StringComparison.Ordinal)) && value.Length > 0)
                        numbers.Add(value);
                }

                foreach (var number in numbers.Distinct())
                    contacts.Add((name ?? number, number));
            }
            catch (IOException)
            {
            }
        }

        return contacts.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ----- Bluetooth (BlueZ + bluedevil-sendfile / bluetooth-sendto) -----

    /// <summary>
    /// KDE: one "Bluetooth" entry opening BlueDevil's send wizard, which has its own device page.
    /// GNOME: <c>bluetooth-sendto</c> requires the device, so one entry per paired device
    /// able to receive files (OBEX Object Push), read from BlueZ.
    /// </summary>
    static async Task<IReadOnlyList<ShareTarget>> DiscoverBluetoothAsync()
    {
        var hasWizard = Tools.Exists("bluedevil-sendfile");
        var hasSendTo = Tools.Exists("bluetooth-sendto");

        if (hasWizard && (IsKdeLike || !hasSendTo))
            return new[] { new ShareTarget("bluetooth", "Bluetooth…", SendWithBluetoothWizardAsync) };

        if (!hasSendTo || DBusAddress.System is null)
            return Array.Empty<ShareTarget>();

        var devices = await DBusObjects.GetManagedObjectsAsync(
            DBusConnection.System, BluezBusName, "/", BluezDeviceInterface, s_discoveryTimeout).ConfigureAwait(false);

        return devices
            .Where(d => d.Properties.GetBool("Paired")
                     && d.Properties.GetStrings("UUIDs").Contains(ObexObjectPushUuid, StringComparer.OrdinalIgnoreCase))
            .Select(d => (Address: d.Properties.GetString("Address"), Name: d.Properties.GetString("Alias") ?? d.Properties.GetString("Name")))
            .Where(d => d.Address is not null)
            .Select(d => new ShareTarget(
                "bluetooth:" + d.Address,
                $"{d.Name ?? d.Address} (Bluetooth)",
                content => SendWithBluetoothAsync("bluetooth-sendto", d.Address!, d.Name, content)))
            .ToList();
    }

    // bluedevil-sendfile -f <file>...: without --kio/--ubi, the wizard starts on its device page.
    static async Task SendWithBluetoothWizardAsync(ShareContent content)
    {
        var args = new List<string>();
        foreach (var file in content.Files)
            args.AddRange(new[] { "--files", Path.GetFullPath(file) });

        var result = await Tools.RunAsync("bluedevil-sendfile", args, timeout: null).ConfigureAwait(false)
            ?? throw new FeatureNotSupportedException("bluedevil-sendfile is not available.");

        result.ThrowIfFailed("bluedevil-sendfile");
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

        /// <summary>Starts a tool without waiting for it. Returns false when it isn't installed.</summary>
        public static bool Start(string tool, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(psi);
                return process is not null;
            }
            catch (Win32Exception)
            {
                return false;
            }
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