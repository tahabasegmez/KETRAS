using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Ketras.Utilities;

/// <summary>A kind of file the dialogs offer.</summary>
/// <param name="Name">Shown to the operator, e.g. "QGroundControl plan".</param>
/// <param name="Extension">Without the dot, e.g. "plan".</param>
public sealed record FileKind(string Name, string Extension);

/// <summary>Strategy: asks the operator for a file to open or save; null when they cancel. Tests answer without a dialog.</summary>
public interface IFileDialogService
{
    /// <summary>A file to open.</summary>
    Task<string?> OpenAsync(string title, FileKind kind);

    /// <summary>A file to save, proposed as <paramref name="suggestedName"/>.</summary>
    Task<string?> SaveAsync(string title, FileKind kind, string suggestedName);
}

/// <summary>Adapter: <see cref="IFileDialogService"/> over the main window's storage provider.</summary>
public sealed class AvaloniaFileDialogService : IFileDialogService
{
    /// <inheritdoc />
    public async Task<string?> OpenAsync(string title, FileKind kind)
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, FileTypeFilter = [Filter(kind)] });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <inheritdoc />
    public async Task<string?> SaveAsync(string title, FileKind kind, string suggestedName)
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = kind.Extension,
            FileTypeChoices = [Filter(kind)]
        });
        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType Filter(FileKind kind) => new(kind.Name) { Patterns = [$"*.{kind.Extension}"] };

    private static IStorageProvider? Storage() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is Window window ? window.StorageProvider : null;
}
