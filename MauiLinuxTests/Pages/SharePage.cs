using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace MauiLinuxTests.Pages;

public class SharePage : TestPage
{
    public SharePage(IShare share) : base("Share", share)
    {
        AddNote("Sous Linux : choix de la cible (kdialog / zenity) parmi Email, KDE Connect, GSConnect, Bluetooth et « Ouvrir avec… ». " +
                "Fermer le choix ne doit pas produire d'erreur.");

        var title = AddEntry("Titre", "Test de partage");
        var text = AddEntry("Texte", "Bonjour depuis MauiLinuxTests");
        var uri = AddEntry("Lien", "https://github.com/cedric56/MauiLinuxTests");

        AddSection("Texte et lien");
        AddButton("Texte + lien", async () =>
        {
            await share.RequestAsync(new ShareTextRequest { Title = title.Text, Subject = title.Text, Text = text.Text, Uri = uri.Text });
            Log("RequestAsync(texte + lien) terminé");
        });

        AddButton("Texte seul", async () =>
        {
            await share.RequestAsync(new ShareTextRequest { Title = title.Text, Text = text.Text });
            Log("RequestAsync(texte) terminé");
        });

        AddButton("Lien seul", async () =>
        {
            await share.RequestAsync(new ShareTextRequest { Title = title.Text, Uri = uri.Text });
            Log("RequestAsync(lien) terminé");
        });

        AddSection("Fichiers");
        AddButton("Un fichier texte généré", async () =>
        {
            var file = CreateFile("note.txt", $"{text.Text}\n{DateTime.Now:O}\n");
            await share.RequestAsync(new ShareFileRequest { Title = title.Text, File = new ShareFile(file) });
            Log($"RequestAsync(fichier) terminé : {file}");
        });

        AddButton("Deux fichiers générés", async () =>
        {
            var files = new[]
            {
                CreateFile("note.txt", $"{text.Text}\n"),
                CreateFile("data.csv", "id;nom\n1;Linux\n2;MAUI\n"),
            };
            await share.RequestAsync(new ShareMultipleFilesRequest
            {
                Title = title.Text,
                Files = files.Select(f => new ShareFile(f)).ToList(),
            });
            Log($"RequestAsync(2 fichiers) terminé : {string.Join(", ", files)}");
        });

        AddButton("Fichier inexistant (erreur attendue)", async () =>
        {
            await share.RequestAsync(new ShareFileRequest { Title = title.Text, File = new ShareFile("/tmp/does-not-exist.txt") });
            Log("RequestAsync(fichier inexistant) terminé sans erreur");
        });
    }

    static string CreateFile(string name, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "maui-share-test");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
