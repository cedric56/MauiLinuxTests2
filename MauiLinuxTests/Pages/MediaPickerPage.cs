using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;

namespace MauiLinuxTests.Pages;

public class MediaPickerPage : TestPage
{
    readonly Image _preview = new() { HeightRequest = 240, Aspect = Aspect.AspectFit, IsVisible = false };

    public MediaPickerPage(IMediaPicker mediaPicker) : base("MediaPicker", mediaPicker)
    {
        AddNote("Sélection : dialogue de fichiers GTK4. Capture photo : portail Camera + GStreamer (pipewiresrc), sans aperçu.");

        Body.Add(_preview);

        AddSection("Sélection");
        AddButton("PickPhotoAsync", async () => ShowResult("PickPhotoAsync", await mediaPicker.PickPhotoAsync(new MediaPickerOptions { Title = "Choisir une photo" })));
        AddButton("PickPhotosAsync", async () => ShowResults("PickPhotosAsync", await mediaPicker.PickPhotosAsync(new MediaPickerOptions { Title = "Choisir des photos" })));
        AddButton("PickVideoAsync", async () => ShowResult("PickVideoAsync", await mediaPicker.PickVideoAsync(new MediaPickerOptions { Title = "Choisir une vidéo" })));
        AddButton("PickVideosAsync", async () => ShowResults("PickVideosAsync", await mediaPicker.PickVideosAsync(new MediaPickerOptions { Title = "Choisir des vidéos" })));

        AddSection("Capture");
        AddButton("IsCaptureSupported", () => Log($"IsCaptureSupported : {mediaPicker.IsCaptureSupported}"));
        AddButton("CapturePhotoAsync", async () =>
        {
            Log("Capture en cours (autorisation caméra possible)…");
            ShowResult("CapturePhotoAsync", await mediaPicker.CapturePhotoAsync(new MediaPickerOptions { Title = "Prendre une photo" }));
        });
        AddButton("CaptureVideoAsync", async () => ShowResult("CaptureVideoAsync", await mediaPicker.CaptureVideoAsync(new MediaPickerOptions { Title = "Filmer" })));
    }

    void ShowResult(string call, FileResult? result)
    {
        if (result is null)
        {
            Log($"{call} : annulé (null)");
            return;
        }

        Log($"{call} : {result.FullPath} ({result.ContentType}, {SizeOf(result.FullPath)})");
        Preview(result);
    }

    void ShowResults(string call, List<FileResult>? results)
    {
        results ??= new List<FileResult>();
        Log($"{call} : {results.Count} fichier(s)");
        foreach (var result in results)
            Log($"  {result.FullPath} ({SizeOf(result.FullPath)})");

        if (results.Count > 0)
            Preview(results[0]);
    }

    void Preview(FileResult result)
    {
        var isImage = result.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            || new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(Path.GetExtension(result.FullPath).ToLowerInvariant());

        _preview.IsVisible = isImage;
        _preview.Source = isImage ? ImageSource.FromFile(result.FullPath) : null;
    }

    static string SizeOf(string path) =>
        File.Exists(path) ? $"{new FileInfo(path).Length / 1024.0:F1} Ko" : "fichier introuvable";
}
