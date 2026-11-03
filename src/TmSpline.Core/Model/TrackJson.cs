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
    /// <summary>3: point widths no longer include the curbs' inset (see TrackPoint.LegacyCurbInset).</summary>
    public const int CurrentVersion = 3;

    public sealed class TrackDto
    {
        public int Version { get; set; } = CurrentVersion;
        public string Name { get; set; } = "Untitled";
        public bool Closed { get; set; }
        /// <summary>Old per-track "keep edges" (see ShapeAnchor); read only.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ShapeAnchor? ShapeAnchor { get; set; }
        public int Laps { get; set; } = 3;
        /// <summary>Map without the stadium (see TrackMap.NoStadium); missing = with.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? NoStadium { get; set; }
        public List<PointDto> Points { get; set; } = new();
        public List<SegmentDto> Segments { get; set; } = new();
        public List<ObjectDto> Objects { get; set; } = new();
        /// <summary>Version 2 maps: the tracks (each uses Closed, Points, Segments, Objects). Null for a single track.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TrackDto>? Tracks { get; set; }
        /// <summary>Vanilla blocks of the map (version 2 maps only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<BlockDto>? Blocks { get; set; }
        /// <summary>Vanilla items of the map (trees, ...; version 2 maps only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ItemDto>? Items { get; set; }
    }

    public sealed class ItemDto
    {
        public string Name { get; set; } = "";
        public double[]? Position { get; set; }
        public double Yaw { get; set; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Variant { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ExtraFlags { get; set; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ItemColor Color { get; set; }
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
        /// <summary>Keep edges 0..1 (see TrackPoint.EdgeAnchor); missing = 0.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? EdgeAnchor { get; set; }
        /// <summary>Border height in meters. Missing in files from before border heights were adjustable.</summary>
        public double? BorderHeight { get; set; }
        /// <summary>Borders at this point (see TrackPoint.Borders). Missing in files from before borders moved to points.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Borders { get; set; }
        public Waypoint Waypoint { get; set; }
        /// <summary>Gate height in meters (see TrackPoint.GateHeight); missing = the default.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? GateHeight { get; set; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BumpPattern Bumps { get; set; }
        public double BumpHeight { get; set; } = 0.5;
        public double BumpSize { get; set; } = 8;
        public double BumpDetail { get; set; } = 2;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public RoadSignKind Signs { get; set; }
        public SignSide SignSide { get; set; } = SignSide.Both;
        public double SignSpacing { get; set; } = 16;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ItemColor SignColor { get; set; }
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
        var map = new TrackMap { Name = dto.Name, Laps = Math.Clamp(dto.Laps, 1, 99), NoStadium = dto.NoStadium == true };
        map.Tracks.AddRange(Parts(dto).Select(ToTrack));
        if (dto.Version < 3)
            foreach (var track in map.Tracks)
                RemoveCurbInset(track);
        foreach (var b in dto.Blocks ?? new())
            if (!string.IsNullOrWhiteSpace(b.Name))
                map.Blocks.Add(new VanillaBlock
                {
                    Name = b.Name, X = b.X, Y = Math.Max(0, b.Y), Z = b.Z, Dir = ((b.Dir % 4) + 4) % 4,
                    Free = b.Free && b.Position is { Length: 3 }, Position = b.Position is { Length: 3 } p ? V(p) : default,
                    Yaw = b.Yaw, Pitch = b.Pitch, Roll = b.Roll, Pillar = b.Pillar && !b.Free, Color = b.Color, Variant = Math.Clamp(b.Variant, 0, 15), ExtraFlags = b.ExtraFlags & VanillaBlock.AllowedExtraFlags,
                });
        foreach (var i in dto.Items ?? new())
            if (!string.IsNullOrWhiteSpace(i.Name) && i.Position is { Length: 3 } p)
                map.Items.Add(new VanillaItem { Name = i.Name, Position = V(p), Yaw = i.Yaw });
        return map;
    }

    public static string Serialize(Track track, string name = "Untitled") =>
        JsonSerializer.Serialize(FromTrack(track, name), TrackJsonContext.Default.TrackDto);

    /// <summary>
    /// Files before version 3: the width of points with borders included the curbs' inset; without it the curbs and the
    /// driving surface stay where they were. Points without borders had none.
    /// </summary>
    private static void RemoveCurbInset(Track track)
    {
        track.SyncSegments();
        var n = track.Points.Count;
        for (var i = 0; i < n; i++)
        {
            var p = track.Points[i];
            // Borders on the segments (older files): the point had curbs when a segment it joins had them.
            var curbs = track.PointBorders ? p.Borders == true
                : (i < track.SegmentCount && track.Segments[i].Borders) || ((i > 0 || track.Closed) && track.Segments[(i - 1 + n) % n].Borders);
            if (curbs) p.Width = Math.Max(1, p.Width - 2 * TrackPoint.LegacyCurbInset);
        }
    }

    public static Track ToTrack(TrackDto dto)
    {
        var track = new Track { Closed = dto.Closed };
        var legacyAnchor = dto.ShapeAnchor == ShapeAnchor.Edges ? 1.0 : 0.0;
        for (var i = 0; i < dto.Points.Count; i++)
        {
            var p = dto.Points[i];
            var width = p.Width ?? LegacyPointWidth(dto, i);
            track.Points.Add(new TrackPoint(V(p.Position), V(p.HandleOut), p.Bank, width) { HandleIn = V(p.HandleIn), Curve = p.Curve, Flat = Math.Clamp(p.Flat, 0, 0.9),
                Sausage = Math.Clamp(p.Sausage, 0, 1), EdgeAnchor = Math.Clamp(p.EdgeAnchor ?? legacyAnchor, 0, 1),
                BorderHeight = p.BorderHeight ?? TrackPoint.LegacyBorderHeight, Borders = p.Borders, Waypoint = p.Waypoint,
                GateHeight = Math.Clamp(p.GateHeight ?? TrackPoint.DefaultGateHeight, 1, 100) });
        }
        foreach (var s in dto.Segments)
            track.Segments.Add(new TrackSegment
            {
                Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
                Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
                Bumps = s.Bumps, BumpHeight = Math.Clamp(s.BumpHeight, -10, 10), BumpSize = Math.Clamp(s.BumpSize, 0.5, 256),
                BumpDetail = Math.Clamp(s.BumpDetail, 0.25, 16),
                Signs = s.Signs, SignSide = s.SignSide, SignSpacing = Math.Clamp(s.SignSpacing, 2, 256), SignColor = s.SignColor,
            });
        foreach (var o in dto.Objects ?? new())
            track.Objects.Add(new TrackObject
            {
                Kind = o.Kind, Segment = o.Segment, At = Math.Clamp(o.At, 0, 1), Offset = o.Offset, Shape = o.Shape,
                Length = Math.Max(0.1, o.Length), Width = Math.Max(0.1, o.Width),
                // Bumps may be dips (negative height).
                Height = o.Kind == TrackObjectKind.Bump ? Math.Clamp(o.Height, -10, 10) : Math.Max(0.1, o.Height), Color = o.Color,
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
            Position = A(p.Position), HandleIn = A(p.HandleIn), HandleOut = A(p.HandleOut), Bank = p.BankDegrees, Width = p.Width, Curve = p.Curve, Flat = p.Flat, Sausage = p.Sausage, EdgeAnchor = p.EdgeAnchor > 0 ? p.EdgeAnchor : null, BorderHeight = p.BorderHeight, Borders = p.Borders, Waypoint = p.Waypoint,
            GateHeight = p.GateHeight == TrackPoint.DefaultGateHeight ? null : p.GateHeight,
        }).ToList(),
        Segments = track.Segments.Select(s => new SegmentDto
        {
            Surface = s.Surface, Borders = s.Borders, BorderMaterial = s.BorderMaterial, Underside = s.Underside, Color = s.Color,
            Effect = s.Effect, EffectYaw = s.EffectYaw, EffectPitch = s.EffectPitch,
            Bumps = s.Bumps, BumpHeight = s.BumpHeight, BumpSize = s.BumpSize, BumpDetail = s.BumpDetail,
            Signs = s.Signs, SignSide = s.SignSide, SignSpacing = s.SignSpacing, SignColor = s.SignColor,
        }).ToList(),
        Objects = track.Objects.Select(o => new ObjectDto
        {
            Kind = o.Kind, Segment = o.Segment, At = o.At, Offset = o.Offset, Shape = o.Shape, Length = o.Length, Width = o.Width, Height = o.Height, Color = o.Color,
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
