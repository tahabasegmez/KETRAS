using Ketras.Utilities;

namespace Ketras.Tests.Support;

/// <summary>Answers the file dialogs with a fixed path.</summary>
internal sealed class FixedFileDialogs(string? path) : IFileDialogService
{
    public Task<string?> OpenAsync(string title, FileKind kind) => Task.FromResult(path);

    public Task<string?> SaveAsync(string title, FileKind kind, string suggestedName) => Task.FromResult(path);
}
