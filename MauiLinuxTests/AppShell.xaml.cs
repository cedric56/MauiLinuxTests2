using Microsoft.Extensions.DependencyInjection;
using MauiLinuxTests.Pages;

namespace MauiLinuxTests
{
    public partial class AppShell : Shell
    {
        readonly IServiceProvider _services;

        public AppShell(IServiceProvider services)
        {
            _services = services;
            InitializeComponent();

            AddPage<PlatformPage>("Plateforme");
            AddPage<AppInfoPage>("AppInfo");
            AddPage<AppActionsPage>("AppActions");
            AddPage<FlashlightPage>("Flashlight");
            AddPage<GeolocationPage>("Geolocation");
            AddPage<VibrationPage>("Vibration & haptique");
            AddPage<MediaPickerPage>("MediaPicker");
            AddPage<SecureStoragePage>("SecureStorage");
            AddPage<SharePage>("Share");
        }

        void AddPage<TPage>(string title) where TPage : Page =>
            Items.Add(new ShellContent
            {
                Title = title,
                Route = typeof(TPage).Name,
                // Created on first display, by DI, on the UI thread.
                ContentTemplate = new DataTemplate(() => _services.GetRequiredService<TPage>()),
            });
    }
}
