using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Media;

/// <summary>
/// Media picker for Linux.
/// </summary>
/// <remarks>
/// Picking uses the GTK4 file dialog. Photo capture uses the xdg-desktop-portal
/// Camera portal (permission prompt + PipeWire camera stream), and GStreamer's
/// <c>pipewiresrc</c> to grab a frame from that stream.
/// </remarks>
public class LinuxMediaPicker : IMediaPicker
{
    const string CameraInterface = "org.freedesktop.portal.Camera";

    // Frames skipped before keeping one, so auto exposure / white balance can settle (~1 s at 30 fps).
    const int WarmupFrames = 30;

    // The user may have to answer the portal permission dialog.
    static readonly TimeSpan s_accessTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan s_attemptTimeout = TimeSpan.FromSeconds(8);
    static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);

    // Why capture isn't available, or null when it is.
    static readonly Lazy<string?> s_captureUnavailableReason = new(ProbeCapture);

    // GNOME Camera (Snapshot) installed: captures go through it (viewfinder, shutter, video).
    static readonly Lazy<bool> s_hasCameraApp = new(() =>
        XdgDesktopPortal.WaitBounded(LinuxCameraApp.IsAvailableAsync, s_probeTimeout, fallback: false));

    /// <summary>
    /// True when GNOME Camera is installed, or when the Camera portal reports a camera and
    /// GStreamer with the PipeWire plugin is installed (photo only, taken without viewfinder).
    /// </summary>
    public bool IsCaptureSupported => s_hasCameraApp.Value || s_captureUnavailableReason.Value is null;

    public Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options) =>
        PickFileAsync("Select Photo", "image/*");

    public Task<List<FileResult>> PickPhotosAsync(MediaPickerOptions? options) =>
        PickFilesAsync("Select Photos", "image/*");

    public async Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options)
    {
        // The desktop's camera app: viewfinder and shutter, like the camera intent on Android.
        if (s_hasCameraApp.Value)
        {
            var picture = await LinuxCameraApp.CaptureAsync(video: false).ConfigureAwait(false);
            return picture is null ? null : new FileResult(picture, ContentTypeOf(picture));
        }

        if (s_captureUnavailableReason.Value is { } reason)
            throw new FeatureNotSupportedException($"Photo capture is not available: {reason}");

        // 1. Ask for camera access (the portal shows a permission dialog the first time).
        var token = XdgDesktopPortal.NewHandleToken();
        var access = await XdgDesktopPortal.RequestAsync(token, c => CreateAccessCameraMessage(c, token), s_accessTimeout).ConfigureAwait(false);

        if (access == XdgDesktopPortal.ResponseCancelled)
            return null; // like the other platforms: cancelling isn't an error

        if (access == XdgDesktopPortal.ResponseOther)
            throw new PermissionException("Camera access was denied.");

        // access == null (no Response from an old portal): OpenPipeWireRemote fails below if access isn't granted.

        // 2. Get the PipeWire remote exposing the camera nodes.
        SafeFileHandle remote;
        try
        {
            remote = await XdgDesktopPortal.CallAsync(
                CreateOpenPipeWireRemoteMessage,
                static (Message m, object? state) => m.GetBodyReader().ReadHandle<SafeFileHandle>()).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (!XdgDesktopPortal.IsPortalMissing(ex))
        {
            throw new PermissionException($"Camera access was denied: {ex.Message}");
        }

        // 3. Grab a frame.
        using (remote)
        {
            var directory = Path.Combine(Path.GetTempPath(), "maui-media");
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, $"IMG_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}.jpg");

            await CaptureFrameAsync(remote, output).ConfigureAwait(false);
            return new FileResult(output, "image/jpeg");
        }
    }

    public Task<FileResult?> PickVideoAsync(MediaPickerOptions? options) =>
        PickFileAsync("Select Video", "video/*");

    public Task<List<FileResult>> PickVideosAsync(MediaPickerOptions? options) =>
        PickFilesAsync("Select Videos", "video/*");

    // Recording needs a viewfinder and a stop control: only through the desktop's camera app.
    public async Task<FileResult?> CaptureVideoAsync(MediaPickerOptions? options)
    {
        if (!s_hasCameraApp.Value)
            throw new FeatureNotSupportedException("Video capture requires GNOME Camera (org.gnome.Snapshot).");

        var video = await LinuxCameraApp.CaptureAsync(video: true).ConfigureAwait(false);
        return video is null ? null : new FileResult(video, ContentTypeOf(video));
    }

    static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        _ => "image/jpeg",
    };

    // ----- Capture -----

    // AccessCamera(a{sv} options) -> o handle
    static MessageBuffer CreateAccessCameraMessage(DBusConnection connection, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: XdgDesktopPortal.BusName,
            path: XdgDesktopPortal.ObjectPath,
            @interface: CameraInterface,
            member: "AccessCamera",
            signature: "a{sv}");

        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEnd(options);

        return writer.CreateMessage();
    }

    // OpenPipeWireRemote(a{sv} options) -> h fd
    static MessageBuffer CreateOpenPipeWireRemoteMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: XdgDesktopPortal.BusName,
            path: XdgDesktopPortal.ObjectPath,
            @interface: CameraInterface,
            member: "OpenPipeWireRemote",
            signature: "a{sv}");

        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(options);

        return writer.CreateMessage();
    }

    /// <summary>
    /// Grabs one frame into <paramref name="output"/> with GStreamer, trying in order:
    /// <list type="number">
    /// <item><c>pipewiresrc fd=N</c> on the PipeWire remote given by the Camera portal (works sandboxed);</item>
    /// <item>for unsandboxed apps, <c>pipewiresrc</c> on the user's PipeWire daemon (default camera);</item>
    /// <item>for unsandboxed apps, <c>v4l2src</c> (camera not exposed through PipeWire).</item>
    /// </list>
    /// Each pipeline is <c>… num-buffers=N ! decodebin ! videoconvert ! jpegenc ! multifilesink</c>:
    /// every frame overwrites the file, so the last (settled) frame is kept.
    /// </summary>
    /// <remarks>
    /// Without a target node, <c>pipewiresrc</c> lets the session manager link it to the default
    /// camera; when it doesn't (no node visible through the portal remote, e.g. the permission
    /// is stored for another app id), the source waits forever: hence the per-attempt timeout.
    /// </remarks>
    static async Task CaptureFrameAsync(SafeFileHandle remote, string output)
    {
        var failures = new List<string>();

        // Received fds are close-on-exec; dup() gives an inheritable copy for gst-launch.
        var fd = dup((int)remote.DangerousGetHandle());
        if (fd < 0)
        {
            failures.Add($"portal PipeWire remote: dup failed (errno {Marshal.GetLastPInvokeError()})");
        }
        else
        {
            try
            {
                if (await TryCapturePipelineAsync(new[] { "pipewiresrc", $"fd={fd}" }, output, failures, "portal PipeWire remote").ConfigureAwait(false))
                    return;
            }
            finally
            {
                // gst-launch has its own copy.
                close(fd);
            }
        }

        // Unsandboxed apps can talk to PipeWire and V4L2 directly; the portal granted the access.
        if (!IsSandboxed)
        {
            if (await TryCapturePipelineAsync(new[] { "pipewiresrc" }, output, failures, "PipeWire default camera").ConfigureAwait(false))
                return;

            if (await TryCapturePipelineAsync(new[] { "v4l2src" }, output, failures, "V4L2 (/dev/video0)").ConfigureAwait(false))
                return;
        }

        throw new InvalidOperationException("Photo capture failed:\n" + string.Join("\n", failures));
    }

    static bool IsSandboxed =>
        File.Exists("/.flatpak-info") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAP"));

    static async Task<bool> TryCapturePipelineAsync(string[] source, string output, List<string> failures, string label)
    {
        File.Delete(output);

        var psi = new ProcessStartInfo("gst-launch-1.0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Warnings and errors from the elements, for the failure report.
        psi.Environment["GST_DEBUG"] = "2";
        psi.Environment["GST_DEBUG_NO_COLOR"] = "1";

        psi.ArgumentList.Add("-q");
        foreach (var arg in source)
            psi.ArgumentList.Add(arg);
        foreach (var arg in new[]
        {
            $"num-buffers={WarmupFrames}", "!",
            "decodebin", "!",          // raw or MJPEG cameras
            "videoconvert", "!",
            "jpegenc", "quality=90", "!",
            "multifilesink", $"location={output}",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new FeatureNotSupportedException("Photo capture requires GStreamer (gst-launch-1.0).", ex);
        }

        if (process is null)
            throw new InvalidOperationException("Unable to start gst-launch-1.0.");

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(s_attemptTimeout);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            await stdout.ConfigureAwait(false);
            var errors = string.Join(" | ", (await stderr.ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(4));

            // A frame may have been written before the timeout: keep it.
            var hasFrame = File.Exists(output) && new FileInfo(output).Length > 0;
            if (hasFrame && (timedOut || process.ExitCode == 0))
                return true;

            failures.Add(timedOut
                ? $"- {label}: no frame within {s_attemptTimeout.TotalSeconds:F0} s{(errors.Length > 0 ? $" ({errors})" : "")}"
                : $"- {label}: exit code {process.ExitCode}{(errors.Length > 0 ? $" ({errors})" : "")}");
            return false;
        }
    }

    static string? ProbeCapture()
    {
        if (!XdgDesktopPortal.IsSessionBusAvailable)
            return "no D-Bus session bus.";

        var cameraPresent = XdgDesktopPortal.WaitBounded<bool?>(
            async () => (await XdgDesktopPortal.GetPropertyAsync(CameraInterface, "IsCameraPresent").ConfigureAwait(false)).GetBool(),
            s_probeTimeout,
            fallback: null);

        if (cameraPresent is null)
            return "the xdg-desktop-portal Camera portal is not available (is xdg-desktop-portal running?).";

        if (cameraPresent == false)
            return "the Camera portal reports no camera (no camera node in PipeWire: check `wpctl status`, Video section).";

        if (!HasGStreamerElement("pipewiresrc"))
            return "GStreamer's PipeWire plugin is missing (`gst-inspect-1.0 pipewiresrc` fails): install gstreamer1.0-pipewire (Debian/Ubuntu) or pipewire-gstreamer (Fedora).";

        return null;
    }

    static bool HasGStreamerElement(string element)
    {
        try
        {
            var psi = new ProcessStartInfo("gst-inspect-1.0")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--exists");
            psi.ArgumentList.Add(element);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)s_probeTimeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // GStreamer tools not installed
        }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    // ----- Pick (GTK4 file dialog) -----

    private static async Task<FileResult?> PickFileAsync(string title, string mimeType)
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(title);

            var filter = Gtk.FileFilter.New();
            filter.AddMimeType(mimeType);
            var filterList = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            filterList.Append(filter);
            dialog.SetFilters(filterList);

            var window = (Gtk.Application.GetDefault() as Gtk.Application)?.GetActiveWindow();
            if (window is null) return null;

            var file = await dialog.OpenAsync(window);
            var path = file?.GetPath();
            return path is not null ? new FileResult(path) : null;
        }
        catch { return null; }
    }

    private static async Task<List<FileResult>> PickFilesAsync(string title, string mimeType)
    {
        try
        {
            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(title);

            var filter = Gtk.FileFilter.New();
            filter.AddMimeType(mimeType);
            var filterList = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            filterList.Append(filter);
            dialog.SetFilters(filterList);

            var window = (Gtk.Application.GetDefault() as Gtk.Application)?.GetActiveWindow();
            if (window is null) return new List<FileResult>();

            var files = await dialog.OpenMultipleAsync(window);
            if (files is null) return new List<FileResult>();
            var results = new List<FileResult>();
            for (uint i = 0; i < files.GetNItems(); i++)
            {
                var gioFile = (Gio.File)files.GetObject(i)!;
                var path = gioFile.GetPath();
                if (path is not null)
                    results.Add(new FileResult(path));
            }
            return results;
        }
        catch { return new List<FileResult>(); }
    }
}