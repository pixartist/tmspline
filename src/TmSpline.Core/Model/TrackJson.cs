using System.Text.Json;
using System.Text.Json.Serialization;
using TmSpline.Core.Geometry;

namespace TmSpline.Core.Model;

/// <summary>
/// The track file format shared by the web editor, the CLI and the server.
/// Coordinates are meters relative to the map center at ground level (map Y = 8), Y up.
/// Version 2 holds a map: name, laps and a list of tracks (each with closed, points, segments, objects). Version 1 files
/// (and single tracks, as the editor sends them for its preview) have one track's fields at the top level.
/// </summary>
public static class TrackJson
{
    public const int CurrentVersion = 2;

    public sealed class TrackDto
    {
        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = "Untitled";
        public bool Closed { get; set; }
        public int Laps { get; set; } = 3;
        public List<PointDto> Points { get; set; } = new();
        public List<SegmentDto> Segments { get; set; } = new();
        public List<ObjectDto> Objects { get; set; } = new();
        /// <summary>Version 2 maps: the tracks (each uses Closed, Points, Segments, Objects). Null for a single track.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TrackDto>? Tracks { get; set; }
        /// <summary>Vanilla blocks of the map (version 2 maps only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<BlockDto>? Blocks { get; set; }
    }

    public sealed class BlockDto
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int Dir { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Free { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double[]? Position { get; set; }
        public double Yaw { get; set; }
        public double Pitch { get; set; }
        public double Roll { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Pillar { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ItemColor Color { get; set; }
    }

    public sealed class ObjectDto
    {
        public TrackObjectKind Kind { get; set; }
        public int Segment { get; set; }
        public double At { get; set; }
        public double Offset { get; set; }
        public ObjectShape Shape { get; set; }
        public double Length { get; set; } = 4;
        public double Width { get; set; } = 4;
        public double Height { get; set; } = 4;
    }

    public sealed class PointDto
    {
        public double[] Position { get; set; } = new double[3];
        public double[] HandleIn { get; set; } = new double[3];
        public double[] HandleOut { get; set; } = new double[3];
        public double Bank { get; set; }
        /// <summary>Road width in meters. Missing in files written before widths moved from segments to points.</summary>
        public double? Width { get; set; }
        /// <summary>Cross-section bend in degrees (see TrackPoint.Curve).</summary>
        public double Curve { get; set; }
        /// <summary>Flat middle share of the width (see TrackPoint.Flat).</summary>
        public double Flat { get; set; }
        /// <summary>Sausageness 0..1 (see TrackPoint.Sausage).</summary>
        public double Sausage { get; set; }
        /// <summary>Border height in meters. Missing in files from before border heights were adjustable.</summary>
        public double? BorderHeight { get; set; }
        public Waypoint Waypoint { get; set; }
    }

    public sealed class SegmentDto
    {
        public Surface Surface { get; set; } = Surface.RoadTech;
        /// <summary>Legacy (segment widths): only read, to migrate old files to point widths.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Width { get; set; }
        public bool Borders { get; set; } = true;
        public BorderMaterial BorderMaterial { get; set; } = BorderMaterial.Rubber;
        public bool Underside { get; set; } = true;
        public ItemColor Color { get; set; }
        public Effect Effect { get; set; } = Effect.None;
        public double EffectYaw { get; set; }
        public double EffectPitch { get; set; }
    }

    public static TrackDto ParseDto(string json) =>
        JsonSerializer.Deserialize(json, TrackJsonContext.Default.TrackDto) ?? throw new JsonException("Empty track");

    /// <summary>One track (the first one of a map file).</summary>
    public static Track Parse(string json) => ToMap(ParseDto(json)).Tracks[0];

    public static TrackMap ParseMap(string json) => ToMap(ParseDto(json));

    /// <summary>The tracks of a map file, or the file itself as the only track (version 1, single tracks).</summary>
    public static IReadOnlyList<TrackDto> Parts(TrackDto dto) => dto.Tracks is { Count: > 0 } tracks ? tracks : new[] { dto };

    public static TrackMap ToMap(TrackDto dto)
    {
        if (dto.Version > CurrentVersion)
            throw new JsonException($"Track version {dto.Version} is newer than supported ({CurrentVersion})");
        var map = new TrackMap { Name = dto.Name, Laps = Math.Clamp(dto.Laps, 1, 99) };
        map.Tracks.AddRange(Parts(dto).Select(ToTrack));
        foreach (var b in dto.Blocks ?? new())
            if (!string.IsNullOrWhiteSpace(b.Name))
                map.Blocks.Add(new VanillaBlock
                {
                    Name = b.Name, X = b.X, Y = Math.Max(0, b.Y), Z = b.Z, Dir = ((b.Dir % 4) + 4) % 4,
                    Free = b.Free && b.Position is { Length: 3 }, Position = b.Position is { Length: 3 } p ? V(p) : default,
                    Yaw = b.Yaw, Pitch = b.Pitch, Roll = b.Roll, Pillar = b.Pillar && !b.Free, Color = b.Color,
                });
        return map;
    }

    public static string Serialize(Track track, string name = "Untitled") =>
        JsonSerializer.Serialize(FromTrack(track, name), TrackJsonContext.Default.TrackDto);

    public static Track ToTrack(TrackDto dto)
    {
        var track = new Track { Closed = dto.Closed };
        for (var i = 0; i < dto.Points.Count; i++)
        {
            var p = dto.Points[i];
            var width = p.Width ?? LegacyPointWidth(dto, i);
            track.Points.Add(new TrackPoint(V(p.Position), V(p.HandleOut), p.Bank, width) { HandleIn = V(p.HandleIn), Curve = p.Curve, Flat = Math.Clamp(p.Flat, 0, 0.9),
                Sausage = Math.Clamp(p.Sausage, 0, 1),
                BorderHeight = p.BorderHeight ?? TrackPoint.DefaultBorderHeight, Waypoint = p.Waypoint });
        }
        foreach (var s in dto.Segments)
            track.Segments.Add(new TrackSegment
            {
                Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
                Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
            });
        foreach (var o in dto.Objects ?? new())
            track.Objects.Add(new TrackObject
            {
                Kind = o.Kind, Segment = o.Segment, At = Math.Clamp(o.At, 0, 1), Offset = o.Offset, Shape = o.Shape,
                Length = Math.Max(0.1, o.Length), Width = Math.Max(0.1, o.Width), Height = Math.Max(0.1, o.Height),
            });
        track.SyncSegments();
        return track;
    }

    public static TrackDto FromTrack(Track track, string name) => new()
    {
        Name = name,
        Closed = track.Closed,
        Points = track.Points.Select(p => new PointDto
        {
            Position = A(p.Position), HandleIn = A(p.HandleIn), HandleOut = A(p.HandleOut), Bank = p.BankDegrees, Width = p.Width, Curve = p.Curve, Flat = p.Flat, Sausage = p.Sausage, BorderHeight = p.BorderHeight, Waypoint = p.Waypoint,
        }).ToList(),
        Segments = track.Segments.Select(s => new SegmentDto
        {
            Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
            Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
        }).ToList(),
        Objects = track.Objects.Select(o => new ObjectDto
        {
            Kind = o.Kind, Segment = o.Segment, At = o.At, Offset = o.Offset, Shape = o.Shape, Length = o.Length, Width = o.Width, Height = o.Height,
        }).ToList(),
    };

    /// <summary>Old files stored widths per segment: a point gets the average of its incoming and outgoing segment.</summary>
    private static double LegacyPointWidth(TrackDto dto, int point)
    {
        var count = dto.Segments.Count;
        var widths = new List<double>();
        var incoming = point > 0 ? point - 1 : dto.Closed ? count - 1 : -1;
        if (incoming >= 0 && incoming < count && dto.Segments[incoming].Width is { } a) widths.Add(a);
        if (point < count && dto.Segments[point].Width is { } b) widths.Add(b);
        return widths.Count > 0 ? widths.Average() : 16;
    }

    private static Vec3d V(double[] a) => a.Length == 3 ? new Vec3d(a[0], a[1], a[2]) : throw new JsonException("Expected [x, y, z]");
    private static double[] A(Vec3d v) => new[] { v.X, v.Y, v.Z };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TrackJson.TrackDto))]
internal partial class TrackJsonContext : JsonSerializerContext;
