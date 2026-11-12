using GBX.NET;
using GBX.NET.Engines.Game;
// Turns a map into a neutral, empty template: no blocks/items/embedded data, placeholder author, no thumbnail.
static class Template
{
    /// <param name="keepBlocks">For the environments whose empty map is water blocks all over: they stay.</param>
    public static void Run(string input, string output, bool keepBlocks = false)
    {
        var gbx = Gbx.Parse<CGameCtnChallenge>(input);
        var map = gbx.Node;
        if (keepBlocks) map.RemoveAnchoredObjects(_ => true);
        else map.RemoveAll();
        map.EmbeddedZipData = null;
        map.MapName = "TMSpline Template";
        map.Comments = "";
        map.AuthorLogin = "tmspline";
        map.AuthorNickname = "TMSpline";
        map.AuthorZone = "World";
        map.MapInfo = new Ident(map.MapUid, map.MapInfo.Collection, "tmspline");
        map.Thumbnail = null;
        TmSpline.Export.MapExporter.SyncHeaderXml(map);
        gbx.Save(output);
        Console.WriteLine($"Wrote {output}");
    }
}
