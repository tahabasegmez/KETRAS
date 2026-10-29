using System.Numerics;

namespace Ketras.Scene3D.Rendering;

/// <summary>
/// The aircraft as the map draws it: a flat arrow, here lying in the aircraft's
/// body plane so heading, pitch and roll all show. Local frame: +X right wing, +Z nose, unit length.
/// </summary>
internal static class AircraftSymbol
{
    public static readonly Vector3[] Outline =
    [
        new(0f, 0f, 0.5f),
        new(0.364f, 0f, -0.409f),
        new(0f, 0f, -0.182f),
        new(-0.364f, 0f, -0.409f)
    ];
}
