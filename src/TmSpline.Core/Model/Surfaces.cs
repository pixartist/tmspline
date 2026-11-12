namespace TmSpline.Core.Model;

/// <summary>
/// A material as it ends up in the .MeshParams.xml: Link from NadeoImporterMaterialLib.txt, optional PhysicsId override,
/// optional Color (hex RGB, only for colorizable materials), optional GameplayId (only for <see cref="Surfaces.SupportsEffects"/>).
/// </summary>
/// <param name="GamePath">
/// When set, the compiled item's material is changed after the import to use this game material directly
/// (IsUsingGameMaterial, Link = the full path below GameData): only then does the game take the collision physics from
/// the material's PhysicsId instead of the linked material's own (see <see cref="Surfaces.Road(Surface)"/>, magnets).
/// </param>
public sealed record TmMaterial(string Name, string Link, string? PhysicsId = null, string? Color = null, string? GameplayId = null,
    string? GamePath = null);

public static class Surfaces
{
    public static readonly TmMaterial Border = new("Border", "TrackBorders");
    /// <summary>Shiny yellow plastic with Plastic physics (as on inflatable items): grindable, bouncy.</summary>
    public static readonly TmMaterial PlasticBorder = new("BorderPlastic", "CustomPlasticShiny", "Plastic", "E8B020");

    public static TmMaterial BorderOf(BorderMaterial material) => material == BorderMaterial.Plastic ? PlasticBorder : Border;
    /// <summary>Underside and plain side walls: near-black matte concrete, like the native roads.</summary>
    public static readonly TmMaterial Underside = new("Underside", "CustomConcrete", Color: "141416");

    /// <summary>
    /// Own terrain: the hill blocks' materials (grass; dirt with sand physics; ice), textured by world position like
    /// them (projection "pypxz", 32 m tiles), so the terrain looks like the DecoHill pieces.
    /// </summary>
    public static TmMaterial Terrain(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Dirt => new("TerrainDirt", "PlatformDirt_DecoHill"),
        TerrainSurface.Ice => new("TerrainIce", "PlatformIce_DecoHill"),
        _ => new("TerrainGrass", "DecoHill"),
    };

    /// <summary>
    /// Own terrain's steep faces and skirts: the cliff walls' rock (grass, dirt or ice version). Not in the importer's
    /// material library: imported as the hill material (same UV layers) and then made the game's own material
    /// (<see cref="TmMaterial.GamePath"/>), which also gives it the cliffs' surface.
    /// </summary>
    public static TmMaterial TerrainRock(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Dirt => new("TerrainRockDirt", "PlatformDirt_DecoHill", GamePath: @"Stadium\Media\Modifier\PlatformDirt\DecoCliff"),
        TerrainSurface.Ice => new("TerrainRockIce", "PlatformIce_DecoHill", GamePath: @"Stadium\Media\Modifier\PlatformIce\DecoCliff"),
        _ => new("TerrainRock", "DecoHill", GamePath: @"Stadium\Media\Material\DecoCliff"),
    };

    /// <summary>Pillars under the road: light grey concrete.</summary>
    public static readonly TmMaterial Pillar = new("Pillar", "CustomConcrete", Color: "8C9096");

    private static TmMaterial Magnet(string name, string physics) =>
        new(name, "Structure", physics, GamePath: @"Stadium\Media\Material\Structure");

    /// <summary>Material links that accept a GameplayId, per the NadeoImporter MeshParams documentation.</summary>
    private static readonly HashSet<string> EffectLinks = new()
    {
        "RoadTech", "RoadDirt", "RoadBump", "RoadIce", "PlatformTech",
        "PlatformDirt_PlatformTech", "PlatformGrass_PlatformTech", "PlatformIce_PlatformTech",
    };

    public static bool SupportsEffects(Surface surface) => EffectLinks.Contains(Road(surface).Link);

    /// <summary>
    /// Non-collidable boost marking laid over the road. The texture covers one road width per square, with the
    /// chevrons pointing along +V (verified in game with the rotation probe map).
    /// </summary>
    /// <remarks>The non-boost decals are assumed to share the boost texture layout; NoGrip, ForceAcceleration and the
    /// bumpers have no decal in the material library.</remarks>
    public static TmMaterial? Decal(Effect effect)
    {
        var link = effect switch
        {
            Effect.Turbo => "Turbo_Decal",
            Effect.Turbo2 => "Turbo2_Decal",
            Effect.ReactorBoost => "Boost_Decal",
            Effect.ReactorBoost2 => "Boost2_Decal",
            Effect.SlowMotion => "SlowMotion_Decal",
            Effect.NoSteering => "NoSteering_Decal",
            Effect.NoBrakes => "NoBrake_Decal",
            Effect.FreeWheeling => "NoEngine_Decal",
            Effect.Cruise => "Cruise_Decal",
            Effect.Fragile => "Fragile_Decal",
            Effect.Reset => "Reset_Decal",
            _ => null,
        };
        return link is null ? null : new TmMaterial($"Decal_{effect}", link);
    }

    /// <summary>
    /// See-through plane inside checkpoint/finish gates: light blue waterfall glass, tiled so finely that it looks
    /// frosted (WaypointBuilder). It must be a natively non-collidable material: the game ignores a
    /// PhysicsId="NotCollidable" override for collision.
    /// </summary>
    /// <remarks>
    /// In the item it is made the game's own checkpoint effect (RaceTriggerFXCheckpoint, the plane in the game's
    /// checkpoint blocks; RaceTriggerFXFinish, red, on finish gates). The importer's material list has no such
    /// materials, so the item is imported with the glass and the link is set afterwards (GamePath, as for the cliffs'
    /// rock). The game has no such effect for starts (its start blocks have no plane).
    /// </remarks>
    public static readonly TmMaterial GateCurtainFinish = new("GateCurtainFinish", "GlassWaterWall", Color: "FF6060", GamePath: @"Stadium\Media\Material\RaceTriggerFXFinish");
    public static readonly TmMaterial GateCurtain = new("GateCurtain", "GlassWaterWall", Color: "80C8FF", GamePath: @"Stadium\Media\Material\RaceTriggerFXCheckpoint");

    /// <summary>The frame of an effect or car gate of our own (GateObjects), in its effect's colour (the editor's GATE_COLORS mirrors them).</summary>
    public static TmMaterial EffectGate(string gate) => new($"Gate_{gate}", "CustomConcrete", Color: gate switch
    {
        "Turbo" => "F0D020", "Turbo2" => "E03020", "TurboRoulette" => "F08020",
        "ReactorBoost" or "ReactorBoostOriented" => "F0B030", "ReactorBoost2" or "ReactorBoost2Oriented" => "E04030",
        "SlowMotion" => "40A0F0", "NoSteering" => "C050E0", "NoBrakes" => "E06090", "FreeWheeling" => "808890",
        "Cruise" => "30C0C0", "Fragile" => "F07030", "Reset" => "40D060",
        "CarSnow" => "A0D8FF", "CarRally" => "C08040", "CarDesert" => "E0C060",
        "NoGrip" => "70B0E0", "ForceAcceleration" => "60E060", "Bumper" => "E0A0C0", "Bumper2" => "E060A0",
        _ => "E0E0E0",
    });

    /// <summary>
    /// The see-through plane in a gate of our own: the game's effect plane of that effect, by the importer's name for
    /// it (TriggerFXTurbo for Turbo, [effect]_TriggerFX for the others; the names a tester read from the game's gates).
    /// They show only with vertex colours (white: fully there; MeshChunk.WhiteVertexColors), which the importer keeps
    /// only for materials its list declares them for: the exporter adds that (ImporterFiles.Prepare). Where the game
    /// has no plane of its own for an effect (the cars, and the road effects without a gate of the game's), the
    /// checkpoints' plane.
    /// </summary>
    public static TmMaterial EffectGatePlane(string gate)
    {
        var folder = gate switch
        {
            "Turbo2" or "TurboRoulette" or "SlowMotion" or "NoSteering" or "Cruise" or "Fragile" or "Reset" => gate,
            "ReactorBoost" or "ReactorBoostOriented" => "Boost", "ReactorBoost2" or "ReactorBoost2Oriented" => "Boost2",
            "NoBrakes" => "NoBrake", "FreeWheeling" => "NoEngine",
            _ => null,
        };
        // The cars' gates have one plane of their own in the game (a neutral white): imported as the turbo plane, so
        // that its vertex colours are kept, and made that material afterwards.
        return gate == "Turbo" ? new($"GateFx_{gate}", "TriggerFXTurbo")
            : folder is not null ? new($"GateFx_{gate}", $"{folder}_TriggerFX")
            : gate.StartsWith("Car", StringComparison.Ordinal) ? new($"GateFx_{gate}", "TriggerFXTurbo", GamePath: @"Stadium\Media\Material\TriggerFXGateGameplay")
            : GateCurtain with { Name = $"GateFx_{gate}" };
    }

    /// <summary>Visible gate of a race waypoint, colored like the samples (start green, finish red ...).</summary>
    public static TmMaterial Gate(Waypoint waypoint) => waypoint switch
    {
        Waypoint.Start => new("Gate_Start", "CustomConcrete", Color: "20D040"),
        Waypoint.Checkpoint => new("Gate_Checkpoint", "CustomConcrete", Color: "2080FF"),
        Waypoint.Finish => new("Gate_Finish", "CustomConcrete", Color: "E02020"),
        _ => new("Gate_StartFinish", "CustomConcrete", Color: "F0C020"),
    };

    /// <summary>
    /// The driving surface material, with the segment's gameplay effect if the surface supports one. The sausage road
    /// is its own surface (RoadBump), picked like the others, whatever the cross-section.
    /// </summary>
    public static TmMaterial Road(Surface surface, Effect effect)
    {
        var road = Road(surface);
        if (effect == Effect.None || !EffectLinks.Contains(road.Link))
            return road;
        // The editor reads "surface_effect" from the name.
        return road with { Name = $"{road.Name}_{effect}", GameplayId = GameplayId(effect) };
    }

    /// <summary>
    /// GameplayId as NadeoImporter 2022_07_12 accepts it. The documentation lists ReactorBoost/ReactorBoost2, but this
    /// importer only knows the "_Legacy" names (verified by importing each value).
    /// </summary>
    public static string GameplayId(Effect effect) => effect switch
    {
        Effect.ReactorBoost => "ReactorBoost_Legacy",
        Effect.ReactorBoost2 => "ReactorBoost2_Legacy",
        _ => effect.ToString(),
    };

    /// <summary>
    /// The white lines along the edges of the game's wooden road: a decal of the game's (as on its SnowRoad blocks),
    /// which the importer knows. It takes its opacity from the mesh's vertex colours (MeshChunk.VertexShade).
    /// </summary>
    public static readonly TmMaterial RoadWoodLine = new("RoadWoodLine", "DecalMarksItems");

    public static TmMaterial Road(Surface surface) => surface switch
    {
        Surface.RoadTech => new("RoadTech", "RoadTech"),
        Surface.RoadDirt => new("RoadDirt", "RoadDirt"),
        Surface.RoadBump => new("RoadBump", "RoadBump"),
        Surface.RoadIce => new("RoadIce", "RoadIce"),
        Surface.PlatformTech => new("PlatformTech", "PlatformTech"),
        Surface.PlatformDirt => new("PlatformDirt", "PlatformDirt_PlatformTech"),
        Surface.PlatformGrass => new("PlatformGrass", "PlatformGrass_PlatformTech"),
        Surface.PlatformIce => new("PlatformIce", "PlatformIce_PlatformTech"),
        Surface.PlatformPlastic => new("PlatformPlastic", "PlatformPlastic_PlatformTech", "Plastic"),
        Surface.Wood => new("Wood", "CustomRoughWood"),
        // The game's wooden road (the material of the SnowRoad blocks' driving surface, read from a map with one such
        // block). The importer's list has no such material: imported as the tech road (a road material, with the same
        // texture layers; as rough wood, which has only a lightmap layer, it drew nothing), with wood physics, and made the
        // game's material afterwards (GamePath, as the magnets and the cliffs' rock).
        Surface.RoadWood => new("RoadWood", "RoadTech", "Wood", GamePath: @"Stadium\Media\Material\ThemeSnowRoad"),
        Surface.Snow => new("Snow", "CustomSnow"),
        Surface.Sand => new("Sand", "CustomSand"),
        Surface.Grass => new("Grass", "CustomGrass"),
        // Magnets: the Structure material (picked by the user from TMSpline_MagnetLooks, 2026-11-02) with the magnet physics,
        // made a game material after the import (GamePath). Tested in game
        // 2026-11-02 (TMSpline_MagnetPhysProbe): the importer's PhysicsId alone does not stick (the game takes the linked
        // material's physics); with IsUsingGameMaterial and the full path it does. CustomConcrete (the old dark look)
        // ignores the physics even then; the mesh modeller's TechSuperMagnetic material sticks but draws as a checker
        // and vanishes from the editor after test drives.
        Surface.Magnet => Magnet("Magnet", "TechMagnetic"),
        Surface.MagnetAccel => Magnet("MagnetAccel", "TechMagneticAccel"),
        Surface.SuperMagnet => Magnet("SuperMagnet", "TechSuperMagnetic"),
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, null),
    };
}
