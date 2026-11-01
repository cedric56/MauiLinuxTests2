using Microsoft.Maui.Devices.Sensors;

namespace MauiLinuxTests.Pages;

public class GeolocationPage : TestPage
{
    CancellationTokenSource? _cts;

    public GeolocationPage(IGeolocation geolocation) : base("Geolocation", geolocation)
    {
        // Singleton service, page cached by the Shell: listen only while the page is shown.
        EventHandler<GeolocationLocationChangedEventArgs> onChanged = (_, e) => Log($"LocationChanged : {Format(e.Location)}");
        EventHandler<GeolocationListeningFailedEventArgs> onFailed = (_, e) => Log($"ListeningFailed : {e.Error}");
        Loaded += (_, _) =>
        {
            geolocation.LocationChanged += onChanged;
            geolocation.ListeningFailed += onFailed;
        };
        Unloaded += (_, _) =>
        {
            geolocation.LocationChanged -= onChanged;
            geolocation.ListeningFailed -= onFailed;
            _cts?.Cancel();
        };

        AddNote("Sous Linux : GeoClue2 sur le bus système. GeoClue peut demander l'autorisation à l'utilisateur.");

        AddButton("GetLastKnownLocationAsync", async () =>
            Log($"Dernière position : {Format(await geolocation.GetLastKnownLocationAsync())}"));

        AddButton("GetLocationAsync (précision moyenne, 30 s)", async () =>
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Log("Recherche de la position…");
            var location = await geolocation.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(30)), _cts.Token);
            Log($"Position : {Format(location)}");
        });

        AddButton("Annuler la recherche", () =>
        {
            _cts?.Cancel();
            Log("Recherche annulée");
        });

        AddButton("Démarrer l'écoute", async () =>
        {
            var started = await geolocation.StartListeningForegroundAsync(
                new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2)));
            Log($"StartListeningForegroundAsync : {started}");
        });

        AddButton("Arrêter l'écoute", () =>
        {
            geolocation.StopListeningForeground();
            Log($"StopListeningForeground, IsListeningForeground : {geolocation.IsListeningForeground}");
        });
    }

    static string Format(Location? location) => location is null
        ? "aucune"
        : $"{location.Latitude:F5}, {location.Longitude:F5} (±{location.Accuracy?.ToString("F0") ?? "?"} m, {location.Timestamp:HH:mm:ss})";
}
