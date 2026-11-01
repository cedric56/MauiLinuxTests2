using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Media;
using Microsoft.Maui.Platforms.Linux.Essentials.AppModel;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.AppModel;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.DataTransfer;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Devices;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Media;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Sensors;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Storage;
using Microsoft.Maui.Storage;
using MauiLinuxTests.Pages;

namespace MauiLinuxTests;

/// <summary>
/// Registers the Essentials services tested by the pages: the Linux implementations
/// when running on Linux, the default .NET MAUI implementations elsewhere,
/// so the same pages can be compared on every platform.
/// </summary>
public static class TestServices
{
    /// <summary>
    /// Application id: must match the .desktop file basename on Linux
    /// (host app registration for the Secret portal, feedbackd, desktop actions).
    /// </summary>
    public const string ApplicationId = "com.companyname.mauilinuxtests";

    public static IServiceCollection AddTestServices(this IServiceCollection services)
    {
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IAppInfo, LinuxAppInfo>();
            // Resolved by its page, on the UI thread: capture the UI dispatcher now.
            // (Actions are activated on a D-Bus thread, where GetForCurrentThread() returns null.)
            services.AddSingleton<IAppActions>(_ =>
            {
                var dispatcher = Dispatcher.GetForCurrentThread()
                    ?? throw new InvalidOperationException("IAppActions must be resolved on the UI thread");
                return new LinuxAppActions(new Gtk4LinuxAppActionsHost(action => dispatcher.Dispatch(action)));
            });
            services.AddSingleton<IFlashlight, LinuxFlashlight>();
            services.AddSingleton<IGeolocation, LinuxGeolocation>();
            services.AddSingleton<IVibration>(_ => new LinuxVibration(ApplicationId));
            services.AddSingleton<IHapticFeedback>(_ => new LinuxHapticFeedback(ApplicationId));
            services.AddSingleton<IMediaPicker, LinuxMediaPicker>();
            services.AddSingleton<ISecureStorage>(_ => new LinuxSecureStorage(ApplicationId));
            services.AddSingleton<IShare>(_ => new LinuxShare());
        }
        else
        {
            services.AddSingleton(AppInfo.Current);
            services.AddSingleton(AppActions.Current);
            services.AddSingleton(Flashlight.Default);
            services.AddSingleton(Geolocation.Default);
            services.AddSingleton(Vibration.Default);
            services.AddSingleton(HapticFeedback.Default);
            services.AddSingleton(MediaPicker.Default);
            services.AddSingleton(SecureStorage.Default);
            services.AddSingleton(Share.Default);
        }

        services.AddTransient<PlatformPage>();
        services.AddTransient<AppInfoPage>();
        services.AddTransient<AppActionsPage>();
        services.AddTransient<FlashlightPage>();
        services.AddTransient<GeolocationPage>();
        services.AddTransient<VibrationPage>();
        services.AddTransient<MediaPickerPage>();
        services.AddTransient<SecureStoragePage>();
        services.AddTransient<SharePage>();

        return services;
    }
}