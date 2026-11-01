using Microsoft.Maui.Devices;

namespace MauiLinuxTests.Pages;

public class FlashlightPage : TestPage
{
    public FlashlightPage(IFlashlight flashlight) : base("Flashlight", flashlight)
    {
        AddNote("Sous Linux : LED /sys/class/leds/*:torch ou *:flash, pilotée via logind (téléphones Linux).");

        AddButton("IsSupportedAsync", async () => Log($"IsSupportedAsync : {await flashlight.IsSupportedAsync()}"));

        AddButton("Allumer", async () =>
        {
            await flashlight.TurnOnAsync();
            Log("TurnOnAsync : OK");
        });

        AddButton("Éteindre", async () =>
        {
            await flashlight.TurnOffAsync();
            Log("TurnOffAsync : OK");
        });

        AddButton("Clignoter 3 fois", async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await flashlight.TurnOnAsync();
                await Task.Delay(300);
                await flashlight.TurnOffAsync();
                await Task.Delay(300);
            }
            Log("Clignotement terminé");
        });
    }
}
