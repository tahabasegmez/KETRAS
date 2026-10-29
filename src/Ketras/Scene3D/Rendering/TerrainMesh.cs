using TerrainGrid = Ketras.Model.Terrain.TerrainGrid;

namespace Ketras.Scene3D.Rendering;

/// <summary>
/// Chunked level-of-detail mesh of a terrain grid, built once on the CPU; <see cref="TerrainView"/> uploads and draws it.
/// Level 0 uses every grid point, so the nearest terrain shows the data at its full resolution; each further level
/// halves the point spacing. Skirts hang from every chunk edge so neighbours drawn at different levels leave no cracks.
/// </summary>
internal sealed class TerrainMesh
{
    public const int ChunkCells = 64;
    public const int FloatsPerVertex = 6;
    public static readonly int[] Strides = [1, 2, 4, 8, 16, 32];

    private const double EarthRadius = 6378137;

    private TerrainMesh(double anchorLatitude, double anchorLongitude, double cellMeters, IReadOnlyList<Chunk> chunks, string attribution)
    {
        AnchorLatitude = anchorLatitude;
        AnchorLongitude = anchorLongitude;
        CellMeters = cellMeters;
        Chunks = chunks;
        Attribution = attribution;
    }

    /// <summary>The grid's south-west corner; vertex positions are degrees relative to it.</summary>
    public double AnchorLatitude { get; }

    public double AnchorLongitude { get; }

    /// <summary>The longer side of one full-resolution cell, in metres.</summary>
    public double CellMeters { get; }

    public IReadOnlyList<Chunk> Chunks { get; }

    public string Attribution { get; }

    /// <summary>
    /// One block of up to <see cref="ChunkCells"/> × <see cref="ChunkCells"/> cells.
    /// Vertices hold (Δlongitude°, height AMSL, Δlatitude°, normal x, y, z); indices are 16-bit, one array per level.
    /// </summary>
    public sealed class Chunk
    {
        public required float[] Vertices { get; init; }
        public required ushort[][] Indices { get; init; }
        public required double MinDeltaLatitude { get; init; }
        public required double MaxDeltaLatitude { get; init; }
        public required double MinDeltaLongitude { get; init; }
        public required double MaxDeltaLongitude { get; init; }
        public required float MinHeight { get; init; }
        public required float MaxHeight { get; init; }

        public int Levels => Indices.Length;

        public int TriangleCount(int level) => Indices[level].Length / 3;
    }

    public static TerrainMesh Build(TerrainGrid grid)
    {
        var rows = grid.Rows;
        var columns = grid.Columns;
        var heights = grid.Heights.ToArray();
        var middleLatitude = grid.SouthLatitude + (rows - 1) * grid.LatitudeStep * 0.5;
        var metersPerLat = grid.LatitudeStep * Math.PI / 180 * EarthRadius;
        var metersPerLon = grid.LongitudeStep * Math.PI / 180 * EarthRadius * Math.Cos(middleLatitude * Math.PI / 180);

        float Height(int r, int c) => heights[(r * columns) + c];

        var chunks = new List<Chunk>();
        for (var r0 = 0; r0 < rows - 1; r0 += ChunkCells)
        {
            for (var c0 = 0; c0 < columns - 1; c0 += ChunkCells)
            {
                var cellRows = Math.Min(ChunkCells, rows - 1 - r0);
                var cellCols = Math.Min(ChunkCells, columns - 1 - c0);
                if (BuildChunk(r0, c0, cellRows, cellCols) is { } chunk)
                {
                    chunks.Add(chunk);
                }
            }
        }

        return new TerrainMesh(grid.SouthLatitude, grid.WestLongitude, Math.Max(metersPerLat, metersPerLon), chunks, grid.Attribution ?? string.Empty);

        Chunk? BuildChunk(int r0, int c0, int cellRows, int cellCols)
        {
            var width = cellCols + 1;
            var gridVertices = (cellRows + 1) * width;
            var vertices = new List<float>(gridVertices * FloatsPerVertex * 5 / 4);
            var valid = new bool[gridVertices];
            float min = float.MaxValue, max = float.MinValue;

            for (var i = 0; i <= cellRows; i++)
            {
                for (var j = 0; j <= cellCols; j++)
                {
                    int r = r0 + i, c = c0 + j;
                    var h = Height(r, c);
                    valid[(i * width) + j] = !float.IsNaN(h);
                    if (float.IsNaN(h))
                    {
                        h = 0;
                    }
                    else
                    {
                        min = Math.Min(min, h);
                        max = Math.Max(max, h);
                    }

                    var (nx, ny, nz) = Normal(r, c, h);
                    vertices.Add((float)(c * grid.LongitudeStep));
                    vertices.Add(h);
                    vertices.Add((float)(r * grid.LatitudeStep));
                    vertices.Add(nx);
                    vertices.Add(ny);
                    vertices.Add(nz);
                }
            }

            if (min > max)
            {
                return null;
            }

            var levels = new List<ushort[]>();
            foreach (var stride in Strides)
            {
                if (stride > 1 && stride > Math.Max(cellRows, cellCols))
                {
                    break;
                }

                levels.Add(BuildLevel(stride, cellRows, cellCols, width, valid, vertices));
            }

            var deepestSkirt = SkirtDepth(Strides[levels.Count - 1]);
            return new Chunk
            {
                Vertices = vertices.ToArray(),
                Indices = levels.ToArray(),
                MinDeltaLatitude = r0 * grid.LatitudeStep,
                MaxDeltaLatitude = (r0 + cellRows) * grid.LatitudeStep,
                MinDeltaLongitude = c0 * grid.LongitudeStep,
                MaxDeltaLongitude = (c0 + cellCols) * grid.LongitudeStep,
                MinHeight = min - deepestSkirt,
                MaxHeight = max
            };
        }

        (float X, float Y, float Z) Normal(int r, int c, float h)
        {
            float Sample(int rr, int cc)
            {
                rr = Math.Clamp(rr, 0, rows - 1);
                cc = Math.Clamp(cc, 0, columns - 1);
                var v = Height(rr, cc);
                return float.IsNaN(v) ? h : v;
            }

            var spanX = (Math.Min(c + 1, columns - 1) - Math.Max(c - 1, 0)) * metersPerLon;
            var spanZ = (Math.Min(r + 1, rows - 1) - Math.Max(r - 1, 0)) * metersPerLat;
            var dhdx = spanX > 0 ? (Sample(r, c + 1) - Sample(r, c - 1)) / spanX : 0;
            var dhdz = spanZ > 0 ? (Sample(r + 1, c) - Sample(r - 1, c)) / spanZ : 0;
            var length = Math.Sqrt((dhdx * dhdx) + 1 + (dhdz * dhdz));
            return ((float)(-dhdx / length), (float)(1 / length), (float)(-dhdz / length));
        }
    }

    private static float SkirtDepth(int stride) => 10f + (12f * stride);

    /// <summary>Triangles of one level (quads with a missing corner are left out) plus its skirt, whose vertices are appended.</summary>
    private static ushort[] BuildLevel(int stride, int cellRows, int cellCols, int width, bool[] valid, List<float> vertices)
    {
        var rowsAt = Positions(cellRows, stride);
        var colsAt = Positions(cellCols, stride);
        var indices = new List<ushort>(rowsAt.Count * colsAt.Count * 6);

        int Index(int i, int j) => (i * width) + j;

        for (var a = 0; a + 1 < rowsAt.Count; a++)
        {
            for (var b = 0; b + 1 < colsAt.Count; b++)
            {
                int i0 = rowsAt[a], i1 = rowsAt[a + 1], j0 = colsAt[b], j1 = colsAt[b + 1];
                int v00 = Index(i0, j0), v01 = Index(i0, j1), v10 = Index(i1, j0), v11 = Index(i1, j1);
                if (!valid[v00] || !valid[v01] || !valid[v10] || !valid[v11])
                {
                    continue;
                }

                indices.Add((ushort)v00); indices.Add((ushort)v10); indices.Add((ushort)v11);
                indices.Add((ushort)v00); indices.Add((ushort)v11); indices.Add((ushort)v01);
            }
        }

        var depth = SkirtDepth(stride);
        var edges = new List<(int I, int J)>[]
        {
            colsAt.Select(j => (0, j)).ToList(),
            colsAt.Select(j => (cellRows, j)).ToList(),
            rowsAt.Select(i => (i, 0)).ToList(),
            rowsAt.Select(i => (i, cellCols)).ToList()
        };

        foreach (var edge in edges)
        {
            for (var k = 0; k + 1 < edge.Count; k++)
            {
                var top0 = Index(edge[k].I, edge[k].J);
                var top1 = Index(edge[k + 1].I, edge[k + 1].J);
                if (!valid[top0] || !valid[top1])
                {
                    continue;
                }

                var bottom0 = AddLowered(vertices, top0, depth);
                var bottom1 = AddLowered(vertices, top1, depth);
                indices.Add((ushort)top0); indices.Add((ushort)top1); indices.Add((ushort)bottom1);
                indices.Add((ushort)top0); indices.Add((ushort)bottom1); indices.Add((ushort)bottom0);
            }
        }

        return indices.ToArray();
    }

    /// <summary>0, stride, 2·stride … and always the last cell edge, so a chunk's border is kept at every level.</summary>
    private static List<int> Positions(int cells, int stride)
    {
        var positions = new List<int>(cells / stride + 2);
        for (var p = 0; p < cells; p += stride)
        {
            positions.Add(p);
        }

        positions.Add(cells);
        return positions;
    }

    private static int AddLowered(List<float> vertices, int source, float depth)
    {
        var index = vertices.Count / FloatsPerVertex;
        var at = source * FloatsPerVertex;
        vertices.Add(vertices[at]);
        vertices.Add(vertices[at + 1] - depth);
        vertices.Add(vertices[at + 2]);
        vertices.Add(vertices[at + 3]);
        vertices.Add(vertices[at + 4]);
        vertices.Add(vertices[at + 5]);
        return index;
    }
}
