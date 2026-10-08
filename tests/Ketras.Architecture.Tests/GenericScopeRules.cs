using System.Reflection;
using System.Text.RegularExpressions;

namespace Ketras.Architecture.Tests;

/// <summary>
/// docs/decisions/0004: the repository names no team, competition or one deployment. The scan covers the code, the
/// plugins, the samples and the tools.
/// </summary>
public sealed partial class GenericScopeRules
{
    private static readonly string[] Scanned = ["src", "plugins", "samples", "tools", "tests"];
    private static readonly string[] Extensions = [".cs", ".axaml", ".json", ".py", ".sh", ".ps1", ".csproj", ".resx"];

    [Fact]
    public void No_source_file_names_a_team_or_a_competition()
    {
        var root = RepositoryRoot();
        var hits = Scanned
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Where(path => Extensions.Contains(Path.GetExtension(path)) && !IsBuildOutput(path) && !path.EndsWith(nameof(GenericScopeRules) + ".cs", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => Forbidden().IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(root, entry.path)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToList();

        Assert.True(hits.Count == 0, string.Join(Environment.NewLine, hits));
    }

    // Generated MAVLink code carries the upstream XML's descriptions, which are not ours to word.
    private static bool IsBuildOutput(string path) =>
        path.EndsWith(".g.cs", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    internal static string RepositoryRoot() =>
        typeof(GenericScopeRules).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value
        ?? throw new InvalidOperationException("RepositoryRoot metadata missing");

    [GeneratedRegex(@"\b(sartek|teknofest|referee|kamikaze|enemy|enemies|gorev|competition)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
