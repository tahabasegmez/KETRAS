namespace Ketras.Tests.Support;

/// <summary>
/// Test classes that touch Avalonia's dispatcher run here, one at a time and apart from the parallel tests: a plain
/// test on a pool thread and a headless test on the UI thread must never pump the same dispatcher at once.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaCollection
{
    public const string Name = "Avalonia";
}
