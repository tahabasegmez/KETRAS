using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ketras.Mavlink.Messages;
using Ketras.Model.Fences;
using Ketras.Model.Geo;
using Ketras.Model.Missions;
using Ketras.Model.Planning;

namespace Ketras.Station.Missions;

/// <summary>A plan read from a file, with what could not be carried over.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Skipped">Items or shapes the station cannot represent, in operator-facing English.</param>
public sealed record PlanFileResult(Plan Plan, IReadOnlyList<string> Skipped);

/// <summary>
/// QGroundControl's <c>.plan</c> JSON (plan version 1, mission version 2, geofence version 2): simple items, the
/// fixed-wing landing pattern complex item, polygon and circle fences, and an empty rally list. Key names and the jump
/// numbering (DO_JUMP param1 = the target's <c>doJumpId</c>) follow QGC's MissionController, MissionItem,
/// LandingComplexItem and GeoFenceController. NaN parameters are written as null. The plan's altitude reference is QGC's
/// altitude mode (relative 1, AMSL 2, calculated above terrain 3): each item keeps its value in <c>Altitude</c> and its
/// wire value (relative, or AMSL) in <c>params[6]</c>. Settings QGC has no place for (the landing settings) go under a
/// <c>ketras</c> key, which QGC ignores.
/// </summary>
public static class PlanFileFormat
{
    private const string LandingPatternType = "fwLandingPattern";
    private const int PlanVersion = 1;
    private const int MissionVersion = 2;
    private const int FenceVersion = 2;
    private const int AltitudeModeMixed = 0;
    private const int AltitudeModeRelative = 1;
    private const int AltitudeModeAbsolute = 2;
    private const int AltitudeModeAboveTerrain = 3;
    private const int Px4Autopilot = 12;
    private const int FixedWing = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The plan as <c>.plan</c> JSON; <paramref name="groundAt"/> gives the AMSL values of an above-terrain plan.</summary>
    public static string Write(Plan plan, Func<GeoPoint, double?>? groundAt = null)
    {
        var mission = plan.Mission;
        var settings = mission.Settings;
        var mode = ModeOf(settings.AltitudeReference);
        var items = new JsonArray();
        var sequence = 1;
        var doJumpIds = new int[mission.Items.Count];
        for (var i = 0; i < mission.Items.Count; i++)
        {
            doJumpIds[i] = sequence;
            sequence += mission.Items[i].Type == MissionItemType.LandingPattern ? 3 : 1;
        }

        for (var i = 0; i < mission.Items.Count; i++)
        {
            var item = mission.Items[i];
            items.Add(item.Type == MissionItemType.LandingPattern
                ? LandingPattern(item, settings, groundAt)
                : SimpleItem(item, doJumpIds[i], item.Type == MissionItemType.DoJump && item.Param1 >= 0 && item.Param1 < doJumpIds.Length ? doJumpIds[(int)item.Param1] : null,
                    settings, groundAt));
        }

        if (settings.EndAction == MissionEndAction.ReturnToLaunch && !mission.Items.Any(item => item.Phase == MissionPhase.Landing))
        {
            items.Add(SimpleItem(new MissionItem { Type = MissionItemType.ReturnToLaunch }, sequence, null, settings, groundAt));
        }

        var file = new JsonObject
        {
            ["fileType"] = "Plan",
            ["groundStation"] = "Ketras",
            ["version"] = PlanVersion,
            ["mission"] = new JsonObject
            {
                ["version"] = MissionVersion,
                ["firmwareType"] = Px4Autopilot,
                ["vehicleType"] = FixedWing,
                ["cruiseSpeed"] = settings.CruiseSpeedMps,
                ["hoverSpeed"] = 5,
                ["globalPlanAltitudeMode"] = mode,
                ["plannedHomePosition"] = Coordinate(settings.PlannedHome, settings.HomeAltitudeMeters),
                ["items"] = items,
                ["ketras"] = new JsonObject
                {
                    ["landing"] = new JsonObject
                    {
                        ["terrainUse"] = (int)settings.Landing.TerrainUse,
                        ["abortIfTerrainNotFound"] = settings.Landing.AbortIfTerrainNotFound,
                        ["abortIfTerrainLost"] = settings.Landing.AbortIfTerrainLost
                    }
                }
            },
            ["geoFence"] = new JsonObject
            {
                ["version"] = FenceVersion,
                ["polygons"] = new JsonArray([.. plan.Fences.Where(f => f.Area is PolygonArea).Select(f => (JsonNode)new JsonObject
                {
                    ["version"] = 1,
                    ["inclusion"] = f.Inclusion,
                    ["polygon"] = new JsonArray([.. ((PolygonArea)f.Area).Vertices.Select(v => (JsonNode)Coordinate(v, null))])
                })]),
                ["circles"] = new JsonArray([.. plan.Fences.Where(f => f.Area is CircleArea).Select(f => (JsonNode)new JsonObject
                {
                    ["version"] = 1,
                    ["inclusion"] = f.Inclusion,
                    ["circle"] = new JsonObject { ["center"] = Coordinate(((CircleArea)f.Area).Center, null), ["radius"] = ((CircleArea)f.Area).RadiusMeters }
                })])
            },
            ["rallyPoints"] = new JsonObject { ["version"] = 2, ["points"] = new JsonArray() }
        };
        return file.ToJsonString(Indented);
    }

    /// <summary>Reads a <c>.plan</c>. Throws <see cref="FormatException"/> when the file is not a plan.</summary>
    public static PlanFileResult Read(string json)
    {
        JsonObject file;
        try
        {
            file = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("The file is not a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The file is not valid JSON: {exception.Message}", exception);
        }

        if ((string?)file["fileType"] != "Plan")
        {
            throw new FormatException("The file is not a QGroundControl plan (fileType is not \"Plan\").");
        }

        var skipped = new List<string>();
        var mission = ReadMission(file["mission"] as JsonObject, skipped);
        var fences = ReadFences(file["geoFence"] as JsonObject, skipped);
        if (file["rallyPoints"]?["points"] is JsonArray { Count: > 0 } rally)
        {
            skipped.Add($"{rally.Count} rally point(s): rally points are not supported");
        }

        return new PlanFileResult(new Plan { Mission = MissionEditor.Normalize(mission), Fences = [.. fences] }, skipped);
    }

    private static Mission ReadMission(JsonObject? json, List<string> skipped)
    {
        if (json is null)
        {
            return Mission.Empty;
        }

        var settings = new MissionSettings();
        if (json["plannedHomePosition"] is JsonArray home && home.Count >= 2)
        {
            settings = settings with
            {
                PlannedHome = new GeoPoint(Number(home[0]), Number(home[1])),
                HomeAltitudeMeters = home.Count > 2 ? Number(home[2]) : 0
            };
        }

        if (json["cruiseSpeed"] is JsonValue cruise && cruise.TryGetValue<double>(out var speed) && speed > 0)
        {
            settings = settings with { CruiseSpeedMps = speed };
        }

        // A mixed plan (mode 0, per-item modes) is read relative to home; AMSL items are converted with the home altitude.
        var planMode = json["globalPlanAltitudeMode"] is JsonValue modeValue && modeValue.TryGetValue<int>(out var m) ? m : AltitudeModeRelative;
        settings = settings with { AltitudeReference = ReferenceOf(planMode) };
        if (json["ketras"]?["landing"] is JsonObject landingJson)
        {
            settings = settings with
            {
                Landing = new LandingSettings
                {
                    TerrainUse = landingJson["terrainUse"] is JsonValue use && use.TryGetValue<int>(out var terrainUse) && Enum.IsDefined((LandingTerrainUse)terrainUse)
                        ? (LandingTerrainUse)terrainUse : LandingTerrainUse.Flare,
                    AbortIfTerrainNotFound = Bool(landingJson["abortIfTerrainNotFound"]),
                    AbortIfTerrainLost = Bool(landingJson["abortIfTerrainLost"])
                }
            };
        }

        var items = new List<MissionItem>();
        var byJumpId = new Dictionary<int, Guid>();
        var jumps = new List<(int Index, int TargetJumpId)>();
        foreach (var node in json["items"] as JsonArray ?? [])
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            switch ((string?)item["type"])
            {
                case "ComplexItem" when (string?)item["complexItemType"] == LandingPatternType:
                    items.Add(ReadLandingPattern(item, settings, skipped, items.Count + 1));
                    break;
                case "ComplexItem":
                    skipped.Add($"complex item '{(string?)item["complexItemType"]}': not supported");
                    break;
                case "SimpleItem":
                    var command = (MavCmd)(int)Number(item["command"]);
                    var parameters = item["params"] as JsonArray ?? [];
                    double P(int i) => i < parameters.Count ? Number(parameters[i]) : 0;
                    if (command == MavCmd.DoLandStart)
                    {
                        break;
                    }

                    if (!MissionEncoder.TryResolve(command, out var type))
                    {
                        skipped.Add($"MAV_CMD {(int)command}: not a command the station plans");
                        break;
                    }

                    var created = new MissionItem
                    {
                        Type = type,
                        AutoContinue = item["autoContinue"] is not JsonValue auto || !auto.TryGetValue<bool>(out var autoContinue) || autoContinue,
                        Param1 = P(0),
                        Param2 = P(1),
                        Param3 = P(2),
                        Param4 = P(3),
                        Location = MissionItemCatalog.UsesLocation(type) ? new GeoPoint(P(4), P(5)) : null,
                        AltitudeMeters = MissionItemCatalog.UsesLocation(type) ? ReadAltitude(item, P(6), settings, skipped, items.Count + 1) : 0
                    };
                    if (item["doJumpId"] is JsonValue id && id.TryGetValue<int>(out var jumpId))
                    {
                        byJumpId[jumpId] = created.Id;
                    }

                    if (type == MissionItemType.DoJump)
                    {
                        jumps.Add((items.Count, (int)P(0)));
                    }

                    items.Add(created);
                    break;
            }
        }

        foreach (var (index, target) in jumps)
        {
            items[index] = items[index] with { JumpTargetId = byJumpId.TryGetValue(target, out var id) ? id : null, Param1 = -1 };
        }

        // The file has no end action: a lone closing RTL is the end action Write appends, and a mission without any
        // landing item ends where it is, so neither gains an RTL on upload.
        var landing = items.Where(item => item.Phase == MissionPhase.Landing).ToList();
        if (landing is [{ Type: MissionItemType.ReturnToLaunch } rtl] && ReferenceEquals(items[^1], rtl) && items.All(i => i.JumpTargetId != rtl.Id))
        {
            items.RemoveAt(items.Count - 1);
            settings = settings with { EndAction = MissionEndAction.ReturnToLaunch };
        }
        else if (landing.Count == 0)
        {
            settings = settings with { EndAction = MissionEndAction.Hold };
        }

        return new Mission { Settings = settings, Items = [.. items] };
    }

    private static IEnumerable<Fence> ReadFences(JsonObject? json, List<string> skipped)
    {
        if (json is null)
        {
            yield break;
        }

        var number = 1;
        foreach (var polygon in (json["polygons"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var vertices = (polygon["polygon"] as JsonArray ?? []).OfType<JsonArray>().Select(v => new GeoPoint(Number(v[0]), Number(v[1]))).ToImmutableArray();
            if (vertices.Length < 3)
            {
                skipped.Add("a polygon fence with fewer than three corners");
                continue;
            }

            yield return new Fence($"fence-{number++}", new PolygonArea(vertices), Bool(polygon["inclusion"]));
        }

        foreach (var circle in (json["circles"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (circle["circle"]?["center"] is JsonArray center)
            {
                yield return new Fence($"fence-{number++}", new CircleArea(new GeoPoint(Number(center[0]), Number(center[1])), Number(circle["circle"]?["radius"])), Bool(circle["inclusion"]));
            }
        }

        if (json["breachReturn"] is not null)
        {
            skipped.Add("the geofence breach return point: not supported");
        }
    }

    private static int ModeOf(AltitudeReference reference) => reference switch
    {
        AltitudeReference.AboveSeaLevel => AltitudeModeAbsolute,
        AltitudeReference.AboveTerrain => AltitudeModeAboveTerrain,
        _ => AltitudeModeRelative
    };

    private static AltitudeReference ReferenceOf(int mode) => mode switch
    {
        AltitudeModeAbsolute => AltitudeReference.AboveSeaLevel,
        AltitudeModeAboveTerrain => AltitudeReference.AboveTerrain,
        _ => AltitudeReference.RelativeToHome
    };

    /// <summary>
    /// An item's altitude in the plan's reference: its <c>Altitude</c> when its mode is the plan's, else converted from
    /// its wire value (<c>params[6]</c> in its frame) when that can be done without terrain data.
    /// </summary>
    private static double ReadAltitude(JsonObject item, double wire, MissionSettings settings, List<string> skipped, int number)
    {
        var itemMode = item["AltitudeMode"] is JsonValue value && value.TryGetValue<int>(out var mode) ? mode : AltitudeModeMixed;
        var altitude = item["Altitude"] is JsonValue a && a.TryGetValue<double>(out var given) ? given : (double?)null;
        if (itemMode != AltitudeModeMixed && ReferenceOf(itemMode) == settings.AltitudeReference && altitude is { } same)
        {
            return same;
        }

        var amsl = (int)Number(item["frame"]) == (int)MavFrame.Global ? wire : wire + settings.HomeAltitudeMeters;
        switch (settings.AltitudeReference)
        {
            case AltitudeReference.AboveSeaLevel:
                return amsl;
            case AltitudeReference.RelativeToHome:
                return amsl - settings.HomeAltitudeMeters;
            default:
                var kept = altitude ?? wire;
                skipped.Add(string.Create(CultureInfo.InvariantCulture, $"item {number}: its altitude is not above terrain; kept as {kept:F0} m above terrain"));
                return kept;
        }
    }

    /// <summary>The value written to <c>params[6]</c> (relative or AMSL, as the frame says) and the frame.</summary>
    private static (double Wire, MavFrame Frame) WireAltitude(double altitude, GeoPoint? position, MavFrame frame, MissionSettings settings, Func<GeoPoint, double?>? groundAt)
    {
        if (frame != MavFrame.GlobalRelativeAlt || settings.AltitudeReference == AltitudeReference.RelativeToHome)
        {
            return (altitude, frame);
        }

        return (MissionAltitudes.AmslOf(settings, altitude, position, groundAt) ?? altitude, MavFrame.Global);
    }

    private static JsonObject SimpleItem(MissionItem item, int doJumpId, int? jumpTarget, MissionSettings settings, Func<GeoPoint, double?>? groundAt)
    {
        var (command, frame) = MissionEncoder.WireOf(item.Type);
        var location = item.Location;
        var (wire, wireFrame) = item.UsesLocation ? WireAltitude(item.AltitudeMeters, location, frame, settings, groundAt) : (0, frame);
        frame = wireFrame;
        var json = new JsonObject
        {
            ["type"] = "SimpleItem",
            ["command"] = (int)command,
            ["frame"] = (int)frame,
            ["autoContinue"] = item.AutoContinue,
            ["doJumpId"] = doJumpId,
            ["params"] = new JsonArray(
                Value(jumpTarget ?? item.Param1), Value(item.Param2), Value(item.Param3), Value(item.Param4),
                Value(location?.Latitude ?? 0), Value(location?.Longitude ?? 0), Value(wire))
        };
        if (item.UsesLocation)
        {
            json["AltitudeMode"] = ModeOf(settings.AltitudeReference);
            json["Altitude"] = item.AltitudeMeters;
            json["AMSLAltAboveTerrain"] = settings.AltitudeReference == AltitudeReference.AboveTerrain ? Value(wire) : null;
        }

        return json;
    }

    /// <summary>
    /// QGC's fixed-wing landing: loiter centre ("landingApproachCoordinate") and touchdown ("landCoordinate"). QGC's
    /// pattern is relative or AMSL only, so an above-terrain plan writes AMSL and keeps its own values under "ketras".
    /// </summary>
    private static JsonObject LandingPattern(MissionItem item, MissionSettings settings, Func<GeoPoint, double?>? groundAt)
    {
        var relative = settings.AltitudeReference == AltitudeReference.RelativeToHome;
        var center = MissionItemValues.LandingPatternOrbitCenter(item) ?? default;
        var approach = relative ? item.AltitudeMeters : MissionAltitudes.AmslOf(settings, item, groundAt) ?? item.AltitudeMeters;
        var touchdown = relative ? item.Param1 : MissionAltitudes.TouchdownAmslOf(settings, item, groundAt) ?? item.Param1;
        var json = LandingPatternJson(item, center, approach, touchdown, relative);
        if (settings.AltitudeReference == AltitudeReference.AboveTerrain)
        {
            json["ketras"] = new JsonObject { ["approachAltitude"] = item.AltitudeMeters, ["touchdownAltitude"] = item.Param1 };
        }

        return json;
    }

    private static JsonObject LandingPatternJson(MissionItem item, GeoPoint center, double approach, double touchdown, bool relative) => new()
    {
        ["type"] = "ComplexItem",
        ["complexItemType"] = LandingPatternType,
        ["version"] = 2,
        ["landingApproachCoordinate"] = Coordinate(center, approach),
        ["landCoordinate"] = Coordinate(item.Location ?? default, touchdown),
        ["loiterRadius"] = Math.Abs(item.Param2),
        ["loiterClockwise"] = MissionItemValues.IsClockwise(item),
        ["useLoiterToAlt"] = true,
        ["altitudesAreRelative"] = relative,
        ["valueSetIsDistance"] = true,
        ["useDoChangeSpeed"] = false,
        ["finalApproachSpeed"] = 0,
        ["stopTakingPhotos"] = true,
        ["stopVideoPhotos"] = true
    };

    /// <summary>
    /// Rebuilds the touchdown, approach heading and length from the loiter centre: the approach is the tangent from the
    /// circle to the touchdown, on the right of the approach for a clockwise orbit.
    /// </summary>
    private static MissionItem ReadLandingPattern(JsonObject json, MissionSettings settings, List<string> skipped, int number)
    {
        var approach = json["landingApproachCoordinate"] as JsonArray ?? json["loiterCoordinate"] as JsonArray ?? [];
        var land = json["landCoordinate"] as JsonArray ?? [];
        var center = new GeoPoint(Number(approach[0]), Number(approach[1]));
        var touchdown = new GeoPoint(Number(land[0]), Number(land[1]));
        var radius = Math.Abs(Number(json["loiterRadius"]));
        var clockwise = Bool(json["loiterClockwise"]);
        var distance = GeoMath.DistanceMeters(touchdown, center);
        var length = Math.Sqrt(Math.Max((distance * distance) - (radius * radius), 0));
        var tangentAngle = distance > 0 ? Math.Asin(Math.Min(radius / distance, 1)) * 180 / Math.PI : 0;
        var backBearing = GeoMath.BearingDegrees(touchdown, center) + (clockwise ? tangentAngle : -tangentAngle);
        return new MissionItem
        {
            Type = MissionItemType.LandingPattern,
            Location = touchdown,
            AltitudeMeters = LandingAltitude(json, settings, approach.Count > 2 ? Number(approach[2]) : MissionItemFactory.DefaultLandingApproachAltitudeMeters,
                "approachAltitude", skipped, number),
            Param1 = LandingAltitude(json, settings, land.Count > 2 ? Number(land[2]) : 0, "touchdownAltitude", skipped, number),
            Param2 = clockwise ? radius : -radius,
            Param3 = Math.Round(length, 1),
            Param4 = Math.Round(((backBearing + 180) % 360 + 360) % 360, 1)
        };
    }

    /// <summary>A landing pattern altitude in the plan's reference, from its relative or AMSL value.</summary>
    private static double LandingAltitude(JsonObject json, MissionSettings settings, double value, string ketrasKey, List<string> skipped, int number)
    {
        if (settings.AltitudeReference == AltitudeReference.AboveTerrain)
        {
            if (json["ketras"]?[ketrasKey] is JsonValue own && own.TryGetValue<double>(out var kept))
            {
                return kept;
            }

            skipped.Add(string.Create(CultureInfo.InvariantCulture, $"item {number}: the landing pattern's altitudes are not above terrain; kept as {value:F0} m above terrain"));
            return value;
        }

        var amsl = json["altitudesAreRelative"] is JsonValue relative && relative.TryGetValue<bool>(out var isRelative) && !isRelative
            ? value
            : value + settings.HomeAltitudeMeters;
        return settings.AltitudeReference == AltitudeReference.AboveSeaLevel ? amsl : amsl - settings.HomeAltitudeMeters;
    }

    private static JsonArray Coordinate(GeoPoint point, double? altitude) =>
        altitude is { } height ? new JsonArray(point.Latitude, point.Longitude, height) : new JsonArray(point.Latitude, point.Longitude);

    private static JsonNode? Value(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;

    private static double Number(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => double.NaN
    };

    private static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
