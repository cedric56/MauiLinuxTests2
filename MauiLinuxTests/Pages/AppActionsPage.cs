using Microsoft.Maui.ApplicationModel;

namespace MauiLinuxTests.Pages;

public class AppActionsPage : TestPage
{
    public AppActionsPage(IAppActions appActions) : base("AppActions", appActions)
    {
        // Singleton service, page cached by the Shell: listen only while the page is shown.
        EventHandler<AppActionEventArgs> onActivated = (_, e) =>
            Log($"Action déclenchée : {e.AppAction.Id} « {e.AppAction.Title} »");
        Loaded += (_, _) => appActions.AppActionActivated += onActivated;
        Unloaded += (_, _) => appActions.AppActionActivated -= onActivated;

        AddNote("Sous Linux, les actions apparaissent dans le menu contextuel de l'icône de l'app (dock, lanceur). " +
                "Un clic doit ajouter une ligne « Action déclenchée » au journal.");

        AddButton("IsSupported", () => Log($"IsSupported : {appActions.IsSupported}"));

        AddButton("Publier 3 actions", async () =>
        {
            await appActions.SetAsync(new[]
            {
                new AppAction("new_document", "Nouveau document", "Crée un document vide"),
                new AppAction("search", "Rechercher"),
                new AppAction("settings", "Paramètres", "Ouvre les paramètres"),
            });
            Log("SetAsync : 3 actions publiées");
        });

        AddButton("Publier une liste vide", async () =>
        {
            await appActions.SetAsync(Array.Empty<AppAction>());
            Log("SetAsync : liste vide publiée");
        });

        AddButton("GetAsync", async () =>
        {
            var actions = (await appActions.GetAsync()).ToList();
            Log($"GetAsync : {actions.Count} action(s)");
            foreach (var action in actions)
                Log($"  {action.Id} — {action.Title} {action.Subtitle}");
        });
    }
}
