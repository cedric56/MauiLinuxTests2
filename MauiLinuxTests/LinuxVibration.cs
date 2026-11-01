using System;
using System.Diagnostics;
using Microsoft.Maui.Devices;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Devices;

/// <summary>
/// Vibration through feedbackd's <c>org.sigxcpu.Feedback.Haptic</c> interface.
/// </summary>
/// <remarks>
/// Desktops without feedbackd, or with a feedbackd too old to expose the Haptic
/// interface, report <see cref="IsSupported"/> = false.
/// No UI toolkit dependency: works for both the Gtk4 and the Avalonia backends.
/// </remarks>
public class LinuxVibration : IVibration
{
    // Same defaults and limits as the shared MAUI implementation.
    static readonly TimeSpan s_defaultDuration = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan s_maxDuration = TimeSpan.FromSeconds(5);

    readonly string _appId;

    /// <param name="appId">
    /// Application id sent to feedbackd, which uses it for per-application settings.
    /// Defaults to the entry assembly name.
    /// </param>
    public LinuxVibration(string? appId = null)
    {
        _appId = string.IsNullOrWhiteSpace(appId) ? Feedbackd.DefaultAppId : appId;
    }

    /// <inheritdoc />
    public bool IsSupported => Feedbackd.HasInterface(Feedbackd.HapticInterface);

    /// <inheritdoc />
    public void Vibrate() => Vibrate(s_defaultDuration);

    /// <inheritdoc />
    public void Vibrate(TimeSpan duration)
    {
        if (!IsSupported)
            return;

        if (duration <= TimeSpan.Zero)
            return;

        if (duration > s_maxDuration)
            duration = s_maxDuration;

        // Full strength for the whole duration; feedbackd caps it with its
        // max-haptic-strength setting.
        var milliseconds = (uint)Math.Max(1, Math.Round(duration.TotalMilliseconds));
        SendPattern(magnitude: 1.0, milliseconds);
    }

    /// <inheritdoc />
    public void Cancel()
    {
        if (!IsSupported)
            return;

        // An empty pattern ends the running vibration.
        SendPattern(magnitude: 0, milliseconds: 0);
    }

    // Vibrate(s app_id, a(du) pattern) -> b success
    // milliseconds == 0 writes an empty pattern.
    void SendPattern(double magnitude, uint milliseconds)
    {
        Feedbackd.Send(
            connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: Feedbackd.BusName,
                    path: Feedbackd.ObjectPath,
                    @interface: Feedbackd.HapticInterface,
                    member: "Vibrate",
                    signature: "sa(du)");

                writer.WriteString(_appId);

                var pattern = writer.WriteArrayStart(DBusType.Struct);
                if (milliseconds > 0)
                {
                    writer.WriteStructureStart();
                    writer.WriteDouble(magnitude);
                    writer.WriteUInt32(milliseconds);
                }
                writer.WriteArrayEnd(pattern);

                return writer.CreateMessage();
            },
            static (Message message, object? state) => message.GetBodyReader().ReadBool(),
            static success =>
            {
                // false: profile set to silent, no vibration motor, or motor busy with an event
                if (!success)
                    Debug.WriteLine("[LinuxVibration] feedbackd refused the vibration");
            });
    }
}