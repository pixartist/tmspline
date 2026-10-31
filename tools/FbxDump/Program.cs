using System.IO.Compression;
using System.Text;
// Dumps a binary FBX (7.x) file as an indented node tree. Arrays are truncated.
if (args[0] == "--top") { TransplantTop.Run(args[1], args[2], args[3], args[4]); Console.WriteLine("written"); return; }
if (args[0] == "--transplant") { Transplant.Run(args[1], args[2], args[3], args[4]); Console.WriteLine("written"); return; }
if (args[0] == "--roundtrip") { RoundTrip.Run(args[1], args[2], args.Contains("--zlib")); Console.WriteLine("written"); return; }
var data = File.ReadAllBytes(args[0]);
var r = new BinaryReader(new MemoryStream(data));
r.BaseStream.Position = 23;
var version = r.ReadUInt32();
Console.WriteLine($"version {version}");
var wide = version >= 7500;
while (ReadNode(0)) { }

bool ReadNode(int depth)
{
    long end = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
    long numProps = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
    long propLen = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
    var nameLen = r.ReadByte();
    if (end == 0) return false;
    var name = Encoding.ASCII.GetString(r.ReadBytes(nameLen));
    var props = new List<string>();
    for (var i = 0; i < numProps; i++) props.Add(ReadProp());
    Console.WriteLine($"{new string(' ', depth * 2)}{name}: {string.Join(", ", props)}");
    while (r.BaseStream.Position < end)
        if (!ReadNode(depth + 1)) break;
    r.BaseStream.Position = end;
    return true;
}

string ReadProp()
{
    var t = (char)r.ReadByte();
    switch (t)
    {
        case 'Y': return r.ReadInt16().ToString();
        case 'C': return r.ReadByte() != 0 ? "T" : "F";
        case 'I': return r.ReadInt32().ToString();
        case 'F': return r.ReadSingle().ToString("R");
        case 'D': return r.ReadDouble().ToString("R");
        case 'L': return r.ReadInt64().ToString();
        case 'S': case 'R':
        {
            var len = r.ReadInt32(); var b = r.ReadBytes(len);
            return t == 'S' ? "\"" + Encoding.UTF8.GetString(b).Replace("\0\u0001", "::") + "\"" : $"<raw {len}>";
        }
        default:
        {
            var count = r.ReadInt32(); var enc = r.ReadInt32(); var clen = r.ReadInt32();
            var raw = r.ReadBytes(clen);
            if (enc == 1) { using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress); var ms = new MemoryStream(); z.CopyTo(ms); raw = ms.ToArray(); }
            var br = new BinaryReader(new MemoryStream(raw));
            var items = new List<string>();
            for (var i = 0; i < Math.Min(count, 12); i++)
                items.Add(t switch { 'f' => br.ReadSingle().ToString("R"), 'd' => br.ReadDouble().ToString("R"), 'l' => br.ReadInt64().ToString(), 'i' => br.ReadInt32().ToString(), _ => br.ReadByte().ToString() });
            return $"{t}[{count}]{{{string.Join(",", items)}{(count > 12 ? ",..." : "")}}}";
        }
    }
}
