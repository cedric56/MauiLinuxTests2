using Microsoft.Maui.ApplicationModel;

namespace MauiLinuxTests.Pages;

public class AppInfoPage : TestPage
{
    public AppInfoPage(IAppInfo appInfo) : base("AppInfo", appInfo)
    {
        AddButton("Lire les propriétés", () =>
        {
            Log($"Name : {appInfo.Name}");
            Log($"PackageName : {appInfo.PackageName}");
            Log($"VersionString : {appInfo.VersionString}");
            Log($"Version : {appInfo.Version}");
            Log($"BuildString : {appInfo.BuildString}");
            Log($"RequestedTheme : {appInfo.RequestedTheme}");
            Log($"PackagingModel : {appInfo.PackagingModel}");
            Log($"RequestedLayoutDirection : {appInfo.RequestedLayoutDirection}");
        });

        AddNote("Ouvre le panneau de configuration du bureau (gnome-control-center, systemsettings…).");
        AddButton("ShowSettingsUI()", () =>
        {
            appInfo.ShowSettingsUI();
            Log("ShowSettingsUI() appelé");
        });
    }
}
