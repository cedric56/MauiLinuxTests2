using Microsoft.Maui.Devices;

namespace MauiLinuxTests.Pages;

public class VibrationPage : TestPage
{
    public VibrationPage(IVibration vibration, IHapticFeedback hapticFeedback)
        : base("Vibration & haptique", vibration)
    {
        AddNote("Sous Linux : feedbackd (Phosh, postmarketOS, Mobian…) et un vibreur présent dans /sys/class/input. " +
                "Sur un PC de bureau, IsSupported vaut false.");

        AddSection("Vibration");
        AddButton("IsSupported", () => Log($"Vibration.IsSupported : {vibration.IsSupported}"));
        AddButton("Vibrate() — 500 ms", () =>
        {
            vibration.Vibrate();
            Log("Vibrate()");
        });
        AddButton("Vibrate(2 s)", () =>
        {
            vibration.Vibrate(TimeSpan.FromSeconds(2));
            Log("Vibrate(2 s)");
        });
        AddButton("Vibrate(10 s) — limité à 5 s", () =>
        {
            vibration.Vibrate(TimeSpan.FromSeconds(10));
            Log("Vibrate(10 s)");
        });
        AddButton("Cancel()", () =>
        {
            vibration.Cancel();
            Log("Cancel()");
        });

        AddSection("Retour haptique");
        AddNote($"Implémentation : {hapticFeedback.GetType().FullName}");
        AddButton("IsSupported", () => Log($"HapticFeedback.IsSupported : {hapticFeedback.IsSupported}"));
        AddButton("Perform(Click)", () =>
        {
            hapticFeedback.Perform(HapticFeedbackType.Click);
            Log("Perform(Click)");
        });
        AddButton("Perform(LongPress)", () =>
        {
            hapticFeedback.Perform(HapticFeedbackType.LongPress);
            Log("Perform(LongPress)");
        });
    }
}
