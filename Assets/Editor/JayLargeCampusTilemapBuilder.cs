#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// CP5030 large-scale generator for Jay's Ground Level 1: Block E and Canteen.
///
/// Why this exists:
/// - Team feedback: the previous generated zones (42x24 / 28x24) were too small.
/// - The size/style reference supplied by the team is examples.unitypackage:
///     Assets/Imports/Tiles/Road/*  : 512x256 isometric ground tiles at 512 PPU  -> 1.0 x 0.5 world units per cell
///     Assets/Imports/Tiles/Walls/* : 256x512 isometric wall tiles at 256 PPU    -> 1.0 x 2.0 world units per wall
///   Those sizes match this project's grid (IsometricZAsY, cellSize 1 x 0.5), so this
///   script uses the reference tiles directly for ground, roads and building shells.
///
/// Size policy:
/// - Zone 1 diagram keeps Canteen ~1.5x wider than Block E with equal height.
/// - Sizes were doubled twice from the original 42x24 / 28x24 after team feedback:
///     Canteen: 168 x 96 cells
///     Block E: 112 x 96 cells
///
/// Connectivity (cell coordinates, logged on rebuild so teammates can align):
/// - Canteen north  -> Backyard          : opening x 76..87 at y = 95
/// - Canteen south  -> Block C walkway   : road x 28..32 at y = 0
/// - Canteen east   -> Block E           : walkway y 40..54 + street y 8..13 at x = 167
/// - Block E west   -> Canteen           : walkway y 40..54 + street y 8..13 at x = 0
/// - Block E north  -> Backyard          : opening x 76..87 at y = 95
/// - Block E south  -> Garden            : road x 52..56 at y = 0
/// - Block E east   -> Basketball court  : walkway y 60..71 at x = 111
/// The four-lane street (y 8..13) runs the full width of both zones, so when Block E is
/// placed immediately east of Canteen the street and the y 40..54 walkway line up.
///
/// Install / usage:
/// 1. Import examples.unitypackage (Assets > Import Package > Custom Package) so that
///    Assets/Imports/Tiles/Road and Assets/Imports/Tiles/Walls exist.
/// 2. Keep the old generated prop sprites in Assets/Tiles/Sprites/Jay/GeneratedCanteenBlockE
///    (this script copies the ones it needs and reimports the copies at prop-friendly PPU).
/// 3. Run Tools > CP5030 > Jay Areas (Large) > Setup Reference Tiles + Rebuild Large BlockE + Canteen.
///
/// Note on wall orientation names:
/// - The reference wall sprites bake their cell offset into the canvas, so every wall is
///   placed with a centered anchor. If a wall edge looks mirrored in your Unity version,
///   swap the names inside WallTileName() only - no layout code needs to change.
/// </summary>
public static class JayLargeCampusTilemapBuilder
{
    private const string RoadSpriteFolder = "Assets/Imports/Tiles/Road";
    private const string WallSpriteFolder = "Assets/Imports/Tiles/Walls";
    private const string OldPropSpriteFolder = "Assets/Tiles/Sprites/Jay/GeneratedCanteenBlockE";
    private const string PropSpriteFolder = "Assets/Tiles/Sprites/Jay/LargeCampusProps";
    private const string TileFolder = "Assets/Tiles/Tile Assets/Jay/LargeCampus";
    private const string AreaFolder = "Assets/Levels/Level 1/Areas/Jay";
    private const string LevelRootName = "Levels";
    private const string ZoneParentName = "Level 1 - Zone 1";

    // Bounded sizes derived from the Zone 1 diagram ratio (Canteen ~1.5x Block E width).
    private const int CanteenWidth = 168;
    private const int CanteenHeight = 96;
    private const int BlockEWidth = 112;
    private const int BlockEHeight = 96;

    // Shared edge bands both zones must respect so the merged campus stays walkable.
    private const int StreetY0 = 8;          // Street occupies y 8..13 (edge, 4 lanes, edge).
    private const int StreetLanes = 4;
    private const int StreetY1 = StreetY0 + StreetLanes + 1;
    private const int LinkWalkY0 = 40;       // Canteen<->Block E pedestrian band y 40..54.
    private const int LinkWalkY1 = 54;

    // Ground / road tiles from examples.unitypackage (Assets/Imports/Tiles/Road).
    private const string GroundLeaf = "iso_platform_leaf_alt_512x256";
    private const string GroundPlain = "iso_platform_plain_alt_512x256";
    private const string RoadX = "iso_road_NEtoSW_512x256";
    private const string RoadY = "iso_road_NWtoSE_512x256";
    private const string RoadEdgeXBottom = "iso_roadside_line_512x256";
    private const string RoadEdgeXTop = "iso_roadside_line_top_512x256";
    private const string RoadEdgeYBottom = "iso_roadside_line_NWtoSE_bottom_512x256";
    private const string RoadEdgeYTop = "iso_roadside_line_NWtoSE_top_512x256";

    private static readonly string[] RoadSpriteNames =
    {
        GroundLeaf, GroundPlain, RoadX, RoadY,
        RoadEdgeXBottom, RoadEdgeXTop, RoadEdgeYBottom, RoadEdgeYTop
    };

    private static readonly string[] WallSpriteNames =
    {
        "high_wall_concrete", "high_wall_concrete_N", "high_wall_concrete_top", "high_wall_concrete_TLBR",
        "high_wall_stone", "high_wall_stone_N", "high_wall_stone_top", "high_wall_brick_TLBR_far",
        "low_wall_concrete", "low_wall_concrete_N", "low_wall_concrete_S", "low_wall_concrete_top",
        "low_wall_stone", "low_wall_stone_N", "low_wall_stone_S", "low_wall_stone_top"
    };

    // Prop/decal/elevation sprites reused from the previous generated set. Copies are
    // reimported at 64 PPU so a 64x64 prop occupies roughly one 1.0-unit-wide cell.
    private static readonly string[] PropSpriteNames =
    {
        "cliff_front", "cliff_left", "cliff_right",
        "ramp_ne", "ramp_nw", "ramp_se", "ramp_sw",
        "drop_shadow", "dark_shadow",
        "dining_table", "chair_stack", "crate_stack", "barrel_red", "rubble_pile",
        "bush", "small_tree", "planter", "kitchen_shelf", "kitchen_appliance",
        "queue_posts", "blood_stain", "trash_decal", "collider_debug"
    };

    private sealed class ZoneMaps
    {
        public GameObject Root;
        public int Width;
        public int Height;
        public Tilemap Base;
        public Tilemap Shadows;
        public Tilemap Cliffs;
        public Tilemap Ramps;
        public Tilemap Walls;
        public Tilemap Obstacles;
        public Tilemap Passables;
        public Tilemap Colliders;
    }

    public static void SetupAndRebuildBoth()
    {
        if (!SetupTileAssets())
        {
            return;
        }
        RebuildBothZones();
    }

    /// <summary>
    /// Prepares sprites and tile assets WITHOUT rebuilding the campus zones.
    /// Used by JayNpcDemoBuilder as the first step of the one-click demo setup.
    /// </summary>
    public static bool SetupTileAssets()
    {
        EnsureFolders();
        if (!VerifyReferenceSpritesPresent())
        {
            return false;
        }
        ImportReferenceSprites();
        CopyAndImportPropSprites();
        CreateTileAssets();
        return true;
    }

    public static void RebuildBothZones()
    {
        EnsureFolders();

        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayLargeCampusTilemapBuilder: Missing tiles. Run the Setup menu item first (and make sure examples.unitypackage is imported).");
            return;
        }

        GameObject zoneParent = GetOrCreateZoneParent();

        ResetZone(zoneParent, "Zone1_Canteen");
        ResetZone(zoneParent, "Zone1_BlockE");

        ZoneMaps canteen = CreateZoneMaps(zoneParent, "Zone1_Canteen", CanteenWidth, CanteenHeight, Vector3.zero);
        BuildCanteen(canteen, tiles);
        EnforceZoneBounds(canteen, "Zone1_Canteen");
        SaveZonePrefab(canteen.Root, "Zone1_Canteen");

        // Block E sits immediately east of the Canteen in the Zone 1 diagram. Offsetting the
        // root by CanteenWidth cells lines the shared street/walkway up in the scene view.
        Vector3 blockEOffset = new Vector3(CanteenWidth * 0.5f, CanteenWidth * 0.25f, 0f);
        ZoneMaps blockE = CreateZoneMaps(zoneParent, "Zone1_BlockE", BlockEWidth, BlockEHeight, blockEOffset);
        BuildBlockE(blockE, tiles);
        EnforceZoneBounds(blockE, "Zone1_BlockE");
        SaveZonePrefab(blockE.Root, "Zone1_BlockE");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        LogExits();
        Debug.Log("JayLargeCampusTilemapBuilder: Rebuilt Zone1_Canteen (" + CanteenWidth + "x" + CanteenHeight + ") and Zone1_BlockE (" + BlockEWidth + "x" + BlockEHeight + ").");
    }

    public static void RebuildCanteenOnly()
    {
        EnsureFolders();
        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayLargeCampusTilemapBuilder: Missing tiles. Run the Setup menu item first.");
            return;
        }
        GameObject zoneParent = GetOrCreateZoneParent();
        ResetZone(zoneParent, "Zone1_Canteen");
        ZoneMaps canteen = CreateZoneMaps(zoneParent, "Zone1_Canteen", CanteenWidth, CanteenHeight, Vector3.zero);
        BuildCanteen(canteen, tiles);
        EnforceZoneBounds(canteen, "Zone1_Canteen");
        SaveZonePrefab(canteen.Root, "Zone1_Canteen");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JayLargeCampusTilemapBuilder: Rebuilt Zone1_Canteen only.");
    }

    public static void RebuildBlockEOnly()
    {
        EnsureFolders();
        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayLargeCampusTilemapBuilder: Missing tiles. Run the Setup menu item first.");
            return;
        }
        GameObject zoneParent = GetOrCreateZoneParent();
        ResetZone(zoneParent, "Zone1_BlockE");
        Vector3 blockEOffset = new Vector3(CanteenWidth * 0.5f, CanteenWidth * 0.25f, 0f);
        ZoneMaps blockE = CreateZoneMaps(zoneParent, "Zone1_BlockE", BlockEWidth, BlockEHeight, blockEOffset);
        BuildBlockE(blockE, tiles);
        EnforceZoneBounds(blockE, "Zone1_BlockE");
        SaveZonePrefab(blockE.Root, "Zone1_BlockE");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JayLargeCampusTilemapBuilder: Rebuilt Zone1_BlockE only.");
    }

    public static void ValidateZones()
    {
        bool ok = true;
        ok &= ValidatePrefabBounds(AreaFolder + "/Zone1_Canteen.prefab", "Zone1_Canteen", CanteenWidth, CanteenHeight);
        ok &= ValidatePrefabBounds(AreaFolder + "/Zone1_BlockE.prefab", "Zone1_BlockE", BlockEWidth, BlockEHeight);
        ok &= ValidateTiles(LoadTiles());
        if (ok)
        {
            Debug.Log("JayLargeCampusTilemapBuilder: Validation passed.");
        }
    }

    public static void SelectExportDependencies()
    {
        List<UnityEngine.Object> selected = new List<UnityEngine.Object>();
        AddAssetIfExists(selected, AreaFolder + "/Zone1_Canteen.prefab");
        AddAssetIfExists(selected, AreaFolder + "/Zone1_BlockE.prefab");
        AddFolderAssets(selected, RoadSpriteFolder);
        AddFolderAssets(selected, WallSpriteFolder);
        AddFolderAssets(selected, PropSpriteFolder);
        AddFolderAssets(selected, TileFolder);
        Selection.objects = selected.ToArray();
        Debug.Log("Selected large Jay zone assets for export. Use Assets > Export Package with Include Dependencies enabled.");
    }

    // ---------------------------------------------------------------- setup

    private static void EnsureFolders()
    {
        EnsureFolder("Assets", "Tiles");
        EnsureFolder("Assets/Tiles", "Sprites");
        EnsureFolder("Assets/Tiles/Sprites", "Jay");
        EnsureFolder("Assets/Tiles/Sprites/Jay", "LargeCampusProps");
        EnsureFolder("Assets/Tiles", "Tile Assets");
        EnsureFolder("Assets/Tiles/Tile Assets", "Jay");
        EnsureFolder("Assets/Tiles/Tile Assets/Jay", "LargeCampus");
        EnsureFolder("Assets", "Levels");
        EnsureFolder("Assets/Levels", "Level 1");
        EnsureFolder("Assets/Levels/Level 1", "Areas");
        EnsureFolder("Assets/Levels/Level 1/Areas", "Jay");
        EnsureFolder("Assets", "Editor");
    }

    private static void EnsureFolder(string parent, string child)
    {
        string fullPath = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(fullPath))
        {
            AssetDatabase.CreateFolder(parent, child);
        }
    }

    private static bool VerifyReferenceSpritesPresent()
    {
        bool anyMissing = false;
        for (int i = 0; i < RoadSpriteNames.Length; i++)
        {
            if (AssetImporter.GetAtPath(RoadSpriteFolder + "/" + RoadSpriteNames[i] + ".png") == null)
            {
                anyMissing = true;
                Debug.LogError("Missing reference sprite: " + RoadSpriteFolder + "/" + RoadSpriteNames[i] + ".png");
            }
        }
        for (int i = 0; i < WallSpriteNames.Length; i++)
        {
            if (AssetImporter.GetAtPath(WallSpriteFolder + "/" + WallSpriteNames[i] + ".png") == null)
            {
                anyMissing = true;
                Debug.LogError("Missing reference sprite: " + WallSpriteFolder + "/" + WallSpriteNames[i] + ".png");
            }
        }
        if (anyMissing)
        {
            Debug.LogError("JayLargeCampusTilemapBuilder: Import examples.unitypackage first (Assets > Import Package > Custom Package). It provides Assets/Imports/Tiles/Road and Assets/Imports/Tiles/Walls.");
            return false;
        }
        return true;
    }

    private static void ImportReferenceSprites()
    {
        // Ground diamonds are 512x256 -> 512 PPU gives exactly one 1.0 x 0.5 cell.
        for (int i = 0; i < RoadSpriteNames.Length; i++)
        {
            ApplySpriteImport(RoadSpriteFolder + "/" + RoadSpriteNames[i] + ".png", 512f);
        }
        // Walls are 256x512 -> 256 PPU gives a 1.0-unit-wide, 2.0-unit-tall wall. A couple of
        // package sprites ship at 100 PPU, so this normalizes all of them.
        for (int i = 0; i < WallSpriteNames.Length; i++)
        {
            ApplySpriteImport(WallSpriteFolder + "/" + WallSpriteNames[i] + ".png", 256f);
        }
    }

    private static void CopyAndImportPropSprites()
    {
        for (int i = 0; i < PropSpriteNames.Length; i++)
        {
            string source = OldPropSpriteFolder + "/" + PropSpriteNames[i] + ".png";
            string destination = PropSpriteFolder + "/" + PropSpriteNames[i] + ".png";

            if (AssetImporter.GetAtPath(source) == null)
            {
                Debug.LogWarning("Missing prop sprite in old generated set: " + source);
                continue;
            }
            if (AssetImporter.GetAtPath(destination) == null)
            {
                File.Copy(source, destination, true);
            }
        }
        AssetDatabase.Refresh();

        for (int i = 0; i < PropSpriteNames.Length; i++)
        {
            // 64x64 props at 64 PPU -> 1.0 x 1.0 world units, one cell-width footprint.
            ApplySpriteImport(PropSpriteFolder + "/" + PropSpriteNames[i] + ".png", 64f);
        }
    }

    private static void ApplySpriteImport(string path, float pixelsPerUnit)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            return;
        }
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = pixelsPerUnit;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
    }

    private static void CreateTileAssets()
    {
        for (int i = 0; i < RoadSpriteNames.Length; i++)
        {
            CreateTileAsset(RoadSpriteFolder, RoadSpriteNames[i]);
        }
        for (int i = 0; i < WallSpriteNames.Length; i++)
        {
            CreateTileAsset(WallSpriteFolder, WallSpriteNames[i]);
        }
        for (int i = 0; i < PropSpriteNames.Length; i++)
        {
            CreateTileAsset(PropSpriteFolder, PropSpriteNames[i]);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void CreateTileAsset(string spriteFolder, string spriteName)
    {
        string spritePath = spriteFolder + "/" + spriteName + ".png";
        string tilePath = TileFolder + "/" + spriteName + ".asset";

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        if (sprite == null)
        {
            return;
        }

        Tile tile = AssetDatabase.LoadAssetAtPath<Tile>(tilePath);
        if (tile == null)
        {
            tile = ScriptableObject.CreateInstance<Tile>();
            tile.name = spriteName;
            AssetDatabase.CreateAsset(tile, tilePath);
        }

        tile.sprite = sprite;
        tile.colliderType = spriteName == "collider_debug" ? Tile.ColliderType.Sprite : Tile.ColliderType.None;
        EditorUtility.SetDirty(tile);
    }

    private static Dictionary<string, TileBase> LoadTiles()
    {
        Dictionary<string, TileBase> tiles = new Dictionary<string, TileBase>();
        AddTilesFrom(tiles, RoadSpriteNames);
        AddTilesFrom(tiles, WallSpriteNames);
        AddTilesFrom(tiles, PropSpriteNames);
        return tiles;
    }

    private static void AddTilesFrom(Dictionary<string, TileBase> tiles, string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            TileBase tile = AssetDatabase.LoadAssetAtPath<TileBase>(TileFolder + "/" + names[i] + ".asset");
            if (tile != null)
            {
                tiles[names[i]] = tile;
            }
        }
    }

    private static bool ValidateTiles(Dictionary<string, TileBase> tiles)
    {
        bool ok = true;
        ok &= ValidateTileSet(tiles, RoadSpriteNames);
        ok &= ValidateTileSet(tiles, WallSpriteNames);
        ok &= ValidateTileSet(tiles, PropSpriteNames);
        return ok;
    }

    private static bool ValidateTileSet(Dictionary<string, TileBase> tiles, string[] names)
    {
        bool ok = true;
        for (int i = 0; i < names.Length; i++)
        {
            if (!tiles.ContainsKey(names[i]))
            {
                ok = false;
                Debug.LogWarning("Missing Tile asset: " + names[i]);
            }
        }
        return ok;
    }

    // ---------------------------------------------------------------- scene plumbing

    private static GameObject GetOrCreateZoneParent()
    {
        GameObject levels = GameObject.Find(LevelRootName);
        if (levels == null)
        {
            levels = new GameObject(LevelRootName);
        }

        Transform zoneParentTransform = levels.transform.Find(ZoneParentName);
        GameObject zoneParent = zoneParentTransform != null ? zoneParentTransform.gameObject : new GameObject(ZoneParentName);
        if (zoneParent.transform.parent == null)
        {
            zoneParent.transform.SetParent(levels.transform);
        }

        zoneParent.transform.position = Vector3.zero;
        zoneParent.transform.localRotation = Quaternion.identity;
        zoneParent.transform.localScale = Vector3.one;

        Grid grid = zoneParent.GetComponent<Grid>();
        if (grid == null)
        {
            grid = zoneParent.AddComponent<Grid>();
        }

        grid.cellLayout = GridLayout.CellLayout.IsometricZAsY;
        grid.cellSize = new Vector3(1f, 0.5f, 1f);
        grid.cellGap = Vector3.zero;

        return zoneParent;
    }

    private static void ResetZone(GameObject zoneParent, string zoneName)
    {
        Transform existing = zoneParent.transform.Find(zoneName);
        if (existing != null)
        {
            Object.DestroyImmediate(existing.gameObject);
        }

        string prefabPath = AreaFolder + "/" + zoneName + ".prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            AssetDatabase.DeleteAsset(prefabPath);
        }
    }

    private static ZoneMaps CreateZoneMaps(GameObject zoneParent, string zoneName, int width, int height, Vector3 localOffset)
    {
        GameObject root = new GameObject(zoneName);
        root.transform.SetParent(zoneParent.transform);
        root.transform.localPosition = localOffset;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        ZoneMaps maps = new ZoneMaps();
        maps.Root = root;
        maps.Width = width;
        maps.Height = height;
        maps.Base = CreateLayer(root, "Base", 0, true, false, false);
        maps.Shadows = CreateLayer(root, "Shadows", 1, true, false, false);
        maps.Cliffs = CreateLayer(root, "Cliffs", 2, true, false, false);
        maps.Ramps = CreateLayer(root, "Ramps", 3, true, false, false);
        maps.Walls = CreateLayer(root, "Walls", 4, true, false, true);
        maps.Obstacles = CreateLayer(root, "Obstacles", 5, true, false, true);
        maps.Passables = CreateLayer(root, "Passables", 6, true, false, false);
        maps.Colliders = CreateLayer(root, "Colliders", 7, false, true, false);
        return maps;
    }

    private static Tilemap CreateLayer(GameObject root, string layerName, int sortingOrder, bool visible, bool collider, bool individualSorting)
    {
        GameObject layerObject = new GameObject(layerName);
        layerObject.transform.SetParent(root.transform);
        layerObject.transform.localPosition = Vector3.zero;
        layerObject.transform.localRotation = Quaternion.identity;
        layerObject.transform.localScale = Vector3.one;

        Tilemap tilemap = layerObject.AddComponent<Tilemap>();
        // Reference sprites bake their offsets into the canvas around a centered pivot,
        // so a centered anchor drops every tile onto its cell.
        tilemap.tileAnchor = new Vector3(0.5f, 0.5f, 0f);

        TilemapRenderer renderer = layerObject.AddComponent<TilemapRenderer>();
        renderer.sortingOrder = sortingOrder;
        renderer.enabled = visible;
        if (individualSorting)
        {
            // Walls and props must sort against characters per tile, not per chunk.
            renderer.mode = TilemapRenderer.Mode.Individual;
        }

        if (collider)
        {
            layerObject.AddComponent<TilemapCollider2D>();
        }
        return tilemap;
    }

    private static void SaveZonePrefab(GameObject zoneRoot, string zoneName)
    {
        string prefabPath = AreaFolder + "/" + zoneName + ".prefab";
        PrefabUtility.SaveAsPrefabAsset(zoneRoot, prefabPath);
    }

    // ---------------------------------------------------------------- wall vocabulary

    // Edge codes: "front" = south (low y, x-run), "back" = north (high y, x-run),
    // "left" = west (low x, y-run), "right" = east (high x, y-run).
    // If an edge renders mirrored on your Unity version, swap names here only.
    private static string WallTileName(string material, bool high, string edge)
    {
        if (material == "concrete")
        {
            if (high)
            {
                switch (edge)
                {
                    case "front": return "high_wall_concrete";
                    case "back": return "high_wall_concrete_top";
                    case "left": return "high_wall_concrete_N";
                    default: return "high_wall_concrete_TLBR";
                }
            }
            switch (edge)
            {
                case "front": return "low_wall_concrete";
                case "back": return "low_wall_concrete_top";
                case "left": return "low_wall_concrete_N";
                default: return "low_wall_concrete_S";
            }
        }

        // Stone (Block E). The stone set has no dedicated high right-edge wall, so the
        // brick TLBR sprite doubles as the east edge and reads as a repaired patch.
        if (high)
        {
            switch (edge)
            {
                case "front": return "high_wall_stone";
                case "back": return "high_wall_stone_top";
                case "left": return "high_wall_stone_N";
                default: return "high_wall_brick_TLBR_far";
            }
        }
        switch (edge)
        {
            case "front": return "low_wall_stone";
            case "back": return "low_wall_stone_top";
            case "left": return "low_wall_stone_N";
            default: return "low_wall_stone_S";
        }
    }

    /// <summary>Rectangular building shell on the Walls layer, with colliders.</summary>
    private static void BuildingShell(ZoneMaps map, Dictionary<string, TileBase> t, string material, bool high, int x, int y, int w, int h, int z)
    {
        for (int ix = x; ix < x + w; ix++)
        {
            PlaceWall(map, t, WallTileName(material, high, "front"), ix, y, z);
            PlaceWall(map, t, WallTileName(material, high, "back"), ix, y + h - 1, z);
        }
        for (int iy = y + 1; iy < y + h - 1; iy++)
        {
            PlaceWall(map, t, WallTileName(material, high, "left"), x, iy, z);
            PlaceWall(map, t, WallTileName(material, high, "right"), x + w - 1, iy, z);
        }
    }

    private static void WallRunX(ZoneMaps map, Dictionary<string, TileBase> t, string material, bool high, string edge, int x, int y, int length, int z)
    {
        for (int i = 0; i < length; i++)
        {
            PlaceWall(map, t, WallTileName(material, high, edge), x + i, y, z);
        }
    }

    private static void WallRunY(ZoneMaps map, Dictionary<string, TileBase> t, string material, bool high, string edge, int x, int y, int length, int z)
    {
        for (int i = 0; i < length; i++)
        {
            PlaceWall(map, t, WallTileName(material, high, edge), x, y + i, z);
        }
    }

    private static void PlaceWall(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int z)
    {
        Place(map.Walls, t[tileName], x, y, z);
        Place(map.Colliders, t["collider_debug"], x, y, z);
    }

    /// <summary>Removes wall + collider tiles to create a doorway or exit gap.</summary>
    private static void CutOpening(ZoneMaps map, int x, int y, int w, int h, int z)
    {
        for (int ix = x; ix < x + w; ix++)
        {
            for (int iy = y; iy < y + h; iy++)
            {
                map.Walls.SetTile(new Vector3Int(ix, iy, z), null);
                map.Obstacles.SetTile(new Vector3Int(ix, iy, z), null);
                map.Colliders.SetTile(new Vector3Int(ix, iy, z), null);
            }
        }
    }

    // ---------------------------------------------------------------- roads

    /// <summary>Street running along +x: edge line, asphalt rows, edge line.</summary>
    private static void StreetX(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int length, int lanes)
    {
        for (int i = 0; i < length; i++)
        {
            Place(map.Base, t[RoadEdgeXBottom], x + i, y, 0);
            for (int lane = 0; lane < lanes; lane++)
            {
                Place(map.Base, t[RoadX], x + i, y + 1 + lane, 0);
            }
            Place(map.Base, t[RoadEdgeXTop], x + i, y + 1 + lanes, 0);
        }
    }

    /// <summary>Street running along +y: edge line, asphalt columns, edge line.</summary>
    private static void StreetY(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int length, int lanes)
    {
        for (int i = 0; i < length; i++)
        {
            Place(map.Base, t[RoadEdgeYBottom], x, y + i, 0);
            for (int lane = 0; lane < lanes; lane++)
            {
                Place(map.Base, t[RoadY], x + 1 + lane, y + i, 0);
            }
            Place(map.Base, t[RoadEdgeYTop], x + 1 + lanes, y + i, 0);
        }
    }

    // ---------------------------------------------------------------- canteen

    private static void BuildCanteen(ZoneMaps map, Dictionary<string, TileBase> t)
    {
        // Overgrown campus ground everywhere, paved apron in the middle.
        FillRect(map.Base, t[GroundLeaf], 0, 0, CanteenWidth, CanteenHeight, 0);
        FillRect(map.Base, t[GroundPlain], 12, 20, 144, 66, 0);
        ScatterBase(map, t, GroundLeaf, 12, 20, 144, 66, 0, 0.08f, 5501);
        ScatterBlobs(map.Base, t[GroundLeaf], 12, 20, 144, 66, 0, 10, 5, 0.6f, 5502);

        // Four-lane street across the full south side (continues into Block E when merged).
        StreetX(map, t, 0, StreetY0, CanteenWidth, StreetLanes);
        // Nature reclaiming the asphalt.
        ScatterBase(map, t, GroundLeaf, 0, StreetY0 + 1, CanteenWidth, StreetLanes, 0, 0.05f, 5503);
        // South exit road toward the Block C walkway.
        StreetY(map, t, 28, 0, StreetY0 + 1, 3);

        // Pedestrian band that lines up with Block E's west entrance.
        FillRect(map.Base, t[GroundPlain], 120, LinkWalkY0, 48, LinkWalkY1 - LinkWalkY0 + 1, 0);
        // Backyard path leaving the north edge.
        FillRect(map.Base, t[GroundPlain], 76, 80, 12, 16, 0);

        // Campus perimeter fence (low stone) along west and north, with exit gaps.
        WallRunY(map, t, "stone", false, "left", 1, 20, 70, 0);
        WallRunX(map, t, "stone", false, "back", 2, 92, 164, 0);
        CutOpening(map, 76, 92, 12, 1, 0);  // North gap -> Backyard.

        // Main canteen hall: high concrete shell, paved interior, long shadow at its feet.
        FillRect(map.Base, t[GroundPlain], 20, 36, 60, 41, 0);
        BuildingShell(map, t, "concrete", true, 20, 36, 60, 41, 0);
        FillRect(map.Shadows, t["dark_shadow"], 21, 34, 58, 2, 0);
        CutOpening(map, 44, 36, 8, 1, 0);   // Main entrance (south).
        CutOpening(map, 79, 44, 1, 10, 0);  // Side door east, opens onto the link walkway.
        CutOpening(map, 76, 76, 4, 1, 0);   // Back door north, onto the backyard path.
        CutOpening(map, 20, 52, 1, 6, 0);   // Collapsed west wall section.

        // Panic at the entrance: dried blood pool where the crowd got caught.
        FillRect(map.Shadows, t["dark_shadow"], 45, 37, 6, 4, 0);
        ScatterPassables(map, t, "blood_stain", 43, 36, 10, 7, 0, 0.45f, 5505);
        BloodTrail(map, t, 47, 14, 47, 36, 5506);

        // Storage room (low-wall interior partition, SW corner of the hall).
        WallRunX(map, t, "concrete", false, "front", 22, 38, 11, 0);
        WallRunX(map, t, "concrete", false, "back", 22, 44, 11, 0);
        WallRunY(map, t, "concrete", false, "left", 22, 39, 5, 0);
        WallRunY(map, t, "concrete", false, "right", 32, 39, 5, 0);
        CutOpening(map, 26, 44, 2, 1, 0);   // Door north.
        LineH(map, t, "kitchen_shelf", 23, 39, 3, 0, true);
        PlaceObstacle(map, t, "crate_stack", 24, 41, 0, true);
        PlaceObstacle(map, t, "crate_stack", 25, 41, 0, true);
        PlaceObstacle(map, t, "barrel_red", 29, 41, 0, true);

        // Office (low-wall interior partition, NE corner of the hall).
        WallRunX(map, t, "concrete", false, "front", 66, 64, 12, 0);
        WallRunX(map, t, "concrete", false, "back", 66, 74, 12, 0);
        WallRunY(map, t, "concrete", false, "left", 66, 65, 9, 0);
        WallRunY(map, t, "concrete", false, "right", 77, 65, 9, 0);
        CutOpening(map, 70, 64, 3, 1, 0);   // Door south.
        DeskPair(map, t, 68, 68, 0);
        DeskPair(map, t, 72, 70, 0);
        PlaceObstacle(map, t, "kitchen_shelf", 75, 66, 0, true);
        Place(map.Shadows, t["dark_shadow"], 70, 68, 0);
        Place(map.Passables, t["blood_stain"], 70, 69, 0);

        // Kitchen block: raised platform (z=1) along the north-west of the hall.
        PaintRaisedPlatform(map, t, 22, 58, 23, 17, 1);
        ClearCliffsRect(map, 30, 57, 1, 1, 0);
        ClearCliffsRect(map, 38, 57, 1, 1, 0);
        ClearCliffsRect(map, 45, 66, 1, 2, 0);
        Place(map.Ramps, t["ramp_se"], 30, 57, 0);
        Place(map.Ramps, t["ramp_se"], 38, 57, 0);
        Place(map.Ramps, t["ramp_ne"], 45, 66, 0);
        Place(map.Ramps, t["ramp_ne"], 45, 67, 0);
        LineH(map, t, "kitchen_shelf", 24, 72, 12, 1, true);
        LineH(map, t, "kitchen_appliance", 24, 68, 8, 1, true);
        LineH(map, t, "kitchen_appliance", 34, 64, 8, 1, true);
        LineV(map, t, "kitchen_appliance", 24, 60, 4, 1, true);
        PlaceObstacle(map, t, "crate_stack", 40, 70, 1, true);
        PlaceObstacle(map, t, "barrel_red", 41, 71, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 33, 60, 1, true);
        ScatterPassables(map, t, "blood_stain", 23, 59, 21, 15, 1, 0.06f, 5507);

        // Serving counter: long U facing the dining floor, with a serving pass.
        WallRunX(map, t, "concrete", false, "front", 26, 48, 21, 0);
        WallRunY(map, t, "concrete", false, "left", 26, 49, 5, 0);
        WallRunY(map, t, "concrete", false, "right", 46, 49, 5, 0);
        CutOpening(map, 34, 48, 4, 1, 0);   // Serving pass.
        LineH(map, t, "queue_posts", 28, 45, 17, 0, true);
        LineH(map, t, "queue_posts", 36, 42, 9, 0, true);

        // Dining floor: table grid east of the counters, skipping the office.
        for (int col = 50; col <= 70; col += 6)
        {
            for (int row = 40; row <= 72; row += 6)
            {
                if (col >= 66 && row >= 62)
                {
                    continue;   // Office footprint.
                }
                if (col >= 44 && col <= 52 && row <= 42)
                {
                    continue;   // Entrance blood scene stays open.
                }
                TableCluster(map, t, col, row, 0);
            }
        }
        PlaceObstacle(map, t, "planter", 48, 56, 0, true);
        PlaceObstacle(map, t, "planter", 62, 38, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 22, 50, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 76, 40, 0, true);
        PlaceObstacle(map, t, "crate_stack", 76, 60, 0, true);

        // Outdoor seating terrace: big raised deck (z=1) east of the hall, stone parapet,
        // and the remains of a survivor camp.
        PaintRaisedPlatform(map, t, 108, 58, 43, 25, 1);
        ClearCliffsRect(map, 116, 57, 2, 1, 0);
        ClearCliffsRect(map, 134, 57, 2, 1, 0);
        Place(map.Ramps, t["ramp_se"], 116, 57, 0);
        Place(map.Ramps, t["ramp_se"], 117, 57, 0);
        Place(map.Ramps, t["ramp_se"], 134, 57, 0);
        Place(map.Ramps, t["ramp_se"], 135, 57, 0);
        WallRunX(map, t, "stone", false, "back", 108, 82, 43, 1);
        WallRunY(map, t, "stone", false, "left", 108, 59, 23, 1);
        WallRunY(map, t, "stone", false, "right", 150, 59, 23, 1);
        CutOpening(map, 108, 64, 1, 4, 1);  // West gap toward the hall.
        ClearCliffsRect(map, 107, 64, 1, 4, 0);
        Place(map.Ramps, t["ramp_ne"], 107, 65, 0);
        Place(map.Ramps, t["ramp_ne"], 107, 66, 0);
        for (int col = 112; col <= 144; col += 8)
        {
            for (int row = 62; row <= 78; row += 8)
            {
                TableCluster(map, t, col, row, 1);
            }
        }
        // Survivor camp remnants in the terrace corner.
        Place(map.Shadows, t["dark_shadow"], 146, 76, 1);
        PlaceObstacle(map, t, "barrel_red", 147, 77, 1, true);
        PlaceObstacle(map, t, "crate_stack", 145, 78, 1, true);
        PlaceObstacle(map, t, "crate_stack", 148, 75, 1, true);
        ScatterPassables(map, t, "trash_decal", 142, 72, 8, 8, 1, 0.20f, 5508);
        ScatterPassables(map, t, "blood_stain", 142, 72, 8, 8, 1, 0.10f, 5509);
        PlaceObstacle(map, t, "planter", 112, 80, 1, true);
        PlaceObstacle(map, t, "planter", 128, 80, 1, true);

        // Bus shelter by the street.
        WallRunX(map, t, "stone", false, "back", 60, 19, 13, 0);
        LineH(map, t, "queue_posts", 62, 17, 9, 0, true);
        ScatterPassables(map, t, "trash_decal", 60, 15, 13, 5, 0, 0.25f, 5510);
        Place(map.Passables, t["blood_stain"], 65, 16, 0);

        // Failed roadblock across the street, with a fought-over gap.
        Barricade(map, t, 99, StreetY0, 3, 5, 5511);
        ClearGameplayRect(map, 99, StreetY1 - 1, 3, 1, 0);   // The lane they broke through.
        FillRect(map.Shadows, t["dark_shadow"], 97, StreetY0 + 1, 2, 2, 0);
        BloodTrail(map, t, 96, StreetY0 + 2, 108, StreetY1 + 4, 5512);

        // Street wreckage.
        PlaceObstacle(map, t, "crate_stack", 20, 15, 0, true);
        PlaceObstacle(map, t, "barrel_red", 21, 15, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 22, 14, 0, true);
        PlaceObstacle(map, t, "barrel_red", 130, 14, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 131, 15, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 6, 15, 0, true);

        // Greenery and decay across the whole zone.
        ScatterBlobs(map.Passables, t["bush"], 2, 16, 164, 76, 0, 14, 4, 0.5f, 5513);
        ScatterPassables(map, t, "bush", 2, 16, 164, 76, 0, 0.012f, 5514);
        ScatterPassables(map, t, "blood_stain", 14, 16, 140, 70, 0, 0.015f, 5515);
        ScatterPassables(map, t, "trash_decal", 4, 14, 160, 76, 0, 0.030f, 5516);
        int[] treeXs = { 4, 3, 5, 4, 20, 60, 100, 140, 160, 160, 104, 100 };
        int[] treeYs = { 24, 48, 72, 88, 88, 88, 88, 88, 24, 70, 84, 16 };
        for (int i = 0; i < treeXs.Length; i++)
        {
            PlaceObstacle(map, t, "small_tree", treeXs[i], treeYs[i], 0, true);
        }

        // Guaranteed walking lanes: entrances, hall circulation, exits.
        ClearGameplayRect(map, 44, 14, 8, 22, 0);                      // Street -> main entrance.
        ClearGameplayRect(map, 21, 54, 58, 3, 0);                      // Cross-hall lane below the kitchen.
        ClearGameplayRect(map, 34, 42, 4, 8, 0);                       // Queue lane through the serving pass.
        ClearGameplayRect(map, 80, LinkWalkY0, 88, LinkWalkY1 - LinkWalkY0 + 1, 0);   // East link walkway.
        ClearGameplayRect(map, 76, 77, 12, 19, 0);                     // North backyard path.
        ClearGameplayRect(map, 28, 0, 5, 15, 0);                       // South exit road.
        ClearGameplayRect(map, 116, 55, 2, 3, 0);                      // Terrace ramp approaches.
        ClearGameplayRect(map, 134, 55, 2, 3, 0);
        ClearGameplayRect(map, 104, 64, 4, 4, 0);                      // Terrace west gap approach.

        CompressAll(map);
    }

    // ---------------------------------------------------------------- block e

    private static void BuildBlockE(ZoneMaps map, Dictionary<string, TileBase> t)
    {
        // Heavier overgrowth than the Canteen: leaf ground with a paved courtyard core.
        FillRect(map.Base, t[GroundLeaf], 0, 0, BlockEWidth, BlockEHeight, 0);
        FillRect(map.Base, t[GroundPlain], 28, 28, 50, 40, 0);
        ScatterBase(map, t, GroundLeaf, 28, 28, 50, 40, 0, 0.15f, 6601);
        ScatterBlobs(map.Base, t[GroundLeaf], 28, 28, 50, 40, 0, 8, 4, 0.6f, 6602);

        // The street continues from the Canteen across the whole zone.
        StreetX(map, t, 0, StreetY0, BlockEWidth, StreetLanes);
        ScatterBase(map, t, GroundLeaf, 0, StreetY0 + 1, BlockEWidth, StreetLanes, 0, 0.07f, 6603);
        // South exit road down to the Garden.
        StreetY(map, t, 52, 0, StreetY0 + 1, 3);

        // West link walkway to the Canteen and east walkway to the Basketball court.
        FillRect(map.Base, t[GroundPlain], 0, LinkWalkY0, 32, LinkWalkY1 - LinkWalkY0 + 1, 0);
        FillRect(map.Base, t[GroundPlain], 100, 60, 12, 12, 0);
        // Backyard corridor east of the admin strip.
        FillRect(map.Base, t[GroundPlain], 76, 68, 12, 28, 0);

        // Perimeter fence along the north edge with a backyard gap.
        WallRunX(map, t, "stone", false, "back", 2, 92, 108, 0);
        CutOpening(map, 76, 92, 12, 1, 0);  // North gap -> Backyard.

        // West classroom wing: raised (z=1) stone shell, north of the Canteen walkway.
        PaintRaisedPlatform(map, t, 12, 58, 19, 29, 1);
        BuildingShell(map, t, "stone", true, 12, 58, 19, 29, 1);
        FillRect(map.Shadows, t["dark_shadow"], 13, 55, 17, 2, 0);
        CutOpening(map, 18, 58, 4, 1, 1);   // Door south toward the walkway.
        ClearCliffsRect(map, 18, 57, 4, 1, 0);
        Place(map.Ramps, t["ramp_se"], 18, 57, 0);
        Place(map.Ramps, t["ramp_se"], 20, 57, 0);
        DeskPair(map, t, 14, 62, 1);
        DeskPair(map, t, 14, 68, 1);
        DeskPair(map, t, 14, 74, 1);
        DeskPair(map, t, 14, 80, 1);
        DeskPair(map, t, 22, 62, 1);
        DeskPair(map, t, 22, 68, 1);
        DeskPair(map, t, 22, 74, 1);
        LineH(map, t, "kitchen_shelf", 24, 84, 4, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 16, 70, 1, true);
        ScatterPassables(map, t, "blood_stain", 13, 59, 17, 27, 1, 0.05f, 6604);

        // East lab wing: raised (z=1) stone shell with brick-patched east face.
        PaintRaisedPlatform(map, t, 80, 28, 19, 39, 1);
        BuildingShell(map, t, "stone", true, 80, 28, 19, 39, 1);
        FillRect(map.Shadows, t["dark_shadow"], 81, 25, 17, 2, 0);
        CutOpening(map, 80, 44, 1, 6, 1);   // West door onto the courtyard.
        CutOpening(map, 98, 62, 1, 4, 1);   // East door toward the basketball court.
        ClearCliffsRect(map, 79, 44, 1, 6, 0);
        ClearCliffsRect(map, 99, 62, 1, 4, 0);
        Place(map.Ramps, t["ramp_sw"], 79, 45, 0);
        Place(map.Ramps, t["ramp_sw"], 79, 46, 0);
        Place(map.Ramps, t["ramp_sw"], 79, 47, 0);
        Place(map.Ramps, t["ramp_ne"], 99, 63, 0);
        Place(map.Ramps, t["ramp_ne"], 99, 64, 0);
        LineH(map, t, "kitchen_appliance", 84, 34, 9, 1, true);
        LineH(map, t, "kitchen_appliance", 84, 40, 9, 1, true);
        LineH(map, t, "kitchen_appliance", 84, 50, 9, 1, true);
        LineH(map, t, "kitchen_appliance", 84, 56, 9, 1, true);
        PlaceObstacle(map, t, "crate_stack", 95, 30, 1, true);
        PlaceObstacle(map, t, "barrel_red", 82, 60, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 90, 44, 1, true);
        ScatterPassables(map, t, "blood_stain", 81, 29, 17, 37, 1, 0.05f, 6605);
        ScatterPassables(map, t, "trash_decal", 81, 29, 17, 37, 1, 0.06f, 6606);

        // North admin strip: raised (z=1), long and wide.
        PaintRaisedPlatform(map, t, 36, 72, 39, 17, 1);
        BuildingShell(map, t, "stone", true, 36, 72, 39, 17, 1);
        CutOpening(map, 52, 72, 4, 1, 1);   // Door south onto the courtyard.
        ClearCliffsRect(map, 52, 71, 4, 1, 0);
        Place(map.Ramps, t["ramp_se"], 53, 71, 0);
        Place(map.Ramps, t["ramp_se"], 54, 71, 0);
        DeskPair(map, t, 40, 76, 1);
        DeskPair(map, t, 40, 82, 1);
        DeskPair(map, t, 46, 84, 1);
        PlaceObstacle(map, t, "kitchen_shelf", 70, 80, 1, true);
        PlaceObstacle(map, t, "crate_stack", 72, 74, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 48, 74, 1, true);

        // Rooftop deck on the admin strip: the highest point of Zone 1 (z=2),
        // with the remains of a survivor camp.
        PaintRaisedPlatform(map, t, 56, 76, 13, 9, 2);
        ClearCliffsRect(map, 55, 79, 1, 2, 1);
        Place(map.Ramps, t["ramp_ne"], 55, 79, 1);
        Place(map.Ramps, t["ramp_ne"], 55, 80, 1);
        WallRunX(map, t, "stone", false, "back", 56, 84, 13, 2);
        Place(map.Shadows, t["dark_shadow"], 60, 79, 2);
        PlaceObstacle(map, t, "barrel_red", 58, 78, 2, true);
        PlaceObstacle(map, t, "crate_stack", 64, 80, 2, true);
        PlaceObstacle(map, t, "crate_stack", 65, 80, 2, true);
        ScatterPassables(map, t, "trash_decal", 57, 77, 11, 7, 2, 0.18f, 6607);
        Place(map.Passables, t["blood_stain"], 61, 79, 2);

        // South library: ground-level stone shell between street and courtyard,
        // one door barricaded from the outside.
        FillRect(map.Base, t[GroundPlain], 36, 16, 39, 11, 0);
        BuildingShell(map, t, "stone", true, 36, 16, 39, 11, 0);
        FillRect(map.Shadows, t["dark_shadow"], 37, 14, 37, 2, 0);
        CutOpening(map, 44, 16, 3, 1, 0);   // Door from the street side.
        CutOpening(map, 64, 26, 3, 1, 0);   // Door onto the courtyard.
        Barricade(map, t, 58, 14, 6, 2, 6608);   // Boarded-up second entrance.
        LineH(map, t, "kitchen_shelf", 40, 19, 8, 0, true);
        LineH(map, t, "kitchen_shelf", 52, 19, 8, 0, true);
        LineH(map, t, "kitchen_shelf", 40, 22, 8, 0, true);
        LineH(map, t, "kitchen_shelf", 52, 22, 8, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 62, 20, 0, true);
        BloodTrail(map, t, 45, 17, 65, 25, 6609);

        // Courtyard centrepiece: dry fountain full of dried blood.
        WallRunX(map, t, "stone", false, "front", 52, 44, 11, 0);
        WallRunX(map, t, "stone", false, "back", 52, 52, 11, 0);
        WallRunY(map, t, "stone", false, "left", 52, 45, 7, 0);
        WallRunY(map, t, "stone", false, "right", 62, 45, 7, 0);
        CutOpening(map, 56, 44, 3, 1, 0);
        FillRect(map.Shadows, t["dark_shadow"], 54, 46, 7, 5, 0);
        ScatterPassables(map, t, "blood_stain", 53, 45, 9, 7, 0, 0.35f, 6610);

        // Abandoned evacuation point in the courtyard south.
        LineH(map, t, "queue_posts", 38, 32, 10, 0, true);
        LineH(map, t, "queue_posts", 38, 34, 10, 0, true);
        PlaceObstacle(map, t, "crate_stack", 50, 33, 0, true);
        PlaceObstacle(map, t, "barrel_red", 49, 32, 0, true);
        Place(map.Shadows, t["dark_shadow"], 43, 33, 0);
        Place(map.Shadows, t["dark_shadow"], 45, 33, 0);
        ScatterPassables(map, t, "blood_stain", 38, 30, 14, 7, 0, 0.18f, 6611);
        ScatterPassables(map, t, "trash_decal", 36, 28, 18, 10, 0, 0.15f, 6612);

        // Courtyard planters and clutter.
        PlaceObstacle(map, t, "planter", 36, 36, 0, true);
        PlaceObstacle(map, t, "planter", 70, 36, 0, true);
        PlaceObstacle(map, t, "planter", 36, 60, 0, true);
        PlaceObstacle(map, t, "planter", 70, 60, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 46, 56, 0, true);
        PlaceObstacle(map, t, "barrel_red", 66, 48, 0, true);
        PlaceObstacle(map, t, "crate_stack", 68, 56, 0, true);

        // Failed roadblock on the street west of the library door.
        Barricade(map, t, 36, StreetY0, 3, 5, 6613);
        ClearGameplayRect(map, 36, StreetY1 - 1, 3, 1, 0);
        FillRect(map.Shadows, t["dark_shadow"], 34, StreetY0 + 1, 2, 2, 0);
        BloodTrail(map, t, 33, StreetY0 + 2, 45, StreetY1 + 2, 6614);
        // Blood trail from the garden road up to the library.
        BloodTrail(map, t, 54, 1, 54, StreetY1 + 1, 6615);

        // Street wreckage.
        PlaceObstacle(map, t, "crate_stack", 10, 15, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 11, 15, 0, true);
        PlaceObstacle(map, t, "barrel_red", 84, 14, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 85, 15, 0, true);

        // Overgrowth and decay: denser than the Canteen for terrain differentiation.
        ScatterBlobs(map.Passables, t["bush"], 1, 16, 110, 74, 0, 16, 4, 0.55f, 6616);
        ScatterPassables(map, t, "bush", 1, 16, 110, 74, 0, 0.020f, 6617);
        ScatterPassables(map, t, "blood_stain", 28, 28, 50, 40, 0, 0.020f, 6618);
        ScatterPassables(map, t, "trash_decal", 4, 14, 104, 76, 0, 0.035f, 6619);
        int[] treeXs = { 6, 4, 8, 33, 94, 50, 104, 106, 102, 6, 32 };
        int[] treeYs = { 20, 56, 90, 88, 90, 90, 30, 44, 16, 34, 20 };
        for (int i = 0; i < treeXs.Length; i++)
        {
            PlaceObstacle(map, t, "small_tree", treeXs[i], treeYs[i], 0, true);
        }

        // Guaranteed walking lanes: all four exits plus courtyard circulation.
        ClearGameplayRect(map, 0, LinkWalkY0, 32, LinkWalkY1 - LinkWalkY0 + 1, 0);   // West -> Canteen.
        ClearGameplayRect(map, 100, 60, 12, 12, 0);                    // East -> Basketball court.
        ClearGameplayRect(map, 76, 68, 12, 28, 0);                     // North -> Backyard.
        ClearGameplayRect(map, 52, 0, 5, 15, 0);                       // South -> Garden.
        ClearGameplayRect(map, 28, 28, 4, 40, 0);                      // Courtyard west lane.
        ClearGameplayRect(map, 74, 28, 4, 40, 0);                      // Courtyard east lane.
        ClearGameplayRect(map, 30, 28, 46, 4, 0);                      // Courtyard south lane.
        ClearGameplayRect(map, 30, 64, 46, 4, 0);                      // Courtyard north lane.
        ClearGameplayRect(map, 18, 55, 4, 3, 0);                       // West wing door approach.
        ClearGameplayRect(map, 74, 44, 6, 6, 0);                       // Lab west door approach.
        ClearGameplayRect(map, 52, 68, 4, 4, 0);                       // Admin door approach.
        ClearGameplayRect(map, 44, 14, 3, 3, 0);                       // Library street door approach.

        CompressAll(map);
    }

    // ---------------------------------------------------------------- shared builders

    private static void PaintRaisedPlatform(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int width, int height, int z)
    {
        FillRect(map.Base, t[GroundPlain], x, y, width, height, z);

        for (int ix = x; ix < x + width; ix++)
        {
            Place(map.Cliffs, t["cliff_front"], ix, y - 1, z - 1);
            Place(map.Shadows, t["drop_shadow"], ix, y - 2, z - 1);
        }
        for (int iy = y; iy < y + height; iy++)
        {
            Place(map.Cliffs, t["cliff_left"], x - 1, iy, z - 1);
            Place(map.Cliffs, t["cliff_right"], x + width, iy, z - 1);
        }
    }

    private static void TableCluster(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int z)
    {
        PlaceObstacle(map, t, "dining_table", x, y, z, true);
        PlaceObstacle(map, t, "chair_stack", x + 1, y, z, true);
        PlaceObstacle(map, t, "dining_table", x, y + 1, z, true);
        PlaceObstacle(map, t, "chair_stack", x + 1, y + 1, z, true);
    }

    private static void DeskPair(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int z)
    {
        PlaceObstacle(map, t, "dining_table", x, y, z, true);
        PlaceObstacle(map, t, "chair_stack", x + 1, y, z, true);
    }

    /// <summary>Patchy blob scatter — reads as nature reclaiming ground, unlike uniform noise.</summary>
    private static void ScatterBlobs(Tilemap tilemap, TileBase tile, int x, int y, int width, int height, int z, int blobs, int radius, float chance, int seed)
    {
        Random.InitState(seed);
        for (int b = 0; b < blobs; b++)
        {
            int cx = Random.Range(x, x + width);
            int cy = Random.Range(y, y + height);
            for (int ix = cx - radius; ix <= cx + radius; ix++)
            {
                for (int iy = cy - radius; iy <= cy + radius; iy++)
                {
                    if (ix < x || iy < y || ix >= x + width || iy >= y + height)
                    {
                        continue;
                    }
                    int dx = ix - cx;
                    int dy = iy - cy;
                    if (dx * dx + dy * dy > radius * radius)
                    {
                        continue;
                    }
                    if (Random.value <= chance)
                    {
                        tilemap.SetTile(new Vector3Int(ix, iy, z), tile);
                    }
                }
            }
        }
    }

    /// <summary>Jittered line of blood decals — someone was dragged or ran while bleeding.</summary>
    private static void BloodTrail(ZoneMaps map, Dictionary<string, TileBase> t, int x0, int y0, int x1, int y1, int seed)
    {
        Random.InitState(seed);
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            float u = steps == 0 ? 0f : (float)i / steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, u)) + Random.Range(-1, 2);
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, u)) + Random.Range(-1, 2);
            x = Mathf.Clamp(x, 0, map.Width - 1);
            y = Mathf.Clamp(y, 0, map.Height - 1);
            if (Random.value < 0.75f)
            {
                Place(map.Passables, t["blood_stain"], x, y, 0);
            }
        }
    }

    /// <summary>Dense mixed pile of crates/barrels/rubble with colliders — a failed barricade.</summary>
    private static void Barricade(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int width, int height, int seed)
    {
        string[] pool = { "crate_stack", "barrel_red", "rubble_pile" };
        Random.InitState(seed);
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                if (Random.value < 0.75f)
                {
                    PlaceObstacle(map, t, pool[Random.Range(0, pool.Length)], ix, iy, 0, true);
                }
            }
        }
    }

    private static void LineH(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z, bool collides)
    {
        if (!t.ContainsKey(tileName))
        {
            return;
        }
        for (int i = 0; i < length; i++)
        {
            PlaceObstacle(map, t, tileName, x + i, y, z, collides);
        }
    }

    private static void LineV(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z, bool collides)
    {
        if (!t.ContainsKey(tileName))
        {
            return;
        }
        for (int i = 0; i < length; i++)
        {
            PlaceObstacle(map, t, tileName, x, y + i, z, collides);
        }
    }

    private static void PlaceObstacle(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int z, bool collides)
    {
        Place(map.Obstacles, t[tileName], x, y, z);
        if (collides)
        {
            Place(map.Colliders, t["collider_debug"], x, y, z);
        }
    }

    private static void ScatterPassables(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int width, int height, int z, float chance, int seed)
    {
        Random.InitState(seed);
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                if (Random.value <= chance)
                {
                    Place(map.Passables, t[tileName], ix, iy, z);
                }
            }
        }
    }

    /// <summary>Random ground-variation scatter on the Base layer.</summary>
    private static void ScatterBase(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int width, int height, int z, float chance, int seed)
    {
        Random.InitState(seed);
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                if (Random.value <= chance)
                {
                    Place(map.Base, t[tileName], ix, iy, z);
                }
            }
        }
    }

    private static void FillRect(Tilemap tilemap, TileBase tile, int x, int y, int width, int height, int z)
    {
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                tilemap.SetTile(new Vector3Int(ix, iy, z), tile);
            }
        }
    }

    /// <summary>Removes cliff tiles where a ramp or doorway punches through a platform edge.</summary>
    private static void ClearCliffsRect(ZoneMaps map, int x, int y, int width, int height, int z)
    {
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                map.Cliffs.SetTile(new Vector3Int(ix, iy, z), null);
            }
        }
    }

    private static void ClearGameplayRect(ZoneMaps map, int x, int y, int width, int height, int z)
    {
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                map.Obstacles.SetTile(new Vector3Int(ix, iy, z), null);
                map.Colliders.SetTile(new Vector3Int(ix, iy, z), null);
                map.Obstacles.SetTile(new Vector3Int(ix, iy, z + 1), null);
                map.Colliders.SetTile(new Vector3Int(ix, iy, z + 1), null);
            }
        }
    }

    private static void Place(Tilemap tilemap, TileBase tile, int x, int y, int z)
    {
        if (tile == null || tilemap == null)
        {
            return;
        }
        tilemap.SetTile(new Vector3Int(x, y, z), tile);
    }

    private static void LogExits()
    {
        Debug.Log("Zone1_Canteen exits: N->Backyard x76..87 @ y" + (CanteenHeight - 1) + " | S->BlockC road x28..32 @ y0 | E->BlockE walkway y" + LinkWalkY0 + ".." + LinkWalkY1 + " and street y" + StreetY0 + ".." + StreetY1 + " @ x" + (CanteenWidth - 1) + ".");
        Debug.Log("Zone1_BlockE exits: W->Canteen walkway y" + LinkWalkY0 + ".." + LinkWalkY1 + " and street y" + StreetY0 + ".." + StreetY1 + " @ x0 | N->Backyard x76..87 @ y" + (BlockEHeight - 1) + " | S->Garden road x52..56 @ y0 | E->Basketball court y60..71 @ x" + (BlockEWidth - 1) + ".");
    }

    // ---------------------------------------------------------------- validation / bounds

    private static void EnforceZoneBounds(ZoneMaps map, string zoneName)
    {
        int removed = 0;
        removed += RemoveOutside(map.Base, map.Width, map.Height);
        removed += RemoveOutside(map.Shadows, map.Width, map.Height);
        removed += RemoveOutside(map.Cliffs, map.Width, map.Height);
        removed += RemoveOutside(map.Ramps, map.Width, map.Height);
        removed += RemoveOutside(map.Walls, map.Width, map.Height);
        removed += RemoveOutside(map.Obstacles, map.Width, map.Height);
        removed += RemoveOutside(map.Passables, map.Width, map.Height);
        removed += RemoveOutside(map.Colliders, map.Width, map.Height);

        CompressAll(map);

        if (removed > 0)
        {
            Debug.LogWarning("JayLargeCampusTilemapBuilder: Removed " + removed + " out-of-bounds tiles from " + zoneName + ". Final bounded size is " + map.Width + "x" + map.Height + ".");
        }
        else
        {
            Debug.Log("JayLargeCampusTilemapBuilder: " + zoneName + " fits within " + map.Width + "x" + map.Height + " cells.");
        }
    }

    private static int RemoveOutside(Tilemap tilemap, int width, int height)
    {
        if (tilemap == null)
        {
            return 0;
        }

        int removed = 0;
        BoundsInt bounds = tilemap.cellBounds;
        foreach (Vector3Int pos in bounds.allPositionsWithin)
        {
            if (!tilemap.HasTile(pos))
            {
                continue;
            }
            if (pos.x < 0 || pos.y < 0 || pos.x >= width || pos.y >= height)
            {
                tilemap.SetTile(pos, null);
                removed++;
            }
        }
        tilemap.CompressBounds();
        return removed;
    }

    private static bool ValidatePrefabBounds(string prefabPath, string zoneName, int width, int height)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogError("Missing prefab: " + prefabPath);
            return false;
        }

        bool ok = true;
        Tilemap[] maps = prefab.GetComponentsInChildren<Tilemap>(true);
        for (int i = 0; i < maps.Length; i++)
        {
            BoundsInt bounds = maps[i].cellBounds;
            foreach (Vector3Int pos in bounds.allPositionsWithin)
            {
                if (!maps[i].HasTile(pos))
                {
                    continue;
                }
                if (pos.x < 0 || pos.y < 0 || pos.x >= width || pos.y >= height)
                {
                    ok = false;
                    Debug.LogError(zoneName + " has out-of-bounds tile in layer " + maps[i].name + " at " + pos + ". Expected x=0.." + (width - 1) + ", y=0.." + (height - 1) + ".");
                }
            }
        }

        if (ok)
        {
            Debug.Log(zoneName + " validated within " + width + "x" + height + " cells.");
        }
        return ok;
    }

    private static void CompressAll(ZoneMaps map)
    {
        map.Base.CompressBounds();
        map.Shadows.CompressBounds();
        map.Cliffs.CompressBounds();
        map.Ramps.CompressBounds();
        map.Walls.CompressBounds();
        map.Obstacles.CompressBounds();
        map.Passables.CompressBounds();
        map.Colliders.CompressBounds();
    }

    private static void AddAssetIfExists(List<UnityEngine.Object> selected, string path)
    {
        UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        if (asset != null)
        {
            selected.Add(asset);
        }
    }

    private static void AddFolderAssets(List<UnityEngine.Object> selected, string folder)
    {
        if (!AssetDatabase.IsValidFolder(folder))
        {
            return;
        }
        string[] guids = AssetDatabase.FindAssets("", new[] { folder });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null)
            {
                selected.Add(asset);
            }
        }
    }
}
#endif
