using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Devices;

/// <summary>
/// Flashlight through the camera flash LED exposed by the kernel LED class
/// (<c>/sys/class/leds/*:torch</c> or <c>*:flash</c>), as on PinePhone, Librem 5
/// and most phones supported by postmarketOS.
/// </summary>
/// <remarks>
/// The brightness is set through logind (<c>org.freedesktop.login1.Session.SetBrightness</c>),
/// which lets the active session drive LEDs without root, like Phosh's torch.
/// Falls back to writing sysfs directly when udev grants write access.
/// No UI toolkit dependency: works for both the Gtk4 and the Avalonia backends.
/// </remarks>
public class LinuxFlashlight : IFlashlight
{
    const string LedsClassPath = "/sys/class/leds";

    const string LogindBusName = "org.freedesktop.login1";
    const string LogindSessionPath = "/org/freedesktop/login1/session/auto"; // the caller's session
    const string LogindSessionInterface = "org.freedesktop.login1.Session";

    static readonly TimeSpan s_exitTimeout = TimeSpan.FromMilliseconds(500);

    readonly Lazy<TorchLed?> _led = new(FindTorchLed);
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _isOn;
    bool _exitHooked;

    /// <inheritdoc />
    public Task<bool> IsSupportedAsync() => Task.FromResult(_led.Value is not null);

    /// <inheritdoc />
    public Task TurnOnAsync() => SetAsync(on: true);

    /// <inheritdoc />
    public Task TurnOffAsync() => SetAsync(on: false);

    async Task SetAsync(bool on)
    {
        var led = _led.Value
            ?? throw new FeatureNotSupportedException("No torch or flash LED was found in /sys/class/leds.");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SetBrightnessAsync(led, on ? led.MaxBrightness : 0u).ConfigureAwait(false);
            _isOn = on;

            // Don't leave the LED burning after the app exits.
            if (on && !_exitHooked)
            {
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                _exitHooked = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    void OnProcessExit(object? sender, EventArgs e)
    {
        if (!_isOn || _led.Value is not { } led)
            return;

        try
        {
            SetBrightnessAsync(led, 0).Wait(s_exitTimeout);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxFlashlight] turning off on exit failed: {ex.Message}");
        }
    }

    static async Task SetBrightnessAsync(TorchLed led, uint brightness)
    {
        Exception? logindError = null;

        if (DBusAddress.System is not null)
        {
            try
            {
                var connection = DBusConnection.System;
                await connection.CallMethodAsync(CreateSetBrightnessMessage(connection, led.Name, brightness)).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // e.g. no logind, process not part of a session, inactive session
                logindError = ex;
            }
        }

        try
        {
            await File.WriteAllTextAsync(Path.Combine(led.Path, "brightness"), brightness.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PermissionException(
                $"Unable to set the brightness of LED '{led.Name}' through logind ({logindError?.Message ?? "system bus unavailable"}) or sysfs ({ex.Message}).");
        }
    }

    // SetBrightness(s subsystem, s name, u brightness)
    static MessageBuffer CreateSetBrightnessMessage(DBusConnection connection, string ledName, uint brightness)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: LogindBusName,
            path: LogindSessionPath,
            @interface: LogindSessionInterface,
            member: "SetBrightness",
            signature: "ssu");

        writer.WriteString("leds");
        writer.WriteString(ledName);
        writer.WriteUInt32(brightness);

        return writer.CreateMessage();
    }

    /// <summary>
    /// Finds the camera LED, using the function names of the kernel's
    /// include/dt-bindings/leds/common.h (also used by feedbackd).
    /// A dedicated torch LED is preferred over a flash LED.
    /// </summary>
    static TorchLed? FindTorchLed()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(LedsClassPath))
            return null;

        try
        {
            // Entries are symlinks to the device directories: enumerate entries, not directories.
            var candidates = Directory.EnumerateFileSystemEntries(LedsClassPath)
                .Select(path => (Path: path, Name: Path.GetFileName(path)))
                .Where(led => led.Name.EndsWith(":torch", StringComparison.Ordinal)
                           || led.Name.EndsWith(":flash", StringComparison.Ordinal))
                .OrderBy(led => led.Name.EndsWith(":torch", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(led => led.Name, StringComparer.Ordinal);

            foreach (var (path, name) in candidates)
            {
                var maxFile = Path.Combine(path, "max_brightness");
                if (!File.Exists(maxFile))
                    continue;

                if (uint.TryParse(File.ReadAllText(maxFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0)
                    return new TorchLed(path, name, max);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[LinuxFlashlight] LED detection failed: {ex.Message}");
        }

        return null;
    }

    sealed record TorchLed(string Path, string Name, uint MaxBrightness);
}