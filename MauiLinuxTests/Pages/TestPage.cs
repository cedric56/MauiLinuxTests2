using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MauiLinuxTests.Pages;

/// <summary>
/// Base layout of the test pages: a header naming the implementation under test,
/// the page controls, and a log (newest entry first) of every call and its result.
/// Each button catches and logs exceptions, so a failing call never closes the app.
/// </summary>
public abstract class TestPage : ContentPage
{
    readonly Label _log = new()
    {
        FontSize = 13,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    protected TestPage(string title, object? service)
    {
        Title = title;

        var clear = new Button { Text = "Effacer le journal" };
        clear.Clicked += (_, _) => _log.Text = string.Empty;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new Label { Text = title, FontSize = 24, FontAttributes = FontAttributes.Bold },
                    new Label
                    {
                        Text = service is null ? "Implémentation : —" : $"Implémentation : {service.GetType().FullName}",
                        FontSize = 12,
                        TextColor = Colors.Gray,
                    },
                    Body,
                    new Label { Text = "Journal", FontSize = 16, FontAttributes = FontAttributes.Bold },
                    new Border { Padding = new Thickness(10), Content = _log },
                    clear,
                },
            },
        };
    }

    /// <summary>Container of the page controls.</summary>
    protected VerticalStackLayout Body { get; } = new() { Spacing = 10 };

    protected Label AddSection(string text)
    {
        var label = new Label { Text = text, FontSize = 16, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) };
        Body.Add(label);
        return label;
    }

    protected Label AddNote(string text)
    {
        var label = new Label { Text = text, FontSize = 12, TextColor = Colors.Gray };
        Body.Add(label);
        return label;
    }

    protected Entry AddEntry(string placeholder, string text = "")
    {
        var entry = new Entry { Placeholder = placeholder, Text = text };
        Body.Add(entry);
        return entry;
    }

    protected Button AddButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.Clicked += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log($"{text} → {ex.GetType().Name} : {ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        Body.Add(button);
        return button;
    }

    protected Button AddButton(string text, Action action) =>
        AddButton(text, () =>
        {
            action();
            return Task.CompletedTask;
        });

    /// <summary>Adds a line to the log; safe from any thread.</summary>
    protected void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

        if (Dispatcher.IsDispatchRequired)
            Dispatcher.Dispatch(() => Append(line));
        else
            Append(line);
    }

    void Append(string line) =>
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : line + Environment.NewLine + _log.Text;
}
