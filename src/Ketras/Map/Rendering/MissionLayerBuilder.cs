using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using NetTopologySuite.Geometries;

namespace Ketras.Map.Rendering;

/// <summary>Builder: turns a mission into a phase-coloured route, numbered item badges, and chips for legs without a location (speed changes, camera actions, delays).</summary>
internal static class MissionLayerBuilder
{
    private const double ChevronPixels = 9;
    private const double TurnArrowPixels = 7;

    internal readonly record struct MissionFeatures(
        IReadOnlyList<IFeature> Route,
        IReadOnlyList<IFeature> Items);

    /// <summary>One item that has a place on the map, with the sequence number shown on it.</summary>
    private readonly record struct Anchor(int Index, MissionItem Item, MPoint World);

    public static MissionFeatures Build(Mission mission, Guid? selectedId, int? executingIndex, double resolution,
        IReadOnlyList<GeoPoint> terrainConflicts)
    {
        var route = new List<IFeature>();
        var items = new List<IFeature>();

        var anchors = CollectAnchors(mission);

        AddHome(items, mission);
        AddTerrainConflicts(items, terrainConflicts);
        AddRoute(route, anchors, resolution);
        AddActionChips(route, mission, anchors, selectedId);

        foreach (var anchor in anchors)
        {
            AddItemDecoration(items, anchor, selectedId, resolution);
            AddBadge(items, anchor, selectedId, executingIndex);
        }

        return new MissionFeatures(route, items);
    }

    private static List<Anchor> CollectAnchors(Mission mission)
    {
        var anchors = new List<Anchor>();

        for (var i = 0; i < mission.Items.Count; i++)
        {
            var item = mission.Items[i];
            if (item.Location is { } location)
            {
                anchors.Add(new Anchor(i, item, MapGeometry.ToMercator(location)));
            }
        }

        return anchors;
    }

    private static void AddHome(List<IFeature> items, Mission mission)
    {
        var home = mission.Settings.PlannedHome;
        if (home.IsUnset)
        {
            return;
        }

        var feature = new PointFeature(MapGeometry.ToMercator(home));
        feature.Styles.Add(MapStyles.HomeBadge());
        feature.Styles.Add(MapStyles.BadgeText("H"));
        items.Add(feature);
    }

    private static void AddTerrainConflicts(List<IFeature> items, IReadOnlyList<GeoPoint> conflicts)
    {
        foreach (var location in conflicts)
        {
            var feature = new PointFeature(MapGeometry.ToMercator(location));
            feature.Styles.Add(MapStyles.TerrainConflict());
            feature.Styles.Add(MapStyles.MapLabel("!", MapPalette.LabelBackgroundStrong));
            items.Add(feature);
        }
    }

    /// <summary>
    /// Legs take the colour of the item they lead into, so the route reads as a sequence of
    /// phases rather than one undifferentiated line.
    /// </summary>
    private static void AddRoute(List<IFeature> route, List<Anchor> anchors, double resolution)
    {
        for (var i = 1; i < anchors.Count; i++)
        {
            var from = anchors[i - 1].World;
            var to = anchors[i].World;
            var phase = anchors[i].Item.Phase;

            var leg = new GeometryFeature
            {
                Geometry = new LineString([new Coordinate(from.X, from.Y), new Coordinate(to.X, to.Y)])
            };

            leg.Styles.Add(MapStyles.RouteGlow(phase));
            leg.Styles.Add(MapStyles.RouteLeg(phase));
            route.Add(leg);

            AddDirectionArrow(route, from, to, phase, resolution);
        }
    }

    private static void AddDirectionArrow(List<IFeature> route, MPoint from, MPoint to, MissionPhase phase, double resolution)
    {
        var midpoint = new MPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        var heading = MapGeometry.HeadingBetween(from, to);
        var size = MapGeometry.PixelsToMapUnits(resolution, ChevronPixels);

        var arrow = new GeometryFeature
        {
            Geometry = MapSymbols.DirectionChevron(midpoint, heading, size)
        };

        arrow.Styles.Add(MapStyles.LegChevron());
        route.Add(arrow);
    }

    /// <summary>
    /// Items with no location of their own are drawn on the leg where they take effect, spread
    /// evenly when several share one leg; putting them on the
    /// map is what shows the operator where the manoeuvre actually happens.
    /// </summary>
    private static void AddActionChips(List<IFeature> route, Mission mission, List<Anchor> anchors, Guid? selectedId)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        for (var legEnd = 0; legEnd < anchors.Count; legEnd++)
        {
            var previousIndex = legEnd == 0 ? -1 : anchors[legEnd - 1].Index;
            var group = CollectBetween(mission, previousIndex, anchors[legEnd].Index);

            if (group.Count == 0)
            {
                continue;
            }

            var from = legEnd == 0 ? anchors[0].World : anchors[legEnd - 1].World;
            var to = anchors[legEnd].World;

            for (var i = 0; i < group.Count; i++)
            {
                var t = (i + 1d) / (group.Count + 1d);
                var point = new MPoint(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

                var chip = new PointFeature(point);
                chip.Styles.Add(MapStyles.ActionChip(group[i].Id == selectedId));
                route.Add(chip);
            }
        }

        // Anything after the final located item still belongs to the plan; hang it off the end.
        var trailing = CollectBetween(mission, anchors[^1].Index, mission.Items.Count);
        for (var i = 0; i < trailing.Count; i++)
        {
            var chip = new PointFeature(anchors[^1].World);
            chip.Styles.Add(MapStyles.ActionChip(trailing[i].Id == selectedId));
            route.Add(chip);
        }
    }

    private static List<MissionItem> CollectBetween(Mission mission, int exclusiveStart, int exclusiveEnd)
    {
        var group = new List<MissionItem>();

        for (var i = exclusiveStart + 1; i < exclusiveEnd && i < mission.Items.Count; i++)
        {
            if (!mission.Items[i].UsesLocation)
            {
                group.Add(mission.Items[i]);
            }
        }

        return group;
    }

    /// <summary>Orbit rings and approach funnels: the geometry that gives an item its shape.</summary>
    private static void AddItemDecoration(List<IFeature> items, Anchor anchor, Guid? selectedId, double resolution)
    {
        var item = anchor.Item;
        var isSelected = item.Id == selectedId;
        var latitude = item.Location?.Latitude ?? 0;

        if (item.Type == MissionItemType.LandingPattern)
        {
            var clockwise = MissionItemValues.IsClockwise(item);
            var radius = MapGeometry.MetersToMapUnits(Math.Max(Math.Abs(item.Param2), 1), latitude);
            var approach = MapGeometry.MetersToMapUnits(Math.Max(item.Param3, 1), latitude);
            var pattern = LandingPatternGeometry.Build(anchor.World, item.Param4, approach, radius, clockwise);
            var arrowSize = MapGeometry.PixelsToMapUnits(resolution, TurnArrowPixels);

            items.Add(Geometry(pattern.Funnel, MapStyles.ApproachFunnel()));
            items.Add(Geometry(pattern.Centerline, MapStyles.DirectionArrow(MissionPhase.Landing)));
            items.Add(Geometry(LoiterGeometry.Ring(pattern.OrbitCenter, radius), MapStyles.LoiterRing(item.Type, isSelected)));
            items.Add(Geometry(LoiterGeometry.TurnArrow(pattern.OrbitCenter, radius, clockwise, arrowSize), MapStyles.DirectionArrow(MissionPhase.Landing)));
            return;
        }

        if (MissionItemValues.LoiterRadiusMeters(item) is { } radiusMeters && radiusMeters > 0)
        {
            var radius = MapGeometry.MetersToMapUnits(radiusMeters, latitude);
            var arrowSize = MapGeometry.PixelsToMapUnits(resolution, TurnArrowPixels);

            items.Add(Geometry(LoiterGeometry.Ring(anchor.World, radius), MapStyles.LoiterRing(item.Type, isSelected)));
            items.Add(Geometry(LoiterGeometry.TurnArrow(anchor.World, radius, clockwise: true, arrowSize), MapStyles.DirectionArrow(item.Phase)));
        }
    }

    private static void AddBadge(List<IFeature> items, Anchor anchor, Guid? selectedId, int? executingIndex)
    {
        var isSelected = anchor.Item.Id == selectedId;
        var isExecuting = anchor.Index == executingIndex;
        var feature = new PointFeature(anchor.World);

        if (isExecuting)
        {
            feature.Styles.Add(MapStyles.ExecutionHalo());
        }

        if (isSelected)
        {
            feature.Styles.Add(MapStyles.SelectionHalo());
        }

        feature.Styles.Add(MapStyles.WaypointBadge(anchor.Item.Type));
        feature.Styles.Add(MapStyles.WaypointNumber(anchor.Index + 1));
        items.Add(feature);
    }

    private static GeometryFeature Geometry(Geometry geometry, Mapsui.Styles.IStyle style)
    {
        var feature = new GeometryFeature { Geometry = geometry };
        feature.Styles.Add(style);
        return feature;
    }
}
