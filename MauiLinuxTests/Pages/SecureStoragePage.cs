using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Storage;
using Microsoft.Maui.Storage;

namespace MauiLinuxTests.Pages;

public class SecureStoragePage : TestPage
{
    public SecureStoragePage(ISecureStorage secureStorage) : base("SecureStorage", secureStorage)
    {
        AddNote($"Sous Linux : secret maître du portail Secret (trousseau GNOME / KWallet), valeurs chiffrées en AES-GCM. " +
                $"Hors Flatpak, un fichier {TestServices.ApplicationId}.desktop doit exister pour que le portail identifie l'app.");

        if (secureStorage is LinuxSecureStorage linux)
            AddNote($"Backend : {linux.Backend}");

        var key = AddEntry("Clé", "token");
        var value = AddEntry("Valeur", "secret-" + Guid.NewGuid().ToString("N")[..8]);

        AddButton("SetAsync", async () =>
        {
            await secureStorage.SetAsync(key.Text ?? string.Empty, value.Text ?? string.Empty);
            Log($"SetAsync(« {key.Text} ») : OK");
        });

        AddButton("GetAsync", async () =>
        {
            var result = await secureStorage.GetAsync(key.Text ?? string.Empty);
            Log($"GetAsync(« {key.Text} ») : {(result is null ? "null" : $"« {result} »")}");
        });

        AddButton("Remove", () => Log($"Remove(« {key.Text} ») : {secureStorage.Remove(key.Text ?? string.Empty)}"));

        AddButton("RemoveAll", () =>
        {
            secureStorage.RemoveAll();
            Log("RemoveAll : OK");
        });

        AddButton("Aller-retour (Set puis Get)", async () =>
        {
            var testKey = "roundtrip";
            var expected = "é€😀 " + DateTime.Now.ToString("O");
            await secureStorage.SetAsync(testKey, expected);
            var actual = await secureStorage.GetAsync(testKey);
            Log(actual == expected ? "Aller-retour : OK" : $"Aller-retour : ÉCHEC (lu « {actual} »)");
        });
    }
}
