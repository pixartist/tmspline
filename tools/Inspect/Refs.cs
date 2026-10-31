using GBX.NET;
// Lists the external files a Gbx references (its reference table), e.g. the meshes behind a block definition.
static class Refs
{
    public static void Run(string path)
    {
        var gbx = Gbx.Parse(path);
        var table = gbx.RefTable;
        Console.WriteLine($"{Path.GetFileName(path)}: {table?.Files.Count ?? 0} referenced files, ancestor level {table?.AncestorLevel}");
        foreach (var f in table?.Files ?? [])
            Console.WriteLine($"  {f}");
    }
}
