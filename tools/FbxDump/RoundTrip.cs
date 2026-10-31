using System.IO.Compression;
using System.Text;

// Parses a binary FBX into a node tree and writes it back using the same encoding rules as FbxBinaryWriter.
static class RoundTrip
{
    public sealed class N { public string Name = ""; public List<(char T, byte[] Raw, int Count, int Enc)> Props = new(); public List<N> Children = new(); public bool HadSentinel; }

    public static void Run(string input, string output, bool forceZlib, Func<List<N>, List<N>>? edit = null)
    {
        var data = File.ReadAllBytes(input);
        var r = new BinaryReader(new MemoryStream(data));
        r.BaseStream.Position = 27;
        var nodes = new List<N>();
        while (Read(r, out var n)) nodes.Add(n!);
        if (edit != null) nodes = edit(nodes);

        using var fs = File.Create(output);
        var w = new BinaryWriter(fs);
        w.Write(data, 0, 27);
        foreach (var n in nodes) Write(w, n, forceZlib);
        w.Write(new byte[13]);
        w.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
        w.Write(new byte[4]);
        var ofs = fs.Position; var pad = (int)(((ofs + 15) & ~15L) - ofs);
        w.Write(new byte[pad == 0 ? 16 : pad]);
        w.Write(7400); w.Write(new byte[120]);
        w.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
    }

    static bool Read(BinaryReader r, out N? n)
    {
        n = null;
        long end = r.ReadUInt32(); long np = r.ReadUInt32(); r.ReadUInt32(); var nl = r.ReadByte();
        if (end == 0) return false;
        n = new N { Name = Encoding.ASCII.GetString(r.ReadBytes(nl)) };
        for (var i = 0; i < np; i++)
        {
            var t = (char)r.ReadByte();
            switch (t)
            {
                case 'Y': n.Props.Add((t, r.ReadBytes(2), 0, 0)); break;
                case 'C': n.Props.Add((t, r.ReadBytes(1), 0, 0)); break;
                case 'I': case 'F': n.Props.Add((t, r.ReadBytes(4), 0, 0)); break;
                case 'D': case 'L': n.Props.Add((t, r.ReadBytes(8), 0, 0)); break;
                case 'S': case 'R': { var len = r.ReadInt32(); n.Props.Add((t, r.ReadBytes(len), 0, 0)); break; }
                default:
                {
                    var count = r.ReadInt32(); var enc = r.ReadInt32(); var clen = r.ReadInt32(); var raw = r.ReadBytes(clen);
                    if (enc == 1) { using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress); var ms = new MemoryStream(); z.CopyTo(ms); raw = ms.ToArray(); }
                    n.Props.Add((t, raw, count, enc));
                    break;
                }
            }
        }
        while (r.BaseStream.Position < end)
        {
            if (!Read(r, out var c)) { n.HadSentinel = true; break; }
            n.Children.Add(c!);
        }
        r.BaseStream.Position = end;
        return true;
    }

    static void Write(BinaryWriter w, N n, bool forceZlib)
    {
        var s = w.BaseStream; var start = s.Position;
        w.Write(0u); w.Write((uint)n.Props.Count); w.Write(0u);
        var nb = Encoding.ASCII.GetBytes(n.Name); w.Write((byte)nb.Length); w.Write(nb);
        var ps = s.Position;
        foreach (var (t, raw, count, enc) in n.Props)
        {
            w.Write((byte)t);
            if (t is 'S' or 'R') { w.Write(raw.Length); w.Write(raw); }
            else if (char.IsLower(t))
            {
                var z = forceZlib || enc == 1;
                var payload = raw;
                if (z) { var ms = new MemoryStream(); using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw); payload = ms.ToArray(); }
                w.Write(count); w.Write(z ? 1 : 0); w.Write(payload.Length); w.Write(payload);
            }
            else w.Write(raw);
        }
        var pl = s.Position - ps;
        foreach (var c in n.Children) Write(w, c, forceZlib);
        if (n.Children.Count > 0 || n.Props.Count == 0) w.Write(new byte[13]);
        var end = s.Position; s.Position = start; w.Write((uint)end); s.Position = start + 8; w.Write((uint)pl); s.Position = end;
    }
}
