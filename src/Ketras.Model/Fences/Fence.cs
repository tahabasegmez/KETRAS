using Ketras.Model.Geo;

namespace Ketras.Model.Fences;

/// <summary>
/// One geofence zone the operator drew. <see cref="Inclusion"/> tells the vehicle which side to stay on: true =
/// remain inside, false = keep out. Fences carry no altitude; a vertical limit is a vehicle setting.
/// </summary>
/// <param name="Id">Unique within the plan, e.g. "fence-1".</param>
/// <param name="Area">The zone on the ground.</param>
/// <param name="Inclusion">Stay inside (true) or keep out (false).</param>
public sealed record Fence(string Id, Area Area, bool Inclusion = false)
{
    /// <summary>An id that none of <paramref name="fences"/> uses, e.g. "fence-3".</summary>
    public static string NextId(IEnumerable<Fence> fences, string prefix = "fence")
    {
        var used = fences.Select(fence => fence.Id).ToHashSet(StringComparer.Ordinal);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"{prefix}-{suffix}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
