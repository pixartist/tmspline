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
                if (dds is not null)
                    texture = images.GetOrAdd($"{dds}|{keepAlpha}", _ => Convert(dds, outDir, keepAlpha));
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

    /// <summary>Decodes a DDS image (BC1/BC3/BC4/BC5) at the first mip level no larger than MaxSize and saves it as WebP.</summary>
    static string? Convert(string dds, string outDir, bool keepAlpha)
    {
        try
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
            for (var mip = 0; mip < mips - 1 && (width > MaxSize || height > MaxSize); mip++)
            {
                offset += Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            var size = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
            if (offset + size > data.Length) return null;
            var pixels = new BcDecoder().DecodeRaw(data.AsSpan(offset, size).ToArray(), width, height, format.Value);
            var bytes = new byte[pixels.Length * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                bytes[i * 4] = pixels[i].r;
                bytes[i * 4 + 1] = pixels[i].g;
                bytes[i * 4 + 2] = pixels[i].b;
                bytes[i * 4 + 3] = keepAlpha ? pixels[i].a : (byte)255;
            }
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Webp, 85);
            // Modifier folders hold images with the same names as the originals: those get the modifier's name.
            var modifier = dds.Split(Path.DirectorySeparatorChar).SkipWhile(d => !d.Equals("Modifier", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            var file = $"texture/{(modifier is null ? "" : modifier + "_")}{Path.GetFileNameWithoutExtension(dds)}{(keepAlpha ? "_A" : "")}.webp";
            File.WriteAllBytes(Path.Combine(outDir, file), encoded.ToArray());
            return file;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  texture {Path.GetFileName(dds)}: {e.GetType().Name} {e.Message}");
            return null;
        }
    }
}
