using TmSpline.Core.Geometry;
using TmSpline.Core.Model;
using TmSpline.Export;

// Builds a map with straight magnet strips side by side (left to right = candidates in order),
// to choose the visual material for magnet surfaces.
const string userDir = @"I:\Dokumente\Trackmania";
var importer = new NativeImporter(@"E:\SteamLibrary\steamapps\common\Trackmania", isolated: false);
var candidates = new[]
{
    new TmMaterial("M1_Technics", "Technics", "TechMagnetic"),
    new TmMaterial("M2_TechnicsSpecials", "TechnicsSpecials", "TechMagnetic"),
    new TmMaterial("M3_MetalPainted", "CustomMetalPainted", "TechMagnetic", "1A1A1A"),
    new TmMaterial("M4_PlasticShiny", "CustomPlasticShiny", "TechMagnetic", "111111"),
    new TmMaterial("M5_ScreenBack", "ScreenBack", "TechMagnetic"),
    new TmMaterial("M6_Structure", "Structure", "TechMagnetic"),
    new TmMaterial("M7_PlatformTech", "PlatformTech", "TechMagnetic"),
};

const string folder = @"TMSpline\MagnetLook";
var meshes = new List<MeshChunk>();
for (var i = 0; i < candidates.Length; i++)
{
    // Strips run along +Z, spaced 24 m apart in X, ordered left to right when driving along +Z
    // (left = +X in this right-handed, Y-up frame).
    var x = (candidates.Length - 1 - i) * 24.0;
    var track = new Track();
    track.Points.Add(new TrackPoint(new Vec3d(x, 10, 0), new Vec3d(0, 0, 20)));
    track.Points.Add(new TrackPoint(new Vec3d(x, 10, 96), new Vec3d(0, 0, 20)));
    track.Segments.Add(new TrackSegment());
    var mesh = RoadMeshBuilder.Build(track, TrackSampler.Sample(track), Vec3d.Zero, candidates[i].Name,
        new RoadMeshSettings { MaxChunkLength = double.PositiveInfinity })[0];
    mesh.Materials[0] = candidates[i]; // the driving surface is always material 0
    meshes.Add(mesh);
}

var items = await ItemExporter.ImportAllAsync(meshes, userDir, folder, "TMSpline", importer, Console.WriteLine);
var output = Path.Combine(userDir, "Maps", "TMSpline", "MagnetLook.Map.Gbx");
MapExporter.Export(Path.Combine(userDir, "Maps", "Autosaves", "AutoSave.Map.Gbx"), output, items,
    new MapExportOptions { MapName = "TMSpline Magnet Look", ItemFolder = folder });
Console.WriteLine($"Wrote {output}");
