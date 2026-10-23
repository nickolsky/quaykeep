using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Quaykeep.Views;

/// <summary>WPF's Clipboard on Avalonia: the clipboard of the window the user works in.</summary>
public static class Clipboard
{
    private static IClipboard? Current => TopLevel.GetTopLevel(UiWait.ActiveWindow())?.Clipboard;

    public static void SetText(string text)
    {
        if (Current is { } c) UiWait.Run(c.SetTextAsync(text));
    }

    public static string GetText() => Current is { } c ? UiWait.Run(c.TryGetTextAsync()) ?? "" : "";

    public static bool ContainsText() => GetText().Length > 0;
}

/// <summary>
/// WPF's file dialogs on Avalonia (the system pickers): the same properties, synchronous ShowDialog. Filter takes the
/// WPF form "Name|*.a;*.b|Other|*.*".
/// </summary>
public abstract class FileDialogBase
{
    public string Title { get; set; } = "";
    public string Filter { get; set; } = "";
    public string? InitialDirectory { get; set; }
    public string FileName { get; set; } = "";

    protected static IStorageProvider? Provider(Window? owner) => TopLevel.GetTopLevel(owner ?? UiWait.ActiveWindow())?.StorageProvider;

    protected List<FilePickerFileType> FileTypes()
    {
        var parts = Filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        return types;
    }

    protected static IStorageFolder? Folder(IStorageProvider provider, string? path) =>
        string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : UiWait.Run(provider.TryGetFolderFromPathAsync(path));
}

public sealed class OpenFileDialog : FileDialogBase
{
    public bool Multiselect { get; set; }
    public string[] FileNames { get; private set; } = [];

    public bool? ShowDialog(Window? owner = null)
    {
        if (Provider(owner) is not { } provider) return false;
        var files = UiWait.Run(provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Title,
            AllowMultiple = Multiselect,
            FileTypeFilter = FileTypes() is { Count: > 0 } t ? t : null,
            SuggestedStartLocation = Folder(provider, InitialDirectory),
        }));
        FileNames = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (FileNames.Length == 0) return false;
        FileName = FileNames[0];
        return true;
    }
}

public sealed class SaveFileDialog : FileDialogBase
{
    public string? DefaultExt { get; set; }

    public bool? ShowDialog(Window? owner = null)
    {
        if (Provider(owner) is not { } provider) return false;
        var start = InitialDirectory ?? (Path.IsPathRooted(FileName) ? Path.GetDirectoryName(FileName) : null);
        var file = UiWait.Run(provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Title,
            SuggestedFileName = Path.GetFileName(FileName),
            DefaultExtension = DefaultExt?.TrimStart('.'),
            FileTypeChoices = FileTypes() is { Count: > 0 } t ? t : null,
            SuggestedStartLocation = Folder(provider, start),
            ShowOverwritePrompt = true,
        }));
        if (file?.TryGetLocalPath() is not { } path) return false;
        FileName = path;
        return true;
    }
}

public sealed class OpenFolderDialog
{
    public string Title { get; set; } = "";
    public string? InitialDirectory { get; set; }
    public string FolderName { get; set; } = "";

    public bool? ShowDialog(Window? owner = null)
    {
        if (TopLevel.GetTopLevel(owner ?? UiWait.ActiveWindow())?.StorageProvider is not { } provider) return false;
        var start = string.IsNullOrEmpty(InitialDirectory) || !Directory.Exists(InitialDirectory)
            ? null : UiWait.Run(provider.TryGetFolderFromPathAsync(InitialDirectory));
        var folders = UiWait.Run(provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Title, SuggestedStartLocation = start }));
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return false;
        FolderName = path;
        return true;
    }
}
