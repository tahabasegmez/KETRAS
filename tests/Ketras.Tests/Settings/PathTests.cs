using Ketras.Settings;
using Ketras.Terrain;

namespace Ketras.Tests.Settings;

public sealed class PathTests
{
    [Fact]
    public void The_default_folders_are_absolute_even_before_they_exist()
    {
        // On Linux the platform folders may not exist yet (no ~/.config on a fresh account); the paths must still be rooted.
        Assert.True(Path.IsPathRooted(StationPaths.Default.ConfigDirectory), StationPaths.Default.ConfigDirectory);
        Assert.True(Path.IsPathRooted(StationPaths.Default.DataDirectory), StationPaths.Default.DataDirectory);
        Assert.True(Path.IsPathRooted(TerrainTileStore.DefaultDirectory), TerrainTileStore.DefaultDirectory);
    }
}
