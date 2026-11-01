using Microsoft.Maui.Devices;
using Tmds.DBus.Protocol;

namespace Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Devices;

/// <summary>
/// Haptic feedback through feedbackd's event based <c>org.sigxcpu.Feedback</c> interface.
/// </summary>
/// <remarks>
/// Desktops without feedbackd (no vibration motor) report <see cref="IsSupported"/> = false.
/// No UI toolkit dependency: works for both the Gtk4 and the Avalonia backends.
/// </remarks>
public class LinuxHapticFeedback : IHapticFeedback
{
    // Event naming spec: there is no long-press event, "button-pressed" is the
    // short tactile feedback themes map to the vibration motor.
    const string ClickEvent = "button-pressed";
    const string LongPressEvent = "button-pressed";

    // "quiet" = haptic (and LED) only: a tap must not play a sound.
    const string Profile = "quiet";

    readonly string _appId;

    /// <param name="appId">
    /// Application id sent to feedbackd, which uses it for per-application settings.
    /// Defaults to the entry assembly name.
    /// </param>
    public LinuxHapticFeedback(string? appId = null)
    {
        _appId = string.IsNullOrWhiteSpace(appId) ? Feedbackd.DefaultAppId : appId;
    }

    /// <inheritdoc />
    public bool IsSupported => Feedbackd.HasInterface(Feedbackd.FeedbackInterface);

    /// <inheritdoc />
    public void Perform(HapticFeedbackType type)
    {
        if (!IsSupported)
            return;

        var feedbackEvent = type == HapticFeedbackType.LongPress ? LongPressEvent : ClickEvent;

        // TriggerFeedback(s app_id, s event, a{sv} hints, i timeout) -> u id
        Feedbackd.Send(
            connection =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: Feedbackd.BusName,
                    path: Feedbackd.ObjectPath,
                    @interface: Feedbackd.FeedbackInterface,
                    member: "TriggerFeedback",
                    signature: "ssa{sv}i");

                writer.WriteString(_appId);
                writer.WriteString(feedbackEvent);

                var hints = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("profile");
                writer.WriteVariantString(Profile);
                writer.WriteDictionaryEnd(hints);

                // -1: use the duration defined by the feedback theme
                writer.WriteInt32(-1);

                return writer.CreateMessage();
            },
            static (Message message, object? state) => message.GetBodyReader().ReadUInt32());
    }
}