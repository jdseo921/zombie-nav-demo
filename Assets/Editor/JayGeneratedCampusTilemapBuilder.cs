#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// CP5030 bounded generator for Jay's Ground Level 1: Block E and Canteen.
///
/// Purpose:
/// - Uses the generated explicit sprite set in Assets/Tiles/Sprites/Jay/GeneratedCanteenBlockE/.
/// - Creates Tile assets automatically.
/// - Rebuilds Zone1_BlockE and Zone1_Canteen as separate prefabs.
/// - Enforces fixed design bounds so the generated areas stay inside the assigned Zone 1 area sizes.
///
/// Size policy used by this script:
/// - The task handout gives a relative Zone 1 coordinate diagram, not exact tile-cell dimensions.
/// - The diagram shows Canteen and Block E with similar height, while Canteen is roughly 1.5x wider.
/// - This script therefore uses:
///     Canteen: 42 x 24 cells
///     Block E: 28 x 24 cells
/// - Any tile accidentally placed outside those bounds is removed and reported in the Console.
///
/// Install:
/// - Put this file in Assets/Editor/JayGeneratedCampusTilemapBuilder.cs.
/// - Put sprite PNGs in Assets/Tiles/Sprites/Jay/GeneratedCanteenBlockE/.
/// - Run Tools > CP5030 > Jay Areas > Setup Generated Sprites + Rebuild Bounded BlockE + Canteen.
/// </summary>
public static class JayGeneratedCampusTilemapBuilder
{
    private const string SpriteFolder = "Assets/Tiles/Sprites/Jay/GeneratedCanteenBlockE";
    private const string TileFolder = "Assets/Tiles/Tile Assets/Jay/GeneratedCanteenBlockE";
    private const string AreaFolder = "Assets/Levels/Level 1/Areas/Jay";
    private const string LevelRootName = "Levels";
    private const string ZoneParentName = "Level 1 - Zone 1";

    // Bounded size derived from the task.pdf Zone 1 diagram ratio.
    private const int CanteenWidth = 42;
    private const int CanteenHeight = 24;
    private const int BlockEWidth = 28;
    private const int BlockEHeight = 24;

    private static readonly string[] RequiredSpriteNames =
    {
        "grass_flat", "grass_overgrown", "dirt_path", "canteen_floor", "cracked_floor", "dirty_floor",
        "concrete_pavement", "raised_floor_top", "dark_shadow", "drop_shadow",
        "cliff_front", "cliff_left", "cliff_right",
        "ramp_ne", "ramp_nw", "ramp_se", "ramp_sw",
        "broken_wall_h", "broken_wall_v", "counter_h", "counter_v", "counter_corner",
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
        public Tilemap Obstacles;
        public Tilemap Passables;
        public Tilemap Colliders;
    }

    public static void SetupSpritesAndRebuildBoth()
    {
        EnsureFolders();
        ImportGeneratedSprites();
        CreateTileAssetsFromSprites();
        RebuildBothZones();
    }

    public static void RebuildBothZones()
    {
        EnsureFolders();

        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayGeneratedCampusTilemapBuilder: Missing generated tiles. Run setup first.");
            return;
        }

        GameObject zoneParent = GetOrCreateZoneParent();

        ResetZone(zoneParent, "Zone1_BlockE");
        ResetZone(zoneParent, "Zone1_Canteen");

        ZoneMaps blockE = CreateZoneMaps(zoneParent, "Zone1_BlockE", BlockEWidth, BlockEHeight);
        BuildBlockE(blockE, tiles);
        EnforceZoneBounds(blockE, "Zone1_BlockE");
        SaveZonePrefab(blockE.Root, "Zone1_BlockE");

        ZoneMaps canteen = CreateZoneMaps(zoneParent, "Zone1_Canteen", CanteenWidth, CanteenHeight);
        BuildCanteen(canteen, tiles);
        EnforceZoneBounds(canteen, "Zone1_Canteen");
        SaveZonePrefab(canteen.Root, "Zone1_Canteen");

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("JayGeneratedCampusTilemapBuilder: Rebuilt bounded Zone1_BlockE (" + BlockEWidth + "x" + BlockEHeight + ") and Zone1_Canteen (" + CanteenWidth + "x" + CanteenHeight + ").");
    }

    public static void RebuildCanteenOnly()
    {
        EnsureFolders();
        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayGeneratedCampusTilemapBuilder: Missing generated tiles. Run setup first.");
            return;
        }

        GameObject zoneParent = GetOrCreateZoneParent();
        ResetZone(zoneParent, "Zone1_Canteen");
        ZoneMaps canteen = CreateZoneMaps(zoneParent, "Zone1_Canteen", CanteenWidth, CanteenHeight);
        BuildCanteen(canteen, tiles);
        EnforceZoneBounds(canteen, "Zone1_Canteen");
        SaveZonePrefab(canteen.Root, "Zone1_Canteen");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JayGeneratedCampusTilemapBuilder: Rebuilt bounded Zone1_Canteen only.");
    }

    public static void RebuildBlockEOnly()
    {
        EnsureFolders();
        Dictionary<string, TileBase> tiles = LoadTiles();
        if (!ValidateTiles(tiles))
        {
            Debug.LogError("JayGeneratedCampusTilemapBuilder: Missing generated tiles. Run setup first.");
            return;
        }

        GameObject zoneParent = GetOrCreateZoneParent();
        ResetZone(zoneParent, "Zone1_BlockE");
        ZoneMaps blockE = CreateZoneMaps(zoneParent, "Zone1_BlockE", BlockEWidth, BlockEHeight);
        BuildBlockE(blockE, tiles);
        EnforceZoneBounds(blockE, "Zone1_BlockE");
        SaveZonePrefab(blockE.Root, "Zone1_BlockE");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JayGeneratedCampusTilemapBuilder: Rebuilt bounded Zone1_BlockE only.");
    }

    public static void ValidateGeneratedZones()
    {
        bool ok = true;
        ok &= ValidatePrefabBounds(AreaFolder + "/Zone1_BlockE.prefab", "Zone1_BlockE", BlockEWidth, BlockEHeight);
        ok &= ValidatePrefabBounds(AreaFolder + "/Zone1_Canteen.prefab", "Zone1_Canteen", CanteenWidth, CanteenHeight);
        ok &= ValidateTiles(LoadTiles());

        if (ok)
        {
            Debug.Log("JayGeneratedCampusTilemapBuilder: Validation passed. Block E and Canteen prefabs exist and tilemaps fit the bounded sizes.");
        }
    }

    public static void SelectExportDependencies()
    {
        List<UnityEngine.Object> selected = new List<UnityEngine.Object>();
        AddAssetIfExists(selected, AreaFolder + "/Zone1_BlockE.prefab");
        AddAssetIfExists(selected, AreaFolder + "/Zone1_Canteen.prefab");
        AddFolderAssets(selected, SpriteFolder);
        AddFolderAssets(selected, TileFolder);
        Selection.objects = selected.ToArray();
        Debug.Log("Selected Jay generated bounded assets for export. Use Assets > Export Package with Include Dependencies enabled.");
    }

    private static void EnsureFolders()
    {
        EnsureFolder("Assets", "Tiles");
        EnsureFolder("Assets/Tiles", "Sprites");
        EnsureFolder("Assets/Tiles/Sprites", "Jay");
        EnsureFolder("Assets/Tiles/Sprites/Jay", "GeneratedCanteenBlockE");
        EnsureFolder("Assets/Tiles", "Tile Assets");
        EnsureFolder("Assets/Tiles/Tile Assets", "Jay");
        EnsureFolder("Assets/Tiles/Tile Assets/Jay", "GeneratedCanteenBlockE");
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

    private static void ImportGeneratedSprites()
    {
        for (int i = 0; i < RequiredSpriteNames.Length; i++)
        {
            string path = SpriteFolder + "/" + RequiredSpriteNames[i] + ".png";
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning("Missing generated sprite PNG: " + path);
                continue;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 32f;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }
    }

    private static void CreateTileAssetsFromSprites()
    {
        for (int i = 0; i < RequiredSpriteNames.Length; i++)
        {
            string spriteName = RequiredSpriteNames[i];
            string spritePath = SpriteFolder + "/" + spriteName + ".png";
            string tilePath = TileFolder + "/" + spriteName + ".asset";

            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            if (sprite == null)
            {
                continue;
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

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static Dictionary<string, TileBase> LoadTiles()
    {
        Dictionary<string, TileBase> tiles = new Dictionary<string, TileBase>();
        for (int i = 0; i < RequiredSpriteNames.Length; i++)
        {
            string name = RequiredSpriteNames[i];
            TileBase tile = AssetDatabase.LoadAssetAtPath<TileBase>(TileFolder + "/" + name + ".asset");
            if (tile != null)
            {
                tiles[name] = tile;
            }
        }
        return tiles;
    }

    private static bool ValidateTiles(Dictionary<string, TileBase> tiles)
    {
        bool ok = true;
        for (int i = 0; i < RequiredSpriteNames.Length; i++)
        {
            if (!tiles.ContainsKey(RequiredSpriteNames[i]))
            {
                ok = false;
                Debug.LogWarning("Missing Tile asset for role: " + RequiredSpriteNames[i]);
            }
        }
        return ok;
    }

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

    private static ZoneMaps CreateZoneMaps(GameObject zoneParent, string zoneName, int width, int height)
    {
        GameObject root = new GameObject(zoneName);
        root.transform.SetParent(zoneParent.transform);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        ZoneMaps maps = new ZoneMaps();
        maps.Root = root;
        maps.Width = width;
        maps.Height = height;
        maps.Base = CreateLayer(root, "Base", 0, true, false);
        maps.Shadows = CreateLayer(root, "Shadows", 1, true, false);
        maps.Cliffs = CreateLayer(root, "Cliffs", 2, true, false);
        maps.Ramps = CreateLayer(root, "Ramps", 3, true, false);
        maps.Obstacles = CreateLayer(root, "Obstacles", 4, true, false);
        maps.Passables = CreateLayer(root, "Passables", 5, true, false);
        maps.Colliders = CreateLayer(root, "Colliders", 6, false, true);
        return maps;
    }

    private static Tilemap CreateLayer(GameObject root, string layerName, int sortingOrder, bool visible, bool collider)
    {
        GameObject layerObject = new GameObject(layerName);
        layerObject.transform.SetParent(root.transform);
        layerObject.transform.localPosition = Vector3.zero;
        layerObject.transform.localRotation = Quaternion.identity;
        layerObject.transform.localScale = Vector3.one;

        Tilemap tilemap = layerObject.AddComponent<Tilemap>();
        tilemap.tileAnchor = new Vector3(1f, 1f, 0f);

        TilemapRenderer renderer = layerObject.AddComponent<TilemapRenderer>();
        renderer.sortingOrder = sortingOrder;
        renderer.enabled = visible;

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

    private static void BuildCanteen(ZoneMaps map, Dictionary<string, TileBase> t)
    {
        FillRect(map.Base, t["grass_flat"], 0, 0, CanteenWidth, CanteenHeight, 0);
        Scatter(map.Base, t["grass_overgrown"], 0, 0, CanteenWidth, CanteenHeight, 0, 0.06f, 4401);
        FillRect(map.Base, t["dirt_path"], 1, 1, CanteenWidth - 2, CanteenHeight - 2, 0);
        FillRect(map.Base, t["canteen_floor"], 4, 3, 34, 18, 0);
        Scatter(map.Base, t["cracked_floor"], 4, 3, 34, 18, 0, 0.18f, 4402);
        Scatter(map.Base, t["dirty_floor"], 4, 3, 34, 18, 0, 0.12f, 4403);

        // Purposeful canteen elevation: kitchen, serving islands, collapsed side rooms.
        PaintRaisedPlatform(map, t, 6, 16, 12, 6, 1);
        PaintRaisedPlatform(map, t, 27, 14, 9, 6, 1);
        PaintRaisedPlatform(map, t, 16, 10, 10, 5, 1);
        PaintRaisedPlatform(map, t, 6, 5, 8, 5, 1);
        PaintRaisedPlatform(map, t, 30, 5, 6, 5, 1);

        Place(map.Ramps, t["ramp_ne"], 11, 15, 0);
        Place(map.Ramps, t["ramp_se"], 26, 11, 0);
        Place(map.Ramps, t["ramp_nw"], 29, 13, 0);
        Place(map.Ramps, t["ramp_sw"], 10, 10, 0);
        Place(map.Ramps, t["ramp_ne"], 31, 10, 0);

        // Broken canteen shell.
        LineH(map, t, "broken_wall_h", 6, 22, 13, 1, true);
        LineH(map, t, "broken_wall_h", 23, 20, 12, 1, true);
        LineV(map, t, "broken_wall_v", 4, 15, 6, 0, true);
        LineV(map, t, "broken_wall_v", 37, 13, 7, 0, true);
        LineH(map, t, "broken_wall_h", 5, 3, 8, 0, true);
        LineV(map, t, "broken_wall_v", 37, 4, 5, 0, true);

        // Kitchen/storage raised zone.
        LineH(map, t, "kitchen_shelf", 7, 21, 6, 1, true);
        LineH(map, t, "kitchen_appliance", 8, 19, 4, 1, true);
        LineV(map, t, "crate_stack", 16, 17, 4, 1, true);
        PlaceObstacle(map, t, "barrel_red", 8, 17, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 14, 18, 1, true);

        // L/U-shaped serving counters make controlled maze corners.
        LineH(map, t, "counter_h", 7, 15, 8, 0, true);
        LineV(map, t, "counter_v", 14, 12, 4, 0, true);
        PlaceObstacle(map, t, "counter_corner", 14, 15, 0, true);

        LineH(map, t, "counter_h", 18, 15, 8, 1, true);
        LineV(map, t, "counter_v", 18, 11, 5, 1, true);
        PlaceObstacle(map, t, "counter_corner", 18, 15, 1, true);

        LineV(map, t, "counter_v", 28, 14, 5, 1, true);
        LineH(map, t, "counter_h", 28, 14, 6, 1, true);
        PlaceObstacle(map, t, "counter_corner", 28, 14, 1, true);

        LineH(map, t, "counter_h", 22, 7, 7, 0, true);
        LineV(map, t, "counter_v", 28, 7, 4, 0, true);
        PlaceObstacle(map, t, "counter_corner", 28, 7, 0, true);

        LineH(map, t, "counter_h", 8, 7, 5, 1, true);
        LineV(map, t, "counter_v", 12, 7, 3, 1, true);
        PlaceObstacle(map, t, "counter_corner", 12, 7, 1, true);

        // Queue dividers, planters, and table islands create corner-turning paths.
        LineH(map, t, "queue_posts", 8, 12, 5, 0, true);
        LineH(map, t, "queue_posts", 22, 12, 5, 0, true);
        LineV(map, t, "queue_posts", 20, 6, 4, 0, true);
        PlaceObstacle(map, t, "planter", 16, 8, 0, true);
        PlaceObstacle(map, t, "planter", 21, 9, 0, true);
        PlaceObstacle(map, t, "planter", 25, 18, 1, true);
        PlaceObstacle(map, t, "planter", 11, 6, 1, true);

        TableCluster(map, t, 9, 11, 0);
        TableCluster(map, t, 16, 5, 0);
        TableCluster(map, t, 24, 4, 0);
        TableCluster(map, t, 31, 12, 0);
        TableCluster(map, t, 31, 18, 1);
        TableCluster(map, t, 12, 18, 1);

        PlaceObstacle(map, t, "crate_stack", 5, 11, 0, true);
        PlaceObstacle(map, t, "barrel_red", 6, 12, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 7, 13, 0, true);
        PlaceObstacle(map, t, "crate_stack", 34, 8, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 34, 9, 1, true);
        PlaceObstacle(map, t, "barrel_red", 27, 10, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 20, 19, 0, true);

        ScatterPassables(map, t, "bush", 2, 2, 38, 20, 0, 0.035f, 4404);
        ScatterPassables(map, t, "blood_stain", 5, 4, 32, 17, 0, 0.030f, 4405);
        ScatterPassables(map, t, "trash_decal", 5, 4, 32, 17, 0, 0.050f, 4406);
        Scatter(map.Base, t["grass_overgrown"], 2, 2, 38, 20, 0, 0.035f, 4407);

        PlaceObstacle(map, t, "small_tree", 2, 5, 0, true);
        PlaceObstacle(map, t, "small_tree", 39, 7, 0, true);
        PlaceObstacle(map, t, "small_tree", 3, 21, 0, true);
        PlaceObstacle(map, t, "small_tree", 38, 21, 0, true);

        // Cut deliberate openings after dense object placement. These become readable walking lanes.
        ClearGameplayRect(map, 4, 10, 34, 2, 0);
        ClearGameplayRect(map, 19, 3, 3, 18, 0);
        ClearGameplayRect(map, 26, 4, 3, 16, 0);
        ClearGameplayRect(map, 10, 14, 3, 3, 0);
        ClearGameplayRect(map, 28, 11, 4, 3, 0);
        ClearGameplayRect(map, 9, 8, 3, 3, 0);
        ClearGameplayRect(map, 30, 9, 3, 3, 0);

        CompressAll(map);
    }

    private static void BuildBlockE(ZoneMaps map, Dictionary<string, TileBase> t)
    {
        FillRect(map.Base, t["grass_flat"], 0, 0, BlockEWidth, BlockEHeight, 0);
        Scatter(map.Base, t["grass_overgrown"], 0, 0, BlockEWidth, BlockEHeight, 0, 0.06f, 8801);
        FillRect(map.Base, t["dirt_path"], 1, 1, BlockEWidth - 2, BlockEHeight - 2, 0);
        FillRect(map.Base, t["concrete_pavement"], 3, 3, BlockEWidth - 6, BlockEHeight - 6, 0);
        Scatter(map.Base, t["cracked_floor"], 3, 3, BlockEWidth - 6, BlockEHeight - 6, 0, 0.14f, 8802);
        Scatter(map.Base, t["dirty_floor"], 3, 3, BlockEWidth - 6, BlockEHeight - 6, 0, 0.08f, 8803);

        // Smaller than canteen, but still creative: academic wings around damaged atrium.
        PaintRaisedPlatform(map, t, 3, 10, 8, 10, 1);   // West classrooms.
        PaintRaisedPlatform(map, t, 8, 18, 11, 5, 1);   // North admin/tutorial strip.
        PaintRaisedPlatform(map, t, 18, 10, 7, 9, 1);   // East lab wing.
        PaintRaisedPlatform(map, t, 7, 3, 13, 5, 1);    // South library/tutorial wing.
        PaintRaisedPlatform(map, t, 20, 4, 5, 5, 2);    // Higher server/security annex.

        FillRect(map.Base, t["dirty_floor"], 11, 9, 7, 7, 0);
        Scatter(map.Base, t["cracked_floor"], 11, 9, 7, 7, 0, 0.30f, 8804);
        FillRect(map.Shadows, t["dark_shadow"], 13, 11, 3, 2, 0);
        Place(map.Passables, t["blood_stain"], 13, 10, 0);
        Place(map.Passables, t["trash_decal"], 16, 14, 0);

        // Raised walkways and ramps.
        LineHBase(map, t, "raised_floor_top", 10, 16, 10, 1);
        LineVBase(map, t, "raised_floor_top", 18, 10, 8, 1);
        LineHBase(map, t, "raised_floor_top", 7, 8, 13, 1);
        LineVBase(map, t, "raised_floor_top", 10, 8, 9, 1);
        Place(map.Ramps, t["ramp_ne"], 10, 13, 0);
        Place(map.Ramps, t["ramp_nw"], 14, 16, 0);
        Place(map.Ramps, t["ramp_se"], 18, 13, 0);
        Place(map.Ramps, t["ramp_sw"], 13, 8, 0);
        Place(map.Ramps, t["ramp_ne"], 20, 8, 1);

        // West classrooms.
        LineH(map, t, "broken_wall_h", 3, 20, 8, 1, true);
        LineV(map, t, "broken_wall_v", 3, 11, 9, 1, true);
        LineV(map, t, "broken_wall_v", 10, 11, 8, 1, true);
        LineH(map, t, "broken_wall_h", 4, 15, 4, 1, true);
        LineV(map, t, "broken_wall_v", 7, 12, 3, 1, true);
        DeskPair(map, t, 5, 18, 1);
        DeskPair(map, t, 5, 12, 1);
        PlaceObstacle(map, t, "kitchen_shelf", 9, 13, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 6, 10, 1, true);

        // North admin/tutorial wing.
        LineH(map, t, "broken_wall_h", 8, 23, 11, 1, true);
        LineV(map, t, "broken_wall_v", 8, 19, 4, 1, true);
        LineV(map, t, "broken_wall_v", 18, 19, 4, 1, true);
        LineV(map, t, "broken_wall_v", 13, 19, 3, 1, true);
        LineH(map, t, "counter_h", 9, 21, 4, 1, true);
        LineH(map, t, "kitchen_shelf", 14, 21, 4, 1, true);
        PlaceObstacle(map, t, "crate_stack", 17, 19, 1, true);

        // East lab wing.
        LineH(map, t, "broken_wall_h", 18, 19, 7, 1, true);
        LineV(map, t, "broken_wall_v", 18, 11, 8, 1, true);
        LineV(map, t, "broken_wall_v", 24, 11, 8, 1, true);
        LineH(map, t, "counter_h", 20, 17, 4, 1, true);
        LineH(map, t, "counter_h", 20, 14, 4, 1, true);
        LineV(map, t, "counter_v", 23, 13, 4, 1, true);
        PlaceObstacle(map, t, "kitchen_appliance", 23, 12, 1, true);
        PlaceObstacle(map, t, "rubble_pile", 19, 11, 1, true);

        // South library/tutorial wing.
        LineH(map, t, "broken_wall_h", 7, 8, 13, 1, true);
        LineV(map, t, "broken_wall_v", 7, 4, 4, 1, true);
        LineV(map, t, "broken_wall_v", 19, 4, 4, 1, true);
        LineV(map, t, "broken_wall_v", 13, 4, 4, 1, true);
        LineH(map, t, "kitchen_shelf", 8, 6, 4, 1, true);
        LineH(map, t, "kitchen_shelf", 14, 6, 4, 1, true);
        DeskPair(map, t, 9, 4, 1);
        DeskPair(map, t, 15, 4, 1);

        // Higher server annex.
        LineH(map, t, "broken_wall_h", 20, 9, 5, 2, true);
        LineV(map, t, "broken_wall_v", 20, 5, 4, 2, true);
        LineV(map, t, "broken_wall_v", 24, 5, 4, 2, true);
        LineH(map, t, "kitchen_appliance", 21, 7, 3, 2, true);
        PlaceObstacle(map, t, "barrel_red", 23, 5, 2, true);
        PlaceObstacle(map, t, "crate_stack", 21, 5, 2, true);

        // Central atrium and checkpoint clutter.
        LineH(map, t, "planter", 11, 14, 3, 0, true);
        LineH(map, t, "planter", 16, 11, 3, 0, true);
        LineV(map, t, "queue_posts", 14, 9, 3, 0, true);
        PlaceObstacle(map, t, "rubble_pile", 13, 12, 0, true);
        PlaceObstacle(map, t, "barrel_red", 16, 12, 0, true);
        PlaceObstacle(map, t, "crate_stack", 15, 14, 0, true);
        LineH(map, t, "queue_posts", 4, 9, 4, 0, true);
        LineH(map, t, "queue_posts", 21, 9, 4, 0, true);

        ScatterPassables(map, t, "bush", 1, 1, BlockEWidth - 2, BlockEHeight - 2, 0, 0.03f, 8810);
        ScatterPassables(map, t, "blood_stain", 3, 3, BlockEWidth - 6, BlockEHeight - 6, 0, 0.025f, 8811);
        ScatterPassables(map, t, "trash_decal", 3, 3, BlockEWidth - 6, BlockEHeight - 6, 0, 0.045f, 8812);
        PlaceObstacle(map, t, "small_tree", 2, 5, 0, true);
        PlaceObstacle(map, t, "small_tree", 25, 6, 0, true);
        PlaceObstacle(map, t, "small_tree", 2, 21, 0, true);
        PlaceObstacle(map, t, "small_tree", 25, 21, 0, true);

        ClearGameplayRect(map, 10, 8, 9, 2, 0);
        ClearGameplayRect(map, 10, 15, 10, 2, 0);
        ClearGameplayRect(map, 10, 9, 3, 8, 0);
        ClearGameplayRect(map, 17, 9, 3, 8, 0);
        ClearGameplayRect(map, 12, 9, 6, 6, 0);
        ClearGameplayRect(map, 19, 7, 4, 3, 1);
        ClearGameplayRect(map, 4, 9, 6, 2, 0);
        ClearGameplayRect(map, 20, 9, 5, 2, 0);

        CompressAll(map);
    }

    private static void PaintRaisedPlatform(ZoneMaps map, Dictionary<string, TileBase> t, int x, int y, int width, int height, int z)
    {
        FillRect(map.Base, t["raised_floor_top"], x, y, width, height, z);
        Scatter(map.Base, t["cracked_floor"], x, y, width, height, z, 0.12f, x * 97 + y * 193 + width);

        for (int ix = x; ix < x + width; ix++)
        {
            Place(map.Cliffs, t["cliff_front"], ix, y - 1, z - 1);
            Place(map.Shadows, t["drop_shadow"], ix, y - 2, z - 1);
        }
        for (int iy = y; iy < y + height; iy++)
        {
            Place(map.Cliffs, t["cliff_left"], x - 1, iy, z - 1);
            Place(map.Shadows, t["drop_shadow"], x - 2, iy, z - 1);
            Place(map.Cliffs, t["cliff_right"], x + width, iy, z - 1);
            Place(map.Shadows, t["drop_shadow"], x + width + 1, iy, z - 1);
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

    private static void LineH(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z, bool collides)
    {
        for (int i = 0; i < length; i++)
        {
            PlaceObstacle(map, t, tileName, x + i, y, z, collides);
        }
    }

    private static void LineV(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z, bool collides)
    {
        for (int i = 0; i < length; i++)
        {
            PlaceObstacle(map, t, tileName, x, y + i, z, collides);
        }
    }

    private static void LineHBase(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z)
    {
        for (int i = 0; i < length; i++)
        {
            Place(map.Base, t[tileName], x + i, y, z);
        }
    }

    private static void LineVBase(ZoneMaps map, Dictionary<string, TileBase> t, string tileName, int x, int y, int length, int z)
    {
        for (int i = 0; i < length; i++)
        {
            Place(map.Base, t[tileName], x, y + i, z);
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

    private static void Scatter(Tilemap tilemap, TileBase tile, int x, int y, int width, int height, int z, float chance, int seed)
    {
        Random.InitState(seed);
        for (int ix = x; ix < x + width; ix++)
        {
            for (int iy = y; iy < y + height; iy++)
            {
                if (Random.value <= chance)
                {
                    tilemap.SetTile(new Vector3Int(ix, iy, z), tile);
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

    private static void EnforceZoneBounds(ZoneMaps map, string zoneName)
    {
        int removed = 0;
        removed += RemoveOutside(map.Base, map.Width, map.Height);
        removed += RemoveOutside(map.Shadows, map.Width, map.Height);
        removed += RemoveOutside(map.Cliffs, map.Width, map.Height);
        removed += RemoveOutside(map.Ramps, map.Width, map.Height);
        removed += RemoveOutside(map.Obstacles, map.Width, map.Height);
        removed += RemoveOutside(map.Passables, map.Width, map.Height);
        removed += RemoveOutside(map.Colliders, map.Width, map.Height);

        CompressAll(map);

        if (removed > 0)
        {
            Debug.LogWarning("JayGeneratedCampusTilemapBuilder: Removed " + removed + " out-of-bounds tiles from " + zoneName + ". Final bounded size is " + map.Width + "x" + map.Height + ".");
        }
        else
        {
            Debug.Log("JayGeneratedCampusTilemapBuilder: " + zoneName + " fits within " + map.Width + "x" + map.Height + " cells.");
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
