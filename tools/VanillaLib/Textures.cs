using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using GBX.NET;
using GBX.NET.Engines.Plug;
using SkiaSharp;

/// <summary>
/// How the editor draws a material: its diffuse texture (WebP, at most <see cref="Textures.MaxSize"/> px), how it is mapped
/// and how its alpha is used.
/// </summary>
/// <param name="Projection">"uv" (the mesh's UV0), "py" (projected from above, world X/Z) or "pypxz" (from above on
/// floors, from the side on walls).</param>
/// <param name="Scale">Texture repeats per UV unit, or per metre for projected materials.</param>
/// <param name="Alpha">"opaque", "blend" (transparent) or "decal" (an overlay on other geometry).</param>
/// <param name="Parent">The game's material template (shader family), for reference.</param>
record TextureInfo(string? Texture, string Projection, float[] Scale, string Alpha, string? Parent);

static class Textures
{
    public const int MaxSize = 512;
    static readonly ConcurrentDictionary<string, byte> written = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, TextureInfo> Export(IEnumerable<string> materials, IReadOnlyDictionary<string, string> materialFiles,
        Func<string, string[]> references, string outDir)
    {
        var result = new ConcurrentDictionary<string, TextureInfo>();
        var images = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(materials, name =>
        {
            if (!materialFiles.TryGetValue(name, out var file)) return;
            var refs = references(file);
            var parent = refs.Where(r => r.Contains(@"\Media\Material\", StringComparison.OrdinalIgnoreCase))
                .Select(r => Path.GetFileName(r).Split('.')[0]).FirstOrDefault();
            var p = parent ?? "";
            var alpha = p.Contains("Decal") ? "decal" : p.Contains("DiffA") || p.Contains("Blend") ? "blend" : "opaque";
            var diffuse = refs.FirstOrDefault(r => r.EndsWith("_D.Texture.gbx", StringComparison.OrdinalIgnoreCase))
                ?? refs.FirstOrDefault(r => Path.GetFileName(r).Contains("_D.", StringComparison.OrdinalIgnoreCase)
                    && r.EndsWith(".Texture.gbx", StringComparison.OrdinalIgnoreCase));
            string? texture = null;
            var scale = new[] { 1f, 1f };
            if (diffuse is not null && File.Exists(diffuse))
            {
                try
                {
                    if (Gbx.Parse(diffuse).Node is CPlugBitmap bitmap && bitmap.DefaultTexCoordScale is { X: > 0, Y: > 0 } s)
                        scale = [s.X, s.Y];
                }
                catch { /* keep the default scale */ }
                var dds = references(diffuse).FirstOrDefault(r => r.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                var keepAlpha = alpha != "opaque";
                // The game's default colour: the colour table's first entry, painted where the diffuse's hue mask is.
                var hueTexture = refs.FirstOrDefault(r => Path.GetFileName(r).StartsWith(Path.GetFileName(diffuse).Split('.')[0] + "_HueMask", StringComparison.OrdinalIgnoreCase));
                var hueMask = hueTexture is null ? null : references(hueTexture).FirstOrDefault(r => r.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
                var tint = hueMask is null ? null : FirstColour(refs.FirstOrDefault(r => r.EndsWith(".ColorTable.gbx.json", StringComparison.OrdinalIgnoreCase)));
                if (tint is null) hueMask = null; // same image, same file
                if (dds is not null)
                    texture = images.GetOrAdd($"{dds}|{keepAlpha}|{hueMask}|{tint}", _ => Convert(dds, outDir, keepAlpha, hueMask, tint));
            }
            // Projected (world-space) materials: the template says so and the texture scale is per metre (below 1).
            var projected = alpha != "decal" && scale[0] < 1 && (p.Contains("Py") || p.Contains("Pxz") || p.Contains(" PDiff"));
            var projection = !projected ? "uv" : p.Contains("Pxz") ? "pypxz" : "py";
            result[name] = new TextureInfo(texture, projection, scale, alpha, parent);
        });
        return result.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);
    }

    public static void ConvertForDebug(string dds, string outDir)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "texture"));
        Console.WriteLine(Convert(dds, outDir, keepAlpha: false));
        Console.WriteLine(Convert(dds, outDir, keepAlpha: true));
    }

    /// <summary>Decodes a DDS image (BC1/BC3/BC4/BC5) at the first mip level no larger than MaxSize.</summary>
    static (int W, int H, ColorRgba32[] Pixels)? Decode(string dds, int maxSize = MaxSize)
    {
        var data = File.ReadAllBytes(dds);
        if (data.Length < 128 || BitConverter.ToUInt32(data, 0) != 0x20534444) return null;
        int height = BitConverter.ToInt32(data, 12), width = BitConverter.ToInt32(data, 16);
        var mips = Math.Max(1, BitConverter.ToInt32(data, 28));
        var fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
        var (format, blockBytes) = fourCC switch
        {
            "DXT1" => (CompressionFormat.Bc1WithAlpha, 8),
            "DXT5" => (CompressionFormat.Bc3, 16),
            "ATI1" => (CompressionFormat.Bc4, 8),
            "ATI2" => (CompressionFormat.Bc5, 16),
            _ => ((CompressionFormat?)null, 0),
        };
        if (format is null) return null;
        var offset = 128;
        for (var mip = 0; mip < mips - 1 && (width > maxSize || height > maxSize); mip++)
        {
            offset += Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        var size = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
        if (offset + size > data.Length) return null;
        return (width, height, new BcDecoder().DecodeRaw(data.AsSpan(offset, size).ToArray(), width, height, format.Value));
    }

    /// <summary>
    /// Converts a DDS image to WebP. With a hue mask and a colour, the masked parts take the colour's hue and saturation
    /// (the colour table's first entry, the default look), keeping their brightness.
    /// </summary>
    static string? Convert(string dds, string outDir, bool keepAlpha, string? hueMask = null, (byte R, byte G, byte B)? tint = null)
    {
        try
        {
            if (Decode(dds) is not var (width, height, pixels)) return null;
            float[]? mask = null;
            if (hueMask is not null && tint is not null && File.Exists(hueMask) && Decode(hueMask) is var (mw, mh, mp))
            {
                mask = new float[width * height];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var m = mp[y * mh / height * mw + x * mw / width];
                        mask[y * width + x] = Math.Max(m.r, Math.Max(m.g, m.b)) / 255f;
                    }
            }
            static float Lum(ColorRgba32 c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
            // A hue mask swaps hue and saturation for the colour's, keeping each pixel's brightness.
            var tintLum = tint is { } tc ? Math.Max(0.05f, (0.299f * tc.R + 0.587f * tc.G + 0.114f * tc.B) / 255f) : 1f;
            var bytes = new byte[pixels.Length * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                float r = pixels[i].r, g = pixels[i].g, b = pixels[i].b;
                if (mask is not null && tint is { } t && mask[i] > 0)
                {
                    var k = Lum(pixels[i]) / tintLum;
                    r += (Math.Min(255, t.R * k) - r) * mask[i];
                    g += (Math.Min(255, t.G * k) - g) * mask[i];
                    b += (Math.Min(255, t.B * k) - b) * mask[i];
                }
                bytes[i * 4] = (byte)Math.Clamp(r, 0, 255);
                bytes[i * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                bytes[i * 4 + 2] = (byte)Math.Clamp(b, 0, 255);
                bytes[i * 4 + 3] = keepAlpha ? pixels[i].a : (byte)255;
            }
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Webp, 85);
            // Modifier folders hold images with the same names as the originals: those get the modifier's name.
            var modifier = dds.Split(Path.DirectorySeparatorChar).SkipWhile(d => !d.Equals("Modifier", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var colour = mask is not null && tint is { } c ? $"_{c.R:x2}{c.G:x2}{c.B:x2}" : "";
            var file = $"texture/{(modifier is null ? "" : modifier + "_")}{Path.GetFileNameWithoutExtension(dds)}{colour}{(keepAlpha ? "_A" : "")}.webp";
            // Same-named images (e.g. in Stadium and Techno3) share the file: the first one written wins.
            if (written.TryAdd(file, 0)) File.WriteAllBytes(Path.Combine(outDir, file), encoded.ToArray());
            return file;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  texture {Path.GetFileName(dds)}: {e.GetType().Name} {e.Message}");
            return null;
        }
    }

    /// <summary>The first colour of a colour table (.ColorTable.gbx.json, "Colors": ["#rrggbbaa", ...]).</summary>
    static (byte, byte, byte)? FirstColour(string? table)
    {
        if (table is null || !File.Exists(table)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(table), @"""Colors""\s*:\s*\[\s*""#([0-9a-fA-F]{6})");
        if (!m.Success) return null;
        var hex = System.Convert.ToInt32(m.Groups[1].Value, 16);
        return ((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    }
}
