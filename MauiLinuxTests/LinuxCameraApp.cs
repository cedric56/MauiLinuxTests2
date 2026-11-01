using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Media;

/// <summary>
/// Captures with the desktop's camera application, like the camera intent on Android:
/// GNOME Camera (Snapshot, <c>org.gnome.Snapshot</c>) is started, and the photo or video
/// it saves is returned.
/// </summary>
/// <remarks>
/// <para>
/// Snapshot has no "capture and return" API. It is D-Bus activatable (<c>DBusActivatable=true</c>),
/// so it is started with <c>org.freedesktop.Application.Activate</c> (works for a distribution
/// package and for the Flatpak), and its output folder is watched: pictures go to
/// <c>$XDG_PICTURES_DIR/Camera</c>, videos to <c>$XDG_VIDEOS_DIR/Camera</c> (snapshot src/utils.rs).
/// </para>
/// <para>
/// The capture ends with the first new file once it is complete, or with no result when
/// Snapshot exits (its bus name disappears) without saving anything. When Snapshot was
/// started for this capture, it is closed afterwards (<c>app.quit</c>).
/// </para>
/// </remarks>
static class LinuxCameraApp
{
    const string BusName = "org.gnome.Snapshot";
    const string ObjectPath = "/org/gnome/Snapshot";

    static readonly string[] s_pictureExtensions = { ".jpg", ".jpeg", ".png" };
    static readonly string[] s_videoExtensions = { ".mp4", ".webm", ".mkv" };

    static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan s_pictureStableFor = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan s_videoStableFor = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether Snapshot is running or can be started through D-Bus.
    /// </summary>
    public static async Task<bool> IsAvailableAsync()
    {
        if (!XdgDesktopPortal.IsSessionBusAvailable)
            return false;

        try
        {
            var connection = await XdgDesktopPortal.GetConnectionAsync().ConfigureAwait(false);
            return (await connection.ListActivatableServicesAsync().ConfigureAwait(false)).Contains(BusName)
                || (await connection.ListServicesAsync().ConfigureAwait(false)).Contains(BusName);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the camera application and returns the path of the picture (or video) taken,
    /// or null when the user closed it without taking one.
    /// </summary>
    public static async Task<string?> CaptureAsync(bool video, CancellationToken cancellationToken = default)
    {
        var directories = GetOutputDirectories(video);
        // Snapshot's own folder (the localized one), created like Snapshot does.
        Directory.CreateDirectory(directories[0]);

        var extensions = video ? s_videoExtensions : s_pictureExtensions;
        var existing = new HashSet<string>(ListMedia(directories, extensions), StringComparer.Ordinal);
        var startedAt = DateTime.UtcNow;

        var connection = await XdgDesktopPortal.GetConnectionAsync().ConfigureAwait(false);
        var wasRunning = (await connection.ListServicesAsync().ConfigureAwait(false)).Contains(BusName);

        // Open in the right mode (best effort: a Flatpak Snapshot keeps its own settings).
        await SetCaptureModeAsync(video ? "video" : "picture").ConfigureAwait(false);

        // Snapshot exiting = the user closed it.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var nameWatch = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
                Arg0 = BusName,
            },
            static (Message m, object? state) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString();                // name
                reader.ReadString();                // old owner
                return reader.ReadString();         // new owner
            },
            static (Exception? ex, string newOwner, object? readerState, object? handlerState) =>
            {
                if (ex is null && newOwner.Length == 0)
                    ((TaskCompletionSource)handlerState!).TrySetResult();
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: closed,
            emitOnCapturedContext: false).ConfigureAwait(false);

        // D-Bus activation starts it when it isn't running, otherwise brings its window up.
        await connection.CallMethodAsync(CreateActivateMessage(connection)).ConfigureAwait(false);

        // Done as soon as a picture (or a finished recording) is saved, like a camera intent.
        // If the user closes the camera first, whatever was saved meanwhile is still returned.
        string? result = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var appClosed = closed.Task.IsCompleted;

            var candidate = FindNewMedia(directories, extensions, existing, startedAt);
            if (candidate is not null)
            {
                result = await WaitUntilCompleteAsync(candidate, video ? s_videoStableFor : s_pictureStableFor, cancellationToken).ConfigureAwait(false);
                if (result is not null || appClosed)
                    break;

                existing.Add(candidate); // deleted from the camera's gallery: wait for another one
                continue;
            }

            // Checked after the last scan, so a file saved just before closing isn't missed.
            if (appClosed)
                break;

            await Task.WhenAny(closed.Task, Task.Delay(s_pollInterval, cancellationToken)).ConfigureAwait(false);
        }

        Debug.WriteLine($"[LinuxCameraApp] watched {string.Join(", ", directories)}: {result ?? "no new file"}");

        // Started for this capture: close it, like a camera intent returns to the app.
        if (result is not null && !wasRunning && !closed.Task.IsCompleted)
        {
            try
            {
                await connection.CallMethodAsync(CreateQuitMessage(connection)).ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex)
            {
                Debug.WriteLine($"[LinuxCameraApp] closing Snapshot failed: {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    static IEnumerable<string> ListMedia(IEnumerable<string> directories, string[] extensions) =>
        directories
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

    // The newest file that appeared since the camera was opened.
    static string? FindNewMedia(IEnumerable<string> directories, string[] extensions, HashSet<string> existing, DateTime since) =>
        ListMedia(directories, extensions)
            .Where(f => !existing.Contains(f) && File.GetLastWriteTimeUtc(f) >= since.AddSeconds(-1))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>
    /// Where Snapshot may save: &lt;Pictures|Videos&gt;/Camera resolved like GLib does
    /// (user-dirs.dirs, localized), plus the unlocalized ~/Pictures|Videos/Camera, and the
    /// parent folders themselves (other camera apps, Flatpak without user-dirs).
    /// </summary>
    static List<string> GetOutputDirectories(bool video)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fallback = video ? "Videos" : "Pictures";
        var userDirectory = GetUserDirectory(video ? "XDG_VIDEOS_DIR" : "XDG_PICTURES_DIR", fallback);

        return new[]
            {
                Path.Combine(userDirectory, "Camera"),
                Path.Combine(home, fallback, "Camera"),
                userDirectory,
                Path.Combine(home, fallback),
            }
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The camera writes the file progressively (a video grows while recording):
    /// it is complete once its size hasn't changed for <paramref name="stableFor"/>.
    /// </summary>
    static async Task<string?> WaitUntilCompleteAsync(string path, TimeSpan stableFor, CancellationToken cancellationToken)
    {
        long lastSize = -1;
        var stableSince = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(path))
                return null; // deleted from the camera's gallery

            var size = new FileInfo(path).Length;
            if (size != lastSize || size == 0)
            {
                lastSize = size;
                stableSince.Restart();
            }
            else if (stableSince.Elapsed >= stableFor)
            {
                return path;
            }

            await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // org.freedesktop.Application.Activate(a{sv} platform_data)
    static MessageBuffer CreateActivateMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusName,
            path: ObjectPath,
            @interface: "org.freedesktop.Application",
            member: "Activate",
            signature: "a{sv}");

        var platformData = writer.WriteDictionaryStart();
        // Lets the compositor give focus to the camera window (Wayland/X11 startup notification).
        if (Environment.GetEnvironmentVariable("XDG_ACTIVATION_TOKEN") is { Length: > 0 } token)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("activation-token");
            writer.WriteVariantString(token);
        }
        writer.WriteDictionaryEnd(platformData);

        return writer.CreateMessage();
    }

    // org.gtk.Actions.Activate(s action_name, av parameter, a{sv} platform_data): "quit"
    static MessageBuffer CreateQuitMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: BusName,
            path: ObjectPath,
            @interface: "org.gtk.Actions",
            member: "Activate",
            signature: "sava{sv}");

        writer.WriteString("quit");
        var parameters = writer.WriteArrayStart(DBusType.Variant);
        writer.WriteArrayEnd(parameters);
        var platformData = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(platformData);

        return writer.CreateMessage();
    }

    static async Task SetCaptureModeAsync(string mode)
    {
        try
        {
            var psi = new ProcessStartInfo("gsettings") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "set", BusName, "capture-mode", mode })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return;

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            // No gsettings or no schema (Flatpak only): Snapshot opens in its last mode.
        }
    }

    /// <summary>
    /// XDG user directory from ~/.config/user-dirs.dirs (localized: ~/Images in French),
    /// like GLib's g_get_user_special_dir, which Snapshot uses.
    /// </summary>
    static string GetUserDirectory(string key, string fallback)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
            configHome = Path.Combine(home, ".config");

        var file = Path.Combine(configHome, "user-dirs.dirs");
        if (File.Exists(file))
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key + "=", StringComparison.Ordinal))
                    continue;

                var value = trimmed[(key.Length + 1)..].Trim('"');
                value = value.StartsWith("$HOME", StringComparison.Ordinal) ? home + value[5..] : value;
                if (Path.IsPathRooted(value) && value.TrimEnd('/') != home.TrimEnd('/'))
                    return value;
            }
        }

        return Path.Combine(home, fallback);
    }
}