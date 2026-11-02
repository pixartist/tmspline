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
    public static readonly TmMaterial GateCurtain = new("GateCurtain", "GlassWaterWall", Color: "80C8FF");

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
