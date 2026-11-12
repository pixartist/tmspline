using System.Text;
using TmSpline.Core.Model;

namespace TmSpline.Core.Geometry;

/// <summary>
/// Builds the exact road mesh that the export uses, as one chunk, packed into a little-endian buffer for the editor:
/// <code>
/// int32 vertexCount, int32 triangleCount, int32 materialCount, float32 trackLength
/// materialCount × (int32 byteLength, utf8 name)        — padded to 4 bytes
/// float32[vertexCount*3] positions, float32[vertexCount*3] normals, float32[vertexCount*2] uv0 (the export's base UVs)
/// float32[vertexCount*2] (distance, across) on the driving surface, NaN elsewhere (for placing objects)
/// int32[triangleCount*3] indices, int32[triangleCount] material, int32[triangleCount] segment
/// int32 pointCount, pointCount × float32[6] (tangent xyz, up xyz) — the exact road frame at each spline point
/// int32 effectCount, effectCount × (float32[9] center xyz + direction xyz + road direction xyz, int32 effect) — one per boost piece
/// int32 segmentCount, float32[segmentCount+1] distance where each segment starts (the last entry is the track length)
/// int32 objectCount, objectCount × (float32[11] position xyz + normal xyz + tangent xyz + distance + across, int32 placed)
/// </code>
/// </summary>
public static class PreviewMesh
{
    /// <param name="quality">0 = coarse (fast, for interactive edits), 1 = the export mesh at Low quality (finer qualities only add strips and columns).</param>
    public static byte[] Build(string trackJson, int quality = 1)
    {
        var track = TrackJson.Parse(trackJson);
        var coarse = quality <= 0;
        var sampler = coarse ? new SamplerSettings { MaxStep = 8, MaxAngleDegrees = 8 } : MeshQualities.Sampler(MeshQuality.Low);
        var rings = TrackSampler.Sample(track, sampler);
        var settings = new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity, WorldSpace = true, MaxColumnAngleDegrees = MeshQualities.ColumnAngleDegrees(MeshQuality.Low) };
        if (coarse)
        {
            settings.ColumnWidth = 8;
            settings.MaxColumnAngleDegrees = 22.5;
        }
        var chunks = rings.Count >= 2
            ? RoadMeshBuilder.Build(track, rings, Vec3d.Zero, "Preview", settings)
            : new List<MeshChunk>();
        // Race gates as they will be exported (their triggers are invisible and not part of the preview).
        var segmentStarts = TrackSampler.SegmentStarts(track);
        if (rings.Count >= 2)
        {
            chunks.AddRange(WaypointBuilder.Build(track, rings, Vec3d.Zero, settings));
            // Effect and car gates of our own (the game's gate blocks are shown by the editor).
            chunks.AddRange(GateObjects.Custom(track, rings, segmentStarts, Vec3d.Zero, settings, "EffectGate"));
            if (PillarBuilder.Build(track, rings, segmentStarts, Vec3d.Zero, settings) is { } pillars)
                chunks.Add(pillars);
            // Stand-ins for the game's curve signs the export places.
            if (SignBuilder.Preview(track, rings, segmentStarts, settings) is { } signs)
                chunks.Add(signs);
        }
        var mesh = Merge(chunks);

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(mesh.Positions.Count);
        w.Write(mesh.Triangles.Count);
        w.Write(mesh.Materials.Count);
        w.Write((float)(rings.Count > 0 ? rings[^1].Distance : 0));
        foreach (var m in mesh.Materials)
        {
            var bytes = Encoding.UTF8.GetBytes(m.Name);
            w.Write(bytes.Length);
            w.Write(bytes);
            w.Write(new byte[(4 - bytes.Length % 4) % 4]);
        }
        foreach (var p in mesh.Positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
        foreach (var n in mesh.Normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
        foreach (var uv in mesh.Uv0) { w.Write(uv.X); w.Write(uv.Y); }
        foreach (var sc in mesh.Surface) { w.Write(sc.X); w.Write(sc.Y); }
        foreach (var t in mesh.Triangles) { w.Write(t.A); w.Write(t.B); w.Write(t.C); }
        foreach (var t in mesh.Triangles) w.Write(t.Material);
        foreach (var t in mesh.Triangles) w.Write(t.Segment);

        var frames = PointFrames(track, rings);
        w.Write(frames.Length);
        foreach (var r in frames)
        {
            w.Write((float)r.Tangent.X); w.Write((float)r.Tangent.Y); w.Write((float)r.Tangent.Z);
            w.Write((float)r.Up.X); w.Write((float)r.Up.Y); w.Write((float)r.Up.Z);
        }

        var effects = chunks.Where(c => c.Effect != Effect.None).ToList();
        w.Write(effects.Count);
        foreach (var c in effects)
        {
            w.Write((float)c.EffectCenter.X); w.Write((float)c.EffectCenter.Y); w.Write((float)c.EffectCenter.Z);
            w.Write((float)c.EffectDirection.X); w.Write((float)c.EffectDirection.Y); w.Write((float)c.EffectDirection.Z);
            w.Write((float)c.EffectRoadDirection.X); w.Write((float)c.EffectRoadDirection.Y); w.Write((float)c.EffectRoadDirection.Z);
            w.Write((int)c.Effect);
        }

        w.Write(segmentStarts.Length - 1);
        foreach (var d in segmentStarts) w.Write((float)d);
        var placements = rings.Count >= 2 ? TrackObjects.Placements(track, rings, segmentStarts, settings) : new List<ObjectPlacement>();
        w.Write(placements.Count);
        foreach (var p in placements)
        {
            foreach (var v in new[] { p.Position, p.Normal, p.Tangent })
            {
                w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z);
            }
            w.Write((float)p.Distance);
            w.Write((float)p.Across);
            w.Write(p.Placed ? 1 : 0);
        }
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>Concatenates world-space chunks into one mesh (vertices at chunk seams are simply duplicated).</summary>
    private static MeshChunk Merge(List<MeshChunk> chunks)
    {
        var merged = new MeshChunk { Name = "Preview" };
        foreach (var c in chunks.SelectMany(c => c.NotCollidable.Prepend(c)))
        {
            var baseIndex = merged.Positions.Count;
            merged.Positions.AddRange(c.Positions);
            merged.Normals.AddRange(c.Normals);
            merged.Uv0.AddRange(c.Uv0);
            merged.Uv1.AddRange(c.Uv1);
            merged.Surface.AddRange(c.Surface.Count == c.Positions.Count ? c.Surface : Enumerable.Repeat(MeshChunk.NoSurface, c.Positions.Count));
            foreach (var t in c.Triangles)
                merged.Triangles.Add(new Triangle(t.A + baseIndex, t.B + baseIndex, t.C + baseIndex,
                    merged.MaterialIndex(c.Materials[t.Material]), t.Segment));
        }
        return merged;
    }

    /// <summary>The ring lying on each spline point (segment start rings, plus the final ring of an open track).</summary>
    private static Ring[] PointFrames(Track track, List<Ring> rings)
    {
        if (rings.Count == 0)
            return Array.Empty<Ring>();
        var result = new Ring[track.Points.Count];
        foreach (var ring in rings)
            if (ring.IsVertex && result[ring.Segment] is null)
                result[ring.Segment] = ring;
        if (!track.Closed)
            result[^1] = rings[^1];
        for (var i = 0; i < result.Length; i++)
            result[i] ??= rings[0];
        return result;
    }
}
