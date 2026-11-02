using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace A_Capella.App;

public enum MessageButtons { Ok, YesNo, YesNoCancel }
public enum MessageResult { Ok, Yes, No, Cancel }

/// <summary>Modal message boxes and file pickers. Avalonia has neither built in, and its dialogs are
/// all asynchronous, so every caller awaits these.</summary>
public static class Dialogs
{
    public static async Task<MessageResult> ShowMessageAsync(Window? owner, string message, string title,
        MessageButtons buttons = MessageButtons.Ok)
    {
        var result = MessageResult.Cancel;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        void AddButton(string text, MessageResult value, bool isDefault = false)
        {
            var button = new Button { Content = text, MinWidth = 76, Padding = new Thickness(14, 5), IsDefault = isDefault };
            if (isDefault) button.Classes.Add("strong");
            button.Click += (_, _) => { result = value; window.Close(); };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageButtons.Ok: AddButton("OK", MessageResult.Ok, true); break;
            case MessageButtons.YesNo: AddButton("Yes", MessageResult.Yes, true); AddButton("No", MessageResult.No); break;
            case MessageButtons.YesNoCancel: AddButton("Yes", MessageResult.Yes, true); AddButton("No", MessageResult.No); AddButton("Cancel", MessageResult.Cancel); break;
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, row },
        };

        if (owner is null) window.Show(); else await window.ShowDialog(owner);
        if (owner is null) await WaitForCloseAsync(window);
        return result;
    }

    private static Task WaitForCloseAsync(Window window)
    {
        var tcs = new TaskCompletionSource();
        window.Closed += (_, _) => tcs.TrySetResult();
        return tcs.Task;
    }

    private static FilePickerFileType Type(string name, params string[] extensions) =>
        new(name) { Patterns = extensions.Select(e => "*" + e).ToList() };

    public static readonly FilePickerFileType MediaFiles = Type("Media files", ".mp4", ".mov", ".mkv", ".wav", ".mp3", ".m4a");
    public static readonly FilePickerFileType ProjectFiles = Type("A-Capella project", ".a-capella.json", ".json");
    public static readonly FilePickerFileType Mp4Files = Type("MP4 video", ".mp4");
    public static readonly FilePickerFileType ImageFiles = Type("Images", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp");

    /// <summary>The paths picked (local file system), or empty if cancelled.</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(Window owner, string title, FilePickerFileType filter, bool multiple = false)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = new[] { filter, FilePickerFileTypes.All },
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> PickFileAsync(Window owner, string title, FilePickerFileType filter) =>
        (await PickFilesAsync(owner, title, filter)).FirstOrDefault();

    /// <summary>The path chosen for saving, or null if cancelled.</summary>
    public static async Task<string?> PickSavePathAsync(Window owner, string title, FilePickerFileType filter,
        string defaultExtension, string? suggestedFileName = null, string? initialDirectory = null)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = defaultExtension.TrimStart('.'),
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[] { filter },
            ShowOverwritePrompt = true,
        };
        if (initialDirectory is not null)
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
        var file = await owner.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }
}
