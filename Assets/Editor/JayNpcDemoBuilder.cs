#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

/// <summary>
/// CP5030 NPC behaviour demo for Jay's zombie RPG.
///
/// ONE-CLICK SETUP: "Tools > CP5030 > Setup NPC Demo Arena" runs the whole chain -
/// reference-tile setup (needs examples.unitypackage imported once), character sheet
/// import from SpriteStaging/ (skipped when already done), builds BOTH level prefabs
/// into Resources/NpcDemo/, and creates/opens the Game scene (menu, settings, pause).
///
/// GAME LOOP: escort the Survivor to the extraction zone (win); dying fails the level
/// and offers a restart. Level 1 is an easy open campus with three independent-A*
/// zombies. Level 2 is a large maze hunted by a coordinated flow-field horde that
/// learns the player's routes - the paper's "iterative improvement" contrast.
///
/// FAILSAFE ELEVATION (why platforms can no longer break or mislead):
/// - The nav grid is a heightmap: each column's floor is its TOPMOST tile, and steps
///   between columns are allowed only when the height difference is small. Ramps are
///   intermediate-height tiles (level + 0.5), so a placed ramp is ALWAYS climbable and
///   a cliff (delta 1.0) is ALWAYS blocked - colliders are cosmetic, not load-bearing.
/// - Ground stays painted under platforms and bridges, so there are no black void
///   seams; the lower tile reads as terrain thickness.
/// - The Platform() helper builds everything in one call: tinted floor (each height has
///   a visibly different tint), cliff art on the visible faces, a PARAPET WALL on the
///   otherwise-invisible back edge, and its ramps - correct by construction.
/// - After painting, the builder rebuilds the nav and validates: reachability probes for
///   every platform/objective/spawn plus a whole-map reachable-fraction check; the maze
///   additionally AUTO-REPAIRS unreachable rooms by carving extra doorways.
///
/// RENDERING: flat layers on tilemaps; every tall object is an individually Y-sorted
/// sprite (YSorter), deterministic in any render pipeline.
/// </summary>
public static class JayNpcDemoBuilder
{
    private const string TileFolder = "Assets/Tiles/Tile Assets/Jay/LargeCampus";
    private const string DemoSpriteFolder = "Assets/Tiles/Sprites/Jay/NpcDemo";
    private const string RootName = "NpcDemoArena";

    // Level 1 bounds.
    private const int Width = 64;
    private const int Height = 64;
    private static readonly Vector3Int PlayerStart = new Vector3Int(31, 24, 0);
    private static readonly Vector3Int L1Extraction = new Vector3Int(52, 50, 1);

    // Level 2 bounds: a LOT bigger maze (3.5x Level 1's area) - space is the player's
    // compensation for facing twice the zombies.
    private const int MazeW = 120;
    private const int MazeH = 120;
    private static readonly Vector3Int MazePlayerStart = new Vector3Int(60, 60, 0);
    private static readonly Vector3Int L2Extraction = new Vector3Int(112, 112, 0);
    private static readonly Vector3Int L2SurvivorStart = new Vector3Int(7, 7, 0);
    private static readonly int[] MazeLinesX = { 14, 26, 40, 52, 66, 80, 94, 106 };
    private static readonly int[] MazeLinesY = { 14, 28, 40, 54, 68, 82, 96, 108 };

    private const string GroundLeaf = "iso_platform_leaf_alt_512x256";
    private const string GroundPlain = "iso_platform_plain_alt_512x256";
    private const string RoadTile = "iso_road_NEtoSW_512x256";
    private const string RoadEdgeBottom = "iso_roadside_line_512x256";
    private const string RoadEdgeTop = "iso_roadside_line_top_512x256";
    private const string RoadTileY = "iso_road_NWtoSE_512x256";
    private const string RoadEdgeYBottom = "iso_roadside_line_NWtoSE_bottom_512x256";
    private const string RoadEdgeYTop = "iso_roadside_line_NWtoSE_top_512x256";

    // Height tints: every elevation level is visibly distinct at a glance.
    private static readonly Color TintZ1 = new Color(1f, 0.88f, 0.66f);
    private static readonly Color TintZ2 = new Color(1f, 0.78f, 0.52f);
    private static readonly Color TintExtraction = new Color(0.62f, 1f, 0.66f);
    // Sand-coloured floor strip marking every doorway/exit threshold.
    private static readonly Color TintThreshold = new Color(1f, 0.93f, 0.70f);

    private static readonly string[] RequiredTiles =
    {
        GroundLeaf, GroundPlain, RoadTile, RoadEdgeBottom, RoadEdgeTop,
        RoadTileY, RoadEdgeYBottom, RoadEdgeYTop, "collider_debug",
        "high_wall_concrete", "high_wall_concrete_top", "high_wall_concrete_N", "high_wall_concrete_TLBR",
        "high_wall_stone", "high_wall_stone_top", "high_wall_stone_N", "high_wall_brick_TLBR_far",
        "low_wall_concrete_N",
        "low_wall_stone", "low_wall_stone_top", "low_wall_stone_N", "low_wall_stone_S",
        "block_wall_concrete", "block_wall_stone", "block_wall_fence", "block_cliff", "block_ramp",
        "ramp_se", "ramp_ne", "cliff_front", "cliff_left", "cliff_right", "drop_shadow", "dark_shadow",
        "dining_table", "chair_stack", "crate_stack", "barrel_red", "rubble_pile",
        "bush", "small_tree", "planter", "kitchen_shelf", "queue_posts", "blood_stain", "trash_decal"
    };

    private sealed class Arena
    {
        public GameObject Root;
        public Grid Grid;
        public Tilemap Base;
        public Tilemap Shadows;
        public Tilemap Cliffs;
        public Tilemap Ramps;
        public Tilemap Passables;
        public Tilemap Colliders;
        public Transform PropsParent;
        public readonly Dictionary<Vector3Int, GameObject> Props = new Dictionary<Vector3Int, GameObject>();
    }

    [MenuItem("Tools/CP5030/Setup NPC Demo Arena")]
    public static void SetupDemoArena()
    {
        if (!JayLargeCampusTilemapBuilder.SetupTileAssets())
        {
            return;   // Errors already logged (usually: examples.unitypackage not imported).
        }
        EnsureBlockWallSprites();

        bool characterSheetsImported = AssetDatabase.LoadAssetAtPath<Texture2D>(
            "Assets/Tiles/Sprites/Jay/NpcDemo/Characters/zombie_idle.png") != null;
        if (!characterSheetsImported && Directory.Exists("SpriteStaging"))
        {
            JayNpcSpriteImporter.ProcessAll();
        }

        EnsureResourcesFolders();
        BuildArena(false, "Assets/Resources/NpcDemo/Level1.prefab",
            "Level 1 (easy)", "Independent A* pursuit - escort & extract");
        BuildArena(true, "Assets/Resources/NpcDemo/Level2.prefab",
            "Level 2 (hard)", "Probabilistic occupancy-map pursuit (Bayesian search + flow fields)");
        CreateGameScene();

        Debug.Log("JayNpcDemoBuilder: both levels built to Resources/NpcDemo and the Game scene is open. " +
                  "Press Play, pick a level; escort the Survivor to the green EXTRACTION zone. ESC pauses.");
    }

    /// <summary>Builds one level into the working scene, saves it as a Resources prefab, then cleans up.</summary>
    private static void BuildArena(bool maze, string prefabPath, string levelLabel, string algoLabel)
    {
        Dictionary<string, TileBase> t = LoadTiles();
        if (t == null)
        {
            return;
        }
        Sprite unitSprite = EnsurePlaceholderUnitSprite();

        GameObject old = GameObject.Find(RootName);
        if (old != null)
        {
            Object.DestroyImmediate(old);
        }

        Arena a = CreateArena();
        IsoNavGrid nav = a.Root.GetComponent<IsoNavGrid>();
        nav.baseMap = a.Base;
        nav.rampsMap = a.Ramps;
        nav.collidersMap = a.Colliders;

        NavMetricsHud hud;
        if (maze)
        {
            PaintMazeArena(a, t);
            nav.Build();
            RepairMazeConnectivity(a, t, nav);
            ClearSpawnColumns(a, MazeSpawnCells());
            nav.Build();
            ValidateLevel(nav, "Level 2", MazePlayerStart,
                new[] { "Survivor A start", "Survivor B start", "Extraction zone", "Platform A top", "Platform B top" },
                new[] { L2SurvivorStart, new Vector3Int(33, 101, 0), L2Extraction,
                        new Vector3Int(19, 60, 1), new Vector3Int(85, 34, 1) });
            hud = SpawnHordeActors(a, nav, unitSprite);
        }
        else
        {
            PaintArena(a, t);
            ClearSpawnColumns(a, Level1SpawnCells());
            nav.Build();
            ValidateLevel(nav, "Level 1", PlayerStart,
                new[] { "Survivor A start", "Survivor B start", "Extraction zone", "NW platform", "Upper deck", "NE terrace", "Bridge" },
                new[] { new Vector3Int(16, 45, 1), new Vector3Int(9, 12, 0), L1Extraction, new Vector3Int(20, 44, 1),
                        new Vector3Int(12, 51, 2), new Vector3Int(45, 45, 1), new Vector3Int(33, 47, 1) });
            hud = SpawnActors(a, nav, unitSprite);
        }
        if (hud != null)
        {
            hud.levelLabel = levelLabel;
            hud.algorithmLabel = algoLabel;
        }

        PrefabUtility.SaveAsPrefabAsset(a.Root, prefabPath);
        Object.DestroyImmediate(a.Root);   // The runtime instantiates the prefab; keep the scene clean.
        AssetDatabase.SaveAssets();
        Debug.Log("JayNpcDemoBuilder: built " + levelLabel + " -> " + prefabPath + ".");
    }

    // ---------------------------------------------------------------- validation

    private static void ValidateLevel(IsoNavGrid nav, string levelName, Vector3Int playerStart,
        string[] probeNames, Vector3Int[] probeCells)
    {
        Vector3Int start = new Vector3Int(playerStart.x, playerStart.y, nav.ColumnLevel(playerStart.x, playerStart.y));
        if (!nav.IsWalkable(start))
        {
            Debug.LogError(levelName + " validation: PLAYER START " + start + " is not walkable!");
            return;
        }

        int reachable = nav.CountReachableFrom(start);
        float fraction = nav.TotalWalkableColumns > 0 ? (float)reachable / nav.TotalWalkableColumns : 0f;
        string fracMsg = levelName + " validation: " + reachable + "/" + nav.TotalWalkableColumns +
                         " walkable columns reachable from the player start (" + (fraction * 100f).ToString("F0") + "%).";
        if (fraction < 0.85f)
        {
            Debug.LogWarning(fracMsg + " Large unreachable pockets exist - check the layout.");
        }
        else
        {
            Debug.Log(fracMsg);
        }

        for (int i = 0; i < probeCells.Length; i++)
        {
            Vector3Int p = new Vector3Int(probeCells[i].x, probeCells[i].y,
                nav.ColumnLevel(probeCells[i].x, probeCells[i].y));
            if (!nav.IsWalkable(p))
            {
                Debug.LogWarning(levelName + " validation: probe '" + probeNames[i] + "' at " + p + " is not walkable.");
            }
            else if (!nav.IsReachable(start, p))
            {
                Debug.LogWarning(levelName + " validation: '" + probeNames[i] + "' is UNREACHABLE from the player start.");
            }
            else
            {
                Debug.Log(levelName + " validation: '" + probeNames[i] + "' reachable. OK.");
            }
        }
    }

    private static Vector2Int[] Level1SpawnCells()
    {
        return new[]
        {
            new Vector2Int(PlayerStart.x, PlayerStart.y),
            new Vector2Int(16, 45), new Vector2Int(12, 51), new Vector2Int(20, 44),
            new Vector2Int(33, 47), new Vector2Int(45, 45),
            new Vector2Int(9, 12), new Vector2Int(12, 20),
            new Vector2Int(50, 14), new Vector2Int(42, 10), new Vector2Int(58, 10),
            new Vector2Int(58, 22), new Vector2Int(42, 22),
            new Vector2Int(30, 14), new Vector2Int(58, 31), new Vector2Int(4, 31), new Vector2Int(59, 31)
        };
    }

    private static Vector2Int[] MazeSpawnCells()
    {
        return new[]
        {
            new Vector2Int(MazePlayerStart.x, MazePlayerStart.y),
            new Vector2Int(L2SurvivorStart.x, L2SurvivorStart.y),
            new Vector2Int(6, 6), new Vector2Int(10, 10),
            new Vector2Int(33, 101), new Vector2Int(32, 100), new Vector2Int(35, 103),
            new Vector2Int(7, 105), new Vector2Int(112, 8), new Vector2Int(60, 8),
            new Vector2Int(60, 112), new Vector2Int(25, 25), new Vector2Int(95, 95)
        };
    }

    /// <summary>Removes any prop/collider that decoration may have dropped onto a spawn cell.</summary>
    private static void ClearSpawnColumns(Arena a, Vector2Int[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            for (int z = 0; z <= 2; z++)
            {
                Vector3Int c = new Vector3Int(cells[i].x, cells[i].y, z);
                if (a.Props.TryGetValue(c, out GameObject prop))
                {
                    Object.DestroyImmediate(prop);
                    a.Props.Remove(c);
                }
                a.Colliders.SetTile(c, null);
            }
        }
    }

    // ---------------------------------------------------------------- setup / assets

    private static void EnsureResourcesFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
        }
        if (!AssetDatabase.IsValidFolder("Assets/Resources/NpcDemo"))
        {
            AssetDatabase.CreateFolder("Assets/Resources", "NpcDemo");
        }
    }

    private static Dictionary<string, TileBase> LoadTiles()
    {
        Dictionary<string, TileBase> tiles = new Dictionary<string, TileBase>();
        bool ok = true;
        for (int i = 0; i < RequiredTiles.Length; i++)
        {
            TileBase tile = AssetDatabase.LoadAssetAtPath<TileBase>(TileFolder + "/" + RequiredTiles[i] + ".asset");
            if (tile == null)
            {
                ok = false;
                Debug.LogError("Missing tile asset: " + TileFolder + "/" + RequiredTiles[i] + ".asset");
            }
            else
            {
                tiles[RequiredTiles[i]] = tile;
            }
        }
        if (!ok)
        {
            Debug.LogError("JayNpcDemoBuilder: tile assets missing. Import examples.unitypackage, then run Tools > CP5030 > Setup NPC Demo Arena again.");
            return null;
        }
        return tiles;
    }

    private static Sprite EnsurePlaceholderUnitSprite()
    {
        if (!AssetDatabase.IsValidFolder(DemoSpriteFolder))
        {
            AssetDatabase.CreateFolder("Assets/Tiles/Sprites/Jay", "NpcDemo");
        }

        string path = DemoSpriteFolder + "/unit_placeholder.png";
        if (AssetImporter.GetAtPath(path) == null)
        {
            Texture2D tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            Color clear = new Color(0f, 0f, 0f, 0f);
            Color fill = Color.white;
            Color outline = new Color(0.12f, 0.12f, 0.12f, 1f);
            Vector2 center = new Vector2(32f, 30f);
            for (int x = 0; x < 64; x++)
            {
                for (int y = 0; y < 64; y++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    tex.SetPixel(x, y, d <= 19f ? fill : d <= 23f ? outline : clear);
                }
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
        }

        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 64f;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        TextureImporterSettings settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Custom;
        settings.spritePivot = new Vector2(0.5f, 0.2f);   // Feet near the sprite bottom.
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ------------------------------------------------------- full-cell block wall sprites

    /// <summary>
    /// Generates the "block wall" sprites: a solid prism drawn over the EXACT cell
    /// diamond (top diamond face + SW/SE side faces), pivot at the base diamond's
    /// centre. One sprite covers one cell, so wall art and wall collider can never
    /// disagree - unlike the reference wall sprites, which draw along a single cell
    /// edge and leave most of their blocked cell looking walkable.
    /// </summary>
    private static void EnsureBlockWallSprites()
    {
        if (!AssetDatabase.IsValidFolder(DemoSpriteFolder))
        {
            AssetDatabase.CreateFolder("Assets/Tiles/Sprites/Jay", "NpcDemo");
        }

        BlockWallSprite("block_wall_concrete", 288, false, 4211,
            new Color(0.68f, 0.69f, 0.72f), new Color(0.56f, 0.57f, 0.61f), new Color(0.42f, 0.43f, 0.48f));
        BlockWallSprite("block_wall_stone", 288, true, 4212,
            new Color(0.63f, 0.59f, 0.53f), new Color(0.52f, 0.48f, 0.43f), new Color(0.39f, 0.36f, 0.32f));
        BlockWallSprite("block_wall_fence", 96, true, 4213,
            new Color(0.60f, 0.58f, 0.54f), new Color(0.50f, 0.48f, 0.44f), new Color(0.38f, 0.36f, 0.33f));
        // Elevation faces: warm EARTH blocks, one z-level tall (128 px = 0.5 world),
        // clearly terrain rather than architecture.
        BlockWallSprite("block_cliff", 128, true, 4214,
            new Color(0.58f, 0.48f, 0.36f), new Color(0.48f, 0.39f, 0.29f), new Color(0.36f, 0.29f, 0.22f));
        // Exit marker: bright wooden wedge ramp rising one z-level toward the north.
        BlockRampSprite("block_ramp", 4215);
        AssetDatabase.SaveAssets();
    }

    /// <summary>Draws one block-wall PNG, imports it (256 PPU, pivot on the base diamond
    /// centre) and creates/updates its Tile asset in the shared tile folder.</summary>
    private static void BlockWallSprite(string name, int faceH, bool courses, int seed,
        Color top, Color sw, Color se)
    {
        const int W = 256;              // 1.0 world unit at 256 PPU = one cell diamond wide.
        const int DiamondH = 128;       // 0.5 world unit = one cell diamond tall.
        int H = DiamondH + faceH;

        Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        Color clear = new Color(0f, 0f, 0f, 0f);
        Color outline = new Color(0.16f, 0.15f, 0.14f, 1f);
        System.Random rng = new System.Random(seed);
        float[] columnNoise = new float[W];
        for (int px = 0; px < W; px++)
        {
            columnNoise[px] = (float)rng.NextDouble();
        }

        for (int px = 0; px < W; px++)
        {
            // Diamond extent at this column: full height at the centre, zero at the tips.
            float extent = (1f - Mathf.Abs(px - 127.5f) / 128f) * (DiamondH / 2f);
            int yBottom = Mathf.RoundToInt(DiamondH / 2f - extent);   // South silhouette (V shape).
            int yRidge = yBottom + faceH;                              // Face/top boundary.
            int yTop = Mathf.RoundToInt(DiamondH / 2f + extent) + faceH;

            for (int py = 0; py < H; py++)
            {
                if (py < yBottom || py >= yTop)
                {
                    tex.SetPixel(px, py, clear);
                    continue;
                }

                Color c;
                if (py < yRidge)
                {
                    c = px < 128 ? sw : se;                            // Side faces.
                    if (courses && (py - yBottom) % 24 == 0)
                    {
                        c *= 0.82f;                                    // Mortar course lines.
                    }
                    if (px == 127 || px == 128)
                    {
                        c *= 0.78f;                                    // Front corner ridge.
                    }
                }
                else
                {
                    c = top;                                           // Top diamond face.
                }

                // Subtle per-column and per-pixel grain so big runs don't band.
                float n = 0.96f + 0.08f * columnNoise[(px * 31 + py * 17) % W];
                c = new Color(c.r * n, c.g * n, c.b * n, 1f);

                bool edge = py == yBottom || py == yTop - 1 || py == yRidge ||
                            px == 0 || px == W - 1;
                tex.SetPixel(px, py, edge ? Color.Lerp(c, outline, 0.65f) : c);
            }
        }
        tex.Apply();
        SaveBlockSprite(name, tex, H);
    }

    /// <summary>
    /// Bright wooden wedge ramp rising one z-level toward the NORTH cell edge - the
    /// unmistakable "this is the way up" marker on every platform face. Same footprint
    /// and pivot rules as the block sprites; placed on the Ramps tilemap.
    /// </summary>
    private static void BlockRampSprite(string name, int seed)
    {
        const int W = 256;
        const int DiamondH = 128;
        const int Rise = 128;                 // One z-level = 0.5 world unit = 128 px.
        const int H = DiamondH + Rise;

        Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
        Color clear = new Color(0f, 0f, 0f, 0f);
        for (int px = 0; px < W; px++)
        {
            for (int py = 0; py < H; py++)
            {
                tex.SetPixel(px, py, clear);
            }
        }

        Color plank = new Color(0.80f, 0.60f, 0.34f);
        Color plankSeam = new Color(0.60f, 0.44f, 0.25f);
        Color sideFace = new Color(0.46f, 0.33f, 0.20f);
        Color outline = new Color(0.18f, 0.12f, 0.07f);
        System.Random rng = new System.Random(seed);
        float[] grain = new float[W];
        for (int i = 0; i < W; i++)
        {
            grain[i] = (float)rng.NextDouble();
        }

        // West side face: between the S->W base edge and that edge lifted by the slope.
        for (int px = 0; px <= 128; px++)
        {
            float extent = (1f - Mathf.Abs(px - 127.5f) / 128f) * (DiamondH / 2f);
            int yBottom = Mathf.RoundToInt(DiamondH / 2f - extent);
            int lift = Mathf.RoundToInt((128 - px) * (Rise / 128f));
            for (int py = yBottom; py <= yBottom + lift && py < H; py++)
            {
                Color c = sideFace * (0.94f + 0.10f * grain[(px * 13 + py * 7) % W]);
                bool edge = py == yBottom || py == yBottom + lift || px == 0;
                c = edge ? Color.Lerp(c, outline, 0.6f) : c;
                c.a = 1f;
                tex.SetPixel(px, py, c);
            }
        }

        // Sloped top surface, sampled in cell space (u = east axis, v = north axis):
        // screen x = (u - v) * 128 + 127.5, screen y = (u + v) * 64 + v * Rise.
        // Plank seams run ACROSS the slope (constant v), pointing the way up.
        const int Samples = 768;
        for (int i = 0; i <= Samples; i++)
        {
            float u = i / (float)Samples;
            for (int j = 0; j <= Samples; j++)
            {
                float v = j / (float)Samples;
                int px = Mathf.Clamp(Mathf.RoundToInt((u - v) * 128f + 127.5f), 0, W - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt((u + v) * 64f + v * Rise), 0, H - 1);
                bool seam = ((int)(v * 128f)) % 22 < 2;
                Color c = seam ? plankSeam : plank;
                c *= 0.94f + 0.10f * grain[(px * 31 + py * 17) % W];
                c *= Mathf.Lerp(0.92f, 1.06f, v);        // Brighter toward the top.
                bool edge = u < 0.02f || u > 0.98f || v < 0.02f || v > 0.98f;
                c = edge ? Color.Lerp(c, outline, 0.55f) : c;
                c.a = 1f;
                tex.SetPixel(px, py, c);
            }
        }

        tex.Apply();
        SaveBlockSprite(name, tex, H);
    }

    /// <summary>Writes the PNG, imports it (256 PPU, pivot on the base diamond centre)
    /// and creates/updates its Tile asset in the shared tile folder.</summary>
    private static void SaveBlockSprite(string name, Texture2D tex, int height)
    {
        string path = DemoSpriteFolder + "/" + name + ".png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);

        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 256f;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        TextureImporterSettings settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Custom;
        settings.spritePivot = new Vector2(0.5f, 64f / height);   // Base diamond centre.
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        string tilePath = TileFolder + "/" + name + ".asset";
        Tile tile = AssetDatabase.LoadAssetAtPath<Tile>(tilePath);
        if (tile == null)
        {
            tile = ScriptableObject.CreateInstance<Tile>();
            AssetDatabase.CreateAsset(tile, tilePath);
        }
        tile.sprite = sprite;
        tile.colliderType = Tile.ColliderType.None;
        EditorUtility.SetDirty(tile);
    }

    private static Arena CreateArena()
    {
        Arena a = new Arena();
        a.Root = new GameObject(RootName);
        a.Grid = a.Root.AddComponent<Grid>();
        a.Grid.cellLayout = GridLayout.CellLayout.IsometricZAsY;
        a.Grid.cellSize = new Vector3(1f, 0.5f, 1f);

        a.Base = MakeLayer(a.Root, "Base", 0, true, false);
        a.Shadows = MakeLayer(a.Root, "Shadows", 1, true, false);
        a.Cliffs = MakeLayer(a.Root, "Cliffs", 2, true, false);
        a.Ramps = MakeLayer(a.Root, "Ramps", 3, true, false);
        a.Passables = MakeLayer(a.Root, "Passables", 4, true, false);
        a.Colliders = MakeLayer(a.Root, "Colliders", 7, false, true);

        GameObject props = new GameObject("Props");
        props.transform.SetParent(a.Root.transform, false);
        a.PropsParent = props.transform;

        a.Root.AddComponent<IsoNavGrid>();
        return a;
    }

    private static Tilemap MakeLayer(GameObject root, string name, int order, bool visible, bool collider)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        Tilemap map = go.AddComponent<Tilemap>();
        map.tileAnchor = new Vector3(0.5f, 0.5f, 0f);
        TilemapRenderer r = go.AddComponent<TilemapRenderer>();
        r.sortingOrder = order;
        r.enabled = visible;
        if (collider)
        {
            go.AddComponent<TilemapCollider2D>();
        }
        return map;
    }

    // ---------------------------------------------------------------- props (Y-sorted sprites)

    private static Sprite SpriteOf(Dictionary<string, TileBase> t, string name)
    {
        Tile tile = t[name] as Tile;
        return tile != null ? tile.sprite : null;
    }

    private static void PlaceProp(Arena a, Dictionary<string, TileBase> t, string tileName, int x, int y, int z, bool collides)
    {
        Vector3Int cell = new Vector3Int(x, y, z);
        if (a.Props.TryGetValue(cell, out GameObject existing))
        {
            Object.DestroyImmediate(existing);
            a.Props.Remove(cell);
        }

        Sprite sprite = SpriteOf(t, tileName);
        if (sprite == null)
        {
            return;
        }
        GameObject go = new GameObject(tileName + " (" + x + "," + y + "," + z + ")");
        go.transform.SetParent(a.PropsParent, false);
        Vector3 pos = a.Grid.GetCellCenterWorld(cell);
        go.transform.position = pos;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = YSorter.OrderFor(pos.y);
        a.Props[cell] = go;

        if (collides)
        {
            Place(a.Colliders, t["collider_debug"], x, y, z);
        }
    }

    /// <summary>
    /// Solid wall run whose art fills every blocked cell: block-wall sprites are a
    /// full-cell prism (top diamond + SW/SE faces over the exact cell diamond, pivot at
    /// the base centre), so the wall you SEE is exactly the set of cells the collider
    /// blocks - no standing "on" walls, no bumping into seemingly empty space.
    /// </summary>
    private static void WallBlockRow(Arena a, Dictionary<string, TileBase> t, string tileName, int x0, int x1, int y, params int[] gaps)
    {
        for (int x = x0; x <= x1; x++)
        {
            if (System.Array.IndexOf(gaps, x) >= 0) { continue; }
            PlaceProp(a, t, tileName, x, y, 0, true);
        }
    }

    private static void WallBlockCol(Arena a, Dictionary<string, TileBase> t, string tileName, int x, int y0, int y1, params int[] gaps)
    {
        for (int y = y0; y <= y1; y++)
        {
            if (System.Array.IndexOf(gaps, y) >= 0) { continue; }
            PlaceProp(a, t, tileName, x, y, 0, true);
        }
    }

    private static void WallRow(Arena a, Dictionary<string, TileBase> t, string tileName, int x0, int x1, int y, int z, params int[] gaps)
    {
        for (int x = x0; x <= x1; x++)
        {
            if (System.Array.IndexOf(gaps, x) >= 0) { continue; }
            PlaceProp(a, t, tileName, x, y, z, true);
        }
    }

    private static void WallCol(Arena a, Dictionary<string, TileBase> t, string tileName, int x, int y0, int y1, int z, params int[] gaps)
    {
        for (int y = y0; y <= y1; y++)
        {
            if (System.Array.IndexOf(gaps, y) >= 0) { continue; }
            PlaceProp(a, t, tileName, x, y, z, true);
        }
    }

    /// <summary>Ground column with nothing above it and no collider - safe for z0 props/decor.</summary>
    private static bool OpenGround(Arena a, int x, int y)
    {
        return a.Base.HasTile(new Vector3Int(x, y, 0)) &&
               !a.Base.HasTile(new Vector3Int(x, y, 1)) &&
               !a.Base.HasTile(new Vector3Int(x, y, 2)) &&
               !a.Colliders.HasTile(new Vector3Int(x, y, 0));
    }

    /// <summary>Places a solid prop only on open ground (never blocks structure).</summary>
    private static void SafeProp(Arena a, Dictionary<string, TileBase> t, string tileName, int x, int y)
    {
        if (OpenGround(a, x, y))
        {
            PlaceProp(a, t, tileName, x, y, 0, true);
        }
    }

    /// <summary>Places a solid prop at an explicit elevation, only on a free floor cell.</summary>
    private static void SafePropAt(Arena a, Dictionary<string, TileBase> t, string tileName, int x, int y, int z)
    {
        Vector3Int c = new Vector3Int(x, y, z);
        if (a.Base.HasTile(c) && !a.Colliders.HasTile(c))
        {
            PlaceProp(a, t, tileName, x, y, z, true);
        }
    }

    private static void TintCell(Tilemap map, int x, int y, int z, Color c)
    {
        Vector3Int p = new Vector3Int(x, y, z);
        map.SetTileFlags(p, TileFlags.None);
        map.SetColor(p, c);
    }

    /// <summary>Sand-tinted floor cell marking a doorway threshold.</summary>
    private static void TintDoor(Arena a, int x, int y)
    {
        TintCell(a.Base, x, y, 0, TintThreshold);
    }

    // ---------------------------------------------------------------- failsafe elevation helpers

    /// <summary>
    /// Raised platform, correct by construction: tinted floor at z (ground below stays -
    /// it fills the seam), cliff art + cosmetic colliders on the visible S/W/E faces,
    /// a PARAPET WALL along the otherwise-invisible back edge, and ramps on the south
    /// face (intermediate height, guaranteed climbable). rampX0..rampX1 sit on row y-1.
    /// </summary>
    private static void Platform(Arena a, Dictionary<string, TileBase> t, int x, int y, int w, int h, int z, int rampX0, int rampX1)
    {
        Color tint = z >= 2 ? TintZ2 : TintZ1;

        for (int ix = x; ix < x + w; ix++)
        {
            for (int iy = y; iy < y + h; iy++)
            {
                Place(a.Base, t[GroundPlain], ix, iy, z);
                TintCell(a.Base, ix, iy, z, tint);
            }
        }

        // Bright rim along the drop edges so the height also reads from above.
        Color rim = Color.Lerp(tint, Color.white, 0.4f);
        for (int ix = x; ix < x + w; ix++)
        {
            TintCell(a.Base, ix, y, z, rim);
        }
        for (int iy = y; iy < y + h; iy++)
        {
            TintCell(a.Base, x, iy, z, rim);
            TintCell(a.Base, x + w - 1, iy, z, rim);
        }

        // Visible faces: earth blocks, one z-level tall - unmistakably terrain, never
        // confusable with the grey walls (art + cosmetic colliders; the height gate
        // does the real blocking).
        for (int ix = x; ix < x + w; ix++)
        {
            Place(a.Cliffs, t["block_cliff"], ix, y - 1, z - 1);
            Place(a.Colliders, t["collider_debug"], ix, y - 1, z - 1);
            Place(a.Shadows, t["drop_shadow"], ix, y - 2, z - 1);
        }
        for (int iy = y; iy < y + h; iy++)
        {
            Place(a.Cliffs, t["block_cliff"], x - 1, iy, z - 1);
            Place(a.Colliders, t["collider_debug"], x - 1, iy, z - 1);
            Place(a.Cliffs, t["block_cliff"], x + w, iy, z - 1);
            Place(a.Colliders, t["collider_debug"], x + w, iy, z - 1);
        }

        // Back-edge parapet: the visible marker for the boundary you cannot walk off.
        for (int ix = x; ix < x + w; ix++)
        {
            PlaceProp(a, t, "block_wall_fence", ix, y + h - 1, z, true);
        }

        // Ramps last, so they clear any cliff/collider the face pass put on their cells.
        for (int rx = rampX0; rx <= rampX1; rx++)
        {
            RampCell(a, t, "block_ramp", rx, y - 1, z - 1, tint);
        }
    }

    /// <summary>Intermediate-height ramp cell: clears blockers, ensures a floor, tints.</summary>
    private static void RampCell(Arena a, Dictionary<string, TileBase> t, string rampTile, int x, int y, int lowZ, Color tint)
    {
        Vector3Int cell = new Vector3Int(x, y, lowZ);
        a.Cliffs.SetTile(cell, null);
        a.Colliders.SetTile(cell, null);
        if (a.Props.TryGetValue(cell, out GameObject prop))
        {
            Object.DestroyImmediate(prop);
            a.Props.Remove(cell);
        }
        if (!a.Base.HasTile(cell))
        {
            Place(a.Base, t[GroundPlain], x, y, lowZ);
        }
        Place(a.Ramps, t[rampTile], x, y, lowZ);
        TintCell(a.Ramps, x, y, lowZ, tint);
    }

    /// <summary>
    /// Elevated walkway (z1) between two z1 platforms: rows yFloor..yFloor+2, where the
    /// top row carries a parapet (walkway is the lower two rows). The bridge columns'
    /// ground-level cliffs/colliders (the platforms' side faces) are cleared so the floor
    /// meets both platforms cleanly. Ground below stays painted - no voids.
    /// </summary>
    private static void Bridge(Arena a, Dictionary<string, TileBase> t, int x0, int x1, int yFloor)
    {
        int yParapet = yFloor + 2;
        for (int x = x0; x <= x1; x++)
        {
            for (int y = yFloor; y <= yParapet; y++)
            {
                Place(a.Base, t[GroundPlain], x, y, 1);
                TintCell(a.Base, x, y, 1, TintZ1);
                a.Cliffs.SetTile(new Vector3Int(x, y, 0), null);
                a.Colliders.SetTile(new Vector3Int(x, y, 0), null);
            }
            TintCell(a.Base, x, yFloor, 1, Color.Lerp(TintZ1, Color.white, 0.4f));   // Drop-edge rim.
            PlaceProp(a, t, "block_wall_fence", x, yParapet, 1, true);

            Place(a.Cliffs, t["block_cliff"], x, yFloor - 1, 0);
            Place(a.Colliders, t["collider_debug"], x, yFloor - 1, 0);
            Place(a.Shadows, t["drop_shadow"], x, yFloor - 2, 0);
        }
    }

    /// <summary>Green-tinted extraction pad with a post ring (south gap) and floating label.</summary>
    private static void ExtractionZone(Arena a, Dictionary<string, TileBase> t, int cx, int cy, int z)
    {
        for (int x = cx - 2; x <= cx + 2; x++)
        {
            for (int y = cy - 2; y <= cy + 2; y++)
            {
                if (a.Base.HasTile(new Vector3Int(x, y, z)))
                {
                    TintCell(a.Base, x, y, z, TintExtraction);
                }
            }
        }
        for (int x = cx - 3; x <= cx + 3; x++)
        {
            for (int y = cy - 3; y <= cy + 3; y++)
            {
                bool border = x == cx - 3 || x == cx + 3 || y == cy - 3 || y == cy + 3;
                bool southGap = y == cy - 3 && x >= cx - 1 && x <= cx + 1;
                if (border && !southGap)
                {
                    SafePropAt(a, t, "queue_posts", x, y, z);
                }
            }
        }
        CreateWorldLabel(a, "EXTRACTION", new Vector3Int(cx, cy, z), TintExtraction);
    }

    private static void CreateWorldLabel(Arena a, string text, Vector3Int cell, Color color)
    {
        GameObject go = new GameObject("Label " + text);
        go.transform.SetParent(a.Root.transform, false);
        go.transform.position = a.Grid.GetCellCenterWorld(cell) + new Vector3(0f, 0.7f, 0f);
        TextMesh tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.color = color;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.characterSize = 0.16f;
        tm.fontSize = 48;
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtin != null)
        {
            tm.font = builtin;
            go.GetComponent<MeshRenderer>().material = builtin.material;
        }
        go.GetComponent<MeshRenderer>().sortingOrder = 31000;
    }

    // ---------------------------------------------------------------- Level 1 painting

    private static void PaintArena(Arena a, Dictionary<string, TileBase> t)
    {
        // Ground. Chaotic-evacuation dressing: intact pavement, dropped belongings, panic blood.
        FillRect(a.Base, t[GroundPlain], 0, 0, Width, Height, 0);
        ScatterBlobs(a.Base, t[GroundLeaf], 1, 1, Width - 2, Height - 2, 0, 6, 3, 0.45f, 9901);
        Scatter(a.Passables, t["trash_decal"], 2, 2, Width - 4, Height - 4, 0, 0.06f, 9903);
        Scatter(a.Passables, t["blood_stain"], 2, 2, Width - 4, Height - 4, 0, 0.015f, 9904);
        ScatterBlobs(a.Passables, t["bush"], 2, 2, Width - 4, Height - 4, 0, 7, 3, 0.30f, 9905);

        // Main road, y 29..34, with grass in the cracks.
        for (int x = 0; x < Width; x++)
        {
            Place(a.Base, t[RoadEdgeBottom], x, 29, 0);
            for (int lane = 30; lane <= 33; lane++)
            {
                Place(a.Base, t[RoadTile], x, lane, 0);
            }
            Place(a.Base, t[RoadEdgeTop], x, 34, 0);
        }
        Scatter(a.Base, t[GroundLeaf], 1, 30, Width - 2, 4, 0, 0.045f, 9906);
        Scatter(a.Passables, t["trash_decal"], 1, 29, Width - 2, 6, 0, 0.12f, 9907);
        Scatter(a.Passables, t["blood_stain"], 1, 29, Width - 2, 6, 0, 0.04f, 9908);

        // Vertical road spur guiding from the road toward the platforms/bridge gap.
        for (int y = 35; y <= 44; y++)
        {
            Place(a.Base, t[RoadEdgeYBottom], 29, y, 0);
            for (int lane = 30; lane <= 32; lane++)
            {
                Place(a.Base, t[RoadTileY], lane, y, 0);
            }
            Place(a.Base, t[RoadEdgeYTop], 33, y, 0);
        }

        // Perimeter fence.
        WallBlockRow(a, t, "block_wall_fence", 0, Width - 1, 0);
        WallBlockRow(a, t, "block_wall_fence", 0, Width - 1, Height - 1);
        WallBlockCol(a, t, "block_wall_fence", 0, 1, Height - 2);
        WallBlockCol(a, t, "block_wall_fence", Width - 1, 1, Height - 2);

        // Two roadblocks with a clear centre (near the spur junction).
        Roadblock(a, t, 16, 9911);
        Roadblock(a, t, 46, 9912);
        BloodTrail(a, t, 46, 31, 52, 26, 9913);

        // SW: intact concrete building (x 4..24, y 6..24).
        FillRect(a.Base, t[GroundPlain], 4, 6, 21, 19, 0);
        FillRect(a.Shadows, t["dark_shadow"], 5, 4, 19, 2, 0);
        WallBlockRow(a, t, "block_wall_concrete", 4, 24, 6);
        WallBlockRow(a, t, "block_wall_concrete", 4, 24, 24, 13, 14, 15);      // North door.
        WallBlockCol(a, t, "block_wall_concrete", 4, 7, 23);
        WallBlockCol(a, t, "block_wall_concrete", 24, 7, 23, 13, 14, 15);      // East door.
        WallBlockCol(a, t, "block_wall_fence", 14, 7, 17);                     // Interior partition (low).
        TintDoor(a, 13, 24); TintDoor(a, 14, 24); TintDoor(a, 15, 24);
        TintDoor(a, 24, 13); TintDoor(a, 24, 14); TintDoor(a, 24, 15);
        PlaceProp(a, t, "kitchen_shelf", 6, 22, 0, true);
        PlaceProp(a, t, "kitchen_shelf", 7, 22, 0, true);
        PlaceProp(a, t, "dining_table", 18, 9, 0, true);
        PlaceProp(a, t, "chair_stack", 19, 9, 0, true);
        PlaceProp(a, t, "dining_table", 18, 20, 0, true);
        PlaceProp(a, t, "chair_stack", 19, 20, 0, true);
        PlaceProp(a, t, "crate_stack", 6, 8, 0, true);
        Scatter(a.Passables, t["blood_stain"], 5, 7, 19, 17, 0, 0.05f, 9914);

        // SE: ruined stone complex (x 38..60, y 6..26).
        FillRect(a.Base, t[GroundPlain], 38, 6, 23, 21, 0);
        FillRect(a.Shadows, t["dark_shadow"], 39, 4, 21, 2, 0);
        WallBlockRow(a, t, "block_wall_stone", 38, 60, 6, 43, 44, 53);
        WallBlockRow(a, t, "block_wall_stone", 38, 60, 26, 46, 47, 55, 56);
        WallBlockCol(a, t, "block_wall_stone", 38, 7, 25, 14, 15);
        WallBlockCol(a, t, "block_wall_stone", 60, 7, 25, 15, 16, 17);
        WallBlockRow(a, t, "block_wall_stone", 44, 54, 16, 48, 49);            // Collapsed inner wall.
        TintDoor(a, 43, 6); TintDoor(a, 44, 6); TintDoor(a, 53, 6);
        TintDoor(a, 46, 26); TintDoor(a, 47, 26); TintDoor(a, 55, 26); TintDoor(a, 56, 26);
        TintDoor(a, 38, 14); TintDoor(a, 38, 15);
        TintDoor(a, 60, 15); TintDoor(a, 60, 16); TintDoor(a, 60, 17);
        TintDoor(a, 48, 16); TintDoor(a, 49, 16);
        PlaceProp(a, t, "rubble_pile", 41, 10, 0, true);
        PlaceProp(a, t, "rubble_pile", 57, 22, 0, true);
        PlaceProp(a, t, "rubble_pile", 50, 9, 0, true);
        PlaceProp(a, t, "rubble_pile", 40, 20, 0, true);
        PlaceProp(a, t, "barrel_red", 58, 8, 0, true);
        PlaceProp(a, t, "barrel_red", 46, 20, 0, true);
        PlaceProp(a, t, "crate_stack", 52, 24, 0, true);
        FillRect(a.Shadows, t["dark_shadow"], 48, 11, 3, 3, 0);
        Scatter(a.Passables, t["blood_stain"], 39, 7, 21, 19, 0, 0.09f, 9915);
        BloodTrail(a, t, 49, 12, 45, 27, 9916);

        // Plaza between the buildings.
        PlaceProp(a, t, "small_tree", 30, 4, 0, true);
        PlaceProp(a, t, "small_tree", 34, 22, 0, true);
        PlaceProp(a, t, "planter", 28, 26, 0, true);
        PlaceProp(a, t, "planter", 29, 26, 0, true);
        PlaceProp(a, t, "planter", 34, 26, 0, true);
        PlaceProp(a, t, "planter", 35, 26, 0, true);
        PlaceProp(a, t, "crate_stack", 26, 20, 0, true);
        PlaceProp(a, t, "barrel_red", 26, 21, 0, true);
        PlaceProp(a, t, "rubble_pile", 36, 18, 0, true);
        FillRect(a.Shadows, t["dark_shadow"], 30, 10, 3, 2, 0);
        Scatter(a.Passables, t["trash_decal"], 26, 8, 12, 18, 0, 0.06f, 9917);

        // NW: two-tier platform (z1 with a z2 upper deck), both with wide south ramps.
        Platform(a, t, 6, 40, 21, 19, 1, 12, 15);
        PlaceProp(a, t, "crate_stack", 22, 43, 1, true);
        PlaceProp(a, t, "rubble_pile", 24, 55, 1, true);
        PlaceProp(a, t, "barrel_red", 8, 45, 1, true);

        Platform(a, t, 9, 49, 9, 6, 2, 12, 13);
        PlaceProp(a, t, "barrel_red", 10, 52, 2, true);
        PlaceProp(a, t, "crate_stack", 16, 50, 2, true);

        // NE: raised terrace (z1) hosting the extraction zone.
        Platform(a, t, 40, 40, 21, 19, 1, 48, 51);
        PlaceProp(a, t, "planter", 43, 44, 1, true);
        PlaceProp(a, t, "planter", 58, 44, 1, true);
        PlaceProp(a, t, "crate_stack", 44, 54, 1, true);

        // Bridge (z1) linking the two rooftops.
        Bridge(a, t, 27, 39, 47);

        // Extraction zone on the terrace.
        ExtractionZone(a, t, L1Extraction.x, L1Extraction.y, 1);

        // Chaotic-evacuation decoration (guarded: solid props only on open ground).
        Decorate(a, t);

        CompressAll(a);
    }

    private static void Decorate(Arena a, Dictionary<string, TileBase> t)
    {
        // Faint overgrowth at the untidy corners.
        GroundPatch(a, t, 2, 2, 5, 5, 71001);
        GroundPatch(a, t, Width - 7, 2, 5, 5, 71002);

        // Campus tree lines + hedges.
        for (int x = 4; x <= Width - 4; x += 6)
        {
            PerimeterTree(a, t, x, 3);
            PerimeterTree(a, t, x, Height - 3);
        }
        for (int y = 4; y <= Height - 4; y += 6)
        {
            PerimeterTree(a, t, 3, y);
            PerimeterTree(a, t, Width - 3, y);
        }
        HedgeRow(a, t, 26, 37, 5, 71010);
        HedgeRow(a, t, 26, 37, 27, 71011);
        HedgeCol(a, t, 26, 8, 18, 71012);
        HedgeCol(a, t, 37, 8, 18, 71013);

        // Trampled checkpoint north of the player start.
        TrampledCheckpoint(a, t, 27, 15, 34, 22, 71020);

        // Supplies spilled in the panic.
        SpilledSupplies(a, t, 8, 26, 6, 3, 71030);
        SpilledSupplies(a, t, 50, 28, 7, 3, 71031);

        // Camps abandoned mid-pack.
        EvacCamp(a, t, 27, 8, 71040);
        EvacCamp(a, t, 4, 59, 71041);
        EvacCamp(a, t, 56, 60, 71042);

        // Belongings flung in every direction.
        DroppedTrail(a, t, 12, 20, 33, 32, 71050);
        DroppedTrail(a, t, 52, 18, 34, 33, 71051);
        DroppedTrail(a, t, 30, 8, 30, 28, 71052);
        DroppedTrail(a, t, 31, 36, 33, 44, 71053);

        // Scattered debris north of the player start.
        ScatterDebris(a, t, 24, 6, 16, 10, 71060);
    }

    // ---------------------------------------------------------------- Level 2: maze

    private static void PaintMazeArena(Arena a, Dictionary<string, TileBase> t)
    {
        int w = MazeW, h = MazeH;
        FillRect(a.Base, t[GroundPlain], 0, 0, w, h, 0);
        ScatterBlobs(a.Base, t[GroundLeaf], 1, 1, w - 2, h - 2, 0, 14, 3, 0.4f, 81001);

        // Perimeter fence - low block walls (art fills the whole blocked cell).
        WallBlockRow(a, t, "block_wall_fence", 0, w - 1, 0);
        WallBlockRow(a, t, "block_wall_fence", 0, w - 1, h - 1);
        WallBlockCol(a, t, "block_wall_fence", 0, 1, h - 2);
        WallBlockCol(a, t, "block_wall_fence", w - 1, 1, h - 2);

        // Wall lattice with alternating materials (variable room sizes). Block-wall
        // sprites are full-cell prisms, so what you SEE is exactly what the collider
        // blocks.
        for (int i = 0; i < MazeLinesX.Length; i++)
        {
            string tile = (i % 2 == 0) ? "block_wall_concrete" : "block_wall_stone";
            WallBlockCol(a, t, tile, MazeLinesX[i], 1, h - 2);
        }
        for (int i = 0; i < MazeLinesY.Length; i++)
        {
            string tile = (i % 2 == 0) ? "block_wall_concrete" : "block_wall_stone";
            WallBlockRow(a, t, tile, 1, w - 2, MazeLinesY[i]);
        }

        // Large central hall: clear the lattice inside, then mark it with a cool paving
        // tint - a landmark plaza the player can orient by from anywhere nearby.
        ClearRectProps(a, 46, 48, 74, 76);
        for (int x = 46; x <= 74; x++)
        {
            for (int y = 48; y <= 76; y++)
            {
                TintCell(a.Base, x, y, 0, new Color(0.84f, 0.89f, 1f));
            }
        }

        // Carve doorways at segment midpoints (a few skipped for dead ends).
        int[] xb = LatticeBounds(MazeLinesX, w);
        int[] yb = LatticeBounds(MazeLinesY, h);
        Random.InitState(81010);
        for (int i = 0; i < MazeLinesX.Length; i++)
        {
            int x = MazeLinesX[i];
            for (int b = 0; b < yb.Length - 1; b++)
            {
                int midY = (yb[b] + yb[b + 1]) / 2;
                if (Random.value < 0.85f)
                {
                    CarveGap(a, t, x, midY);
                    CarveGap(a, t, x, midY + 1);
                }
            }
        }
        for (int i = 0; i < MazeLinesY.Length; i++)
        {
            int y = MazeLinesY[i];
            for (int b = 0; b < xb.Length - 1; b++)
            {
                int midX = (xb[b] + xb[b + 1]) / 2;
                if (Random.value < 0.85f)
                {
                    CarveGap(a, t, midX, y);
                    CarveGap(a, t, midX + 1, y);
                }
            }
        }

        // Grounding shadows along the near side of every wall cell that survived carving
        // (the ambient-occlusion strip visually plants the walls on the floor).
        for (int i = 0; i < MazeLinesX.Length; i++)
        {
            int x = MazeLinesX[i];
            for (int y = 1; y <= h - 2; y++)
            {
                if (a.Colliders.HasTile(new Vector3Int(x, y, 0)))
                {
                    Place(a.Shadows, t["drop_shadow"], x - 1, y, 0);
                }
            }
        }
        for (int i = 0; i < MazeLinesY.Length; i++)
        {
            int y = MazeLinesY[i];
            for (int x = 1; x <= w - 2; x++)
            {
                if (a.Colliders.HasTile(new Vector3Int(x, y, 0)))
                {
                    Place(a.Shadows, t["drop_shadow"], x, y - 1, 0);
                }
            }
        }

        // Two elevated rooms (high-ground play in the maze).
        Platform(a, t, 16, 57, 8, 8, 1, 18, 20);
        PlaceProp(a, t, "crate_stack", 17, 62, 1, true);
        Platform(a, t, 82, 31, 8, 8, 1, 84, 86);
        PlaceProp(a, t, "barrel_red", 88, 36, 1, true);

        // Overgrown courtyard rooms.
        Courtyard(a, t, 41, 15, 10, 12, 81020);
        Courtyard(a, t, 67, 83, 12, 12, 81021);
        Courtyard(a, t, 95, 55, 10, 12, 81022);

        // Storage rooms with spilled supplies.
        SpilledSupplies(a, t, 54, 16, 8, 8, 81030);
        SpilledSupplies(a, t, 16, 84, 8, 9, 81031);
        SpilledSupplies(a, t, 96, 16, 8, 8, 81032);

        // Extraction zone in the far NE corner room.
        ExtractionZone(a, t, L2Extraction.x, L2Extraction.y, 0);

        // Abandoned-plaza dressing in the central hall (kept clear of the player start).
        Random.InitState(81025);
        SafeProp(a, t, "planter", 48, 50);
        SafeProp(a, t, "planter", 72, 50);
        SafeProp(a, t, "planter", 48, 74);
        SafeProp(a, t, "planter", 72, 74);
        for (int i = 0; i < 8; i++)
        {
            int fx = Random.Range(48, 73);
            int fy = Random.Range(50, 75);
            if (Mathf.Abs(fx - MazePlayerStart.x) + Mathf.Abs(fy - MazePlayerStart.y) > 4)
            {
                SafeProp(a, t, (i % 2 == 0) ? "dining_table" : "chair_stack", fx, fy);
            }
        }
        SafeProp(a, t, "barrel_red", 66, 68);
        SafeProp(a, t, "rubble_pile", 53, 55);

        // Greenery-and-wreckage rhythm: props tucked into room corners across the maze
        // (SafeProp never lands on walls, platforms, or other props).
        Random.InitState(81050);
        for (int i = 0; i < xb.Length - 1; i++)
        {
            for (int j = 0; j < yb.Length - 1; j++)
            {
                if (Random.value < 0.4f)
                {
                    SafeProp(a, t, "small_tree", xb[i] + 2, yb[j] + 2);
                }
                if (Random.value < 0.3f)
                {
                    SafeProp(a, t, "rubble_pile", xb[i + 1] - 2, yb[j + 1] - 2);
                }
                if (Random.value < 0.2f)
                {
                    SafeProp(a, t, "crate_stack", xb[i + 1] - 2, yb[j] + 2);
                }
            }
        }

        // Paranoia dressing + a faint belongings trail hinting toward the extraction.
        Scatter(a.Passables, t["trash_decal"], 2, 2, w - 4, h - 4, 0, 0.05f, 81040);
        Scatter(a.Passables, t["blood_stain"], 2, 2, w - 4, h - 4, 0, 0.02f, 81041);
        ScatterBlobs(a.Passables, t["bush"], 2, 2, w - 4, h - 4, 0, 18, 2, 0.3f, 81042);
        DroppedTrail(a, t, 66, 66, 106, 106, 81043);
        BloodTrail(a, t, 54, 54, 30, 34, 81044);

        CompressAll(a);
    }

    private static int[] LatticeBounds(int[] lines, int size)
    {
        int[] b = new int[lines.Length + 2];
        b[0] = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            b[i + 1] = lines[i];
        }
        b[b.Length - 1] = size;
        return b;
    }

    private static void Courtyard(Arena a, Dictionary<string, TileBase> t, int x, int y, int w, int h, int seed)
    {
        FillRect(a.Base, t[GroundLeaf], x, y, w, h, 0);
        Random.InitState(seed);
        Scatter(a.Passables, t["bush"], x, y, w, h, 0, 0.25f, seed + 1);
        SafeProp(a, t, "small_tree", x + w / 2, y + h / 2);
        SafeProp(a, t, "small_tree", x + 1, y + h - 2);
        SafeProp(a, t, "planter", x + 1, y + 1);
        SafeProp(a, t, "planter", x + w - 2, y + h - 2);
        SafeProp(a, t, "rubble_pile", x + w - 2, y + 1);
    }

    private static void ClearRectProps(Arena a, int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                Vector3Int c = new Vector3Int(x, y, 0);
                if (a.Props.TryGetValue(c, out GameObject prop))
                {
                    Object.DestroyImmediate(prop);
                    a.Props.Remove(c);
                }
                a.Colliders.SetTile(c, null);
            }
        }
    }

    private static void CarveGap(Arena a, Dictionary<string, TileBase> t, int x, int y)
    {
        if (x <= 0 || y <= 0 || x >= MazeW - 1 || y >= MazeH - 1)
        {
            return;   // Never carve the perimeter fence.
        }
        Vector3Int c = new Vector3Int(x, y, 0);
        if (a.Props.TryGetValue(c, out GameObject prop))
        {
            Object.DestroyImmediate(prop);
            a.Props.Remove(c);
        }
        a.Colliders.SetTile(c, null);
        if (!a.Base.HasTile(c))
        {
            Place(a.Base, t[GroundPlain], x, y, 0);
        }
        TintCell(a.Base, x, y, 0, TintThreshold);   // Sand strip: every doorway reads as an exit.
    }

    /// <summary>
    /// Auto-repair: any lattice room whose centre is unreachable from the player start
    /// gets doorways carved on all four of its walls; repeats until everything connects.
    /// </summary>
    private static void RepairMazeConnectivity(Arena a, Dictionary<string, TileBase> t, IsoNavGrid nav)
    {
        int[] xb = LatticeBounds(MazeLinesX, MazeW);
        int[] yb = LatticeBounds(MazeLinesY, MazeH);

        for (int iteration = 0; iteration < 6; iteration++)
        {
            nav.Build();
            Vector3Int start = new Vector3Int(MazePlayerStart.x, MazePlayerStart.y,
                nav.ColumnLevel(MazePlayerStart.x, MazePlayerStart.y));
            bool allOk = true;

            for (int i = 0; i < xb.Length - 1; i++)
            {
                for (int j = 0; j < yb.Length - 1; j++)
                {
                    int cx = (xb[i] + xb[i + 1]) / 2;
                    int cy = (yb[j] + yb[j + 1]) / 2;
                    if (!FindWalkableNear(nav, cx, cy, 3, out Vector3Int roomCell))
                    {
                        continue;   // Fully occupied room - nothing to connect.
                    }
                    if (nav.IsReachable(start, roomCell))
                    {
                        continue;
                    }
                    allOk = false;
                    CarveGap(a, t, xb[i], cy); CarveGap(a, t, xb[i], cy + 1);
                    CarveGap(a, t, xb[i + 1], cy); CarveGap(a, t, xb[i + 1], cy + 1);
                    CarveGap(a, t, cx, yb[j]); CarveGap(a, t, cx + 1, yb[j]);
                    CarveGap(a, t, cx, yb[j + 1]); CarveGap(a, t, cx + 1, yb[j + 1]);
                    Debug.Log("Maze auto-repair: opened doorways for the room around (" + cx + "," + cy + ").");
                }
            }
            if (allOk)
            {
                break;
            }
        }
    }

    private static bool FindWalkableNear(IsoNavGrid nav, int cx, int cy, int radius, out Vector3Int cell)
    {
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r)
                    {
                        continue;
                    }
                    if (nav.TryGetColumnCell(cx + dx, cy + dy, out cell))
                    {
                        return true;
                    }
                }
            }
        }
        cell = default;
        return false;
    }

    // ---------------------------------------------------------------- shared decoration helpers

    private static void Roadblock(Arena a, Dictionary<string, TileBase> t, int x, int seed)
    {
        Random.InitState(seed);
        string[] pool = { "crate_stack", "barrel_red", "rubble_pile" };
        for (int lane = 30; lane <= 33; lane++)
        {
            if (Random.value < 0.8f)
            {
                PlaceProp(a, t, pool[Random.Range(0, pool.Length)], x, lane, 0, true);
            }
            if (Random.value < 0.5f)
            {
                PlaceProp(a, t, pool[Random.Range(0, pool.Length)], x + 1, lane, 0, true);
            }
        }
        FillRect(a.Shadows, t["dark_shadow"], x - 1, 30, 2, 2, 0);
    }

    private static void GroundPatch(Arena a, Dictionary<string, TileBase> t, int x, int y, int w, int h, int seed)
    {
        Random.InitState(seed);
        for (int ix = x; ix < x + w; ix++)
        {
            for (int iy = y; iy < y + h; iy++)
            {
                Vector3Int c = new Vector3Int(ix, iy, 0);
                if (OpenGround(a, ix, iy) && a.Base.GetTile(c) == t[GroundPlain] && Random.value < 0.7f)
                {
                    a.Base.SetTile(c, t[GroundLeaf]);
                }
            }
        }
    }

    private static void PerimeterTree(Arena a, Dictionary<string, TileBase> t, int x, int y)
    {
        Vector3Int c = new Vector3Int(x, y, 0);
        if (!OpenGround(a, x, y))
        {
            return;
        }
        TileBase ground = a.Base.GetTile(c);
        if (ground == t[RoadTile] || ground == t[RoadEdgeBottom] || ground == t[RoadEdgeTop] ||
            ground == t[RoadTileY] || ground == t[RoadEdgeYBottom] || ground == t[RoadEdgeYTop])
        {
            return;   // Keep the roads clear.
        }
        PlaceProp(a, t, "small_tree", x, y, 0, true);
        Place(a.Shadows, t["dark_shadow"], x, y, 0);
    }

    private static void HedgeRow(Arena a, Dictionary<string, TileBase> t, int x0, int x1, int y, int seed)
    {
        Random.InitState(seed);
        for (int x = x0; x <= x1; x++)
        {
            if (OpenGround(a, x, y) && Random.value < 0.75f)
            {
                Place(a.Passables, t["bush"], x, y, 0);
            }
        }
    }

    private static void HedgeCol(Arena a, Dictionary<string, TileBase> t, int x, int y0, int y1, int seed)
    {
        Random.InitState(seed);
        for (int y = y0; y <= y1; y++)
        {
            if (OpenGround(a, x, y) && Random.value < 0.75f)
            {
                Place(a.Passables, t["bush"], x, y, 0);
            }
        }
    }

    private static void TrampledCheckpoint(Arena a, Dictionary<string, TileBase> t, int x0, int y0, int x1, int y1, int seed)
    {
        Random.InitState(seed);
        int w = x1 - x0 + 1;
        int h = y1 - y0 + 1;
        for (int k = 0; k < 6; k++)
        {
            SafeProp(a, t, "queue_posts", Random.Range(x0, x1 + 1), Random.Range(y0, y1 + 1));
        }
        SafeProp(a, t, "barrel_red", Random.Range(x0, x1 + 1), Random.Range(y0, y1 + 1));
        Scatter(a.Passables, t["trash_decal"], x0, y0, w, h, 0, 0.4f, seed + 1);
        Scatter(a.Passables, t["blood_stain"], x0, y0, w, h, 0, 0.14f, seed + 2);
    }

    private static void SpilledSupplies(Arena a, Dictionary<string, TileBase> t, int x, int y, int w, int h, int seed)
    {
        Random.InitState(seed);
        string[] pool = { "crate_stack", "barrel_red", "rubble_pile" };
        int n = Mathf.Max(3, (w * h) / 5);
        for (int k = 0; k < n; k++)
        {
            SafeProp(a, t, pool[Random.Range(0, pool.Length)], Random.Range(x, x + w), Random.Range(y, y + h));
        }
        Scatter(a.Passables, t["trash_decal"], x - 1, y - 1, w + 2, h + 2, 0, 0.4f, seed + 1);
        Scatter(a.Passables, t["blood_stain"], x - 1, y - 1, w + 2, h + 2, 0, 0.1f, seed + 2);
    }

    private static void EvacCamp(Arena a, Dictionary<string, TileBase> t, int x, int y, int seed)
    {
        Random.InitState(seed);
        FillRect(a.Shadows, t["dark_shadow"], x, y, 2, 2, 0);
        SafeProp(a, t, "barrel_red", x + 2, y);
        SafeProp(a, t, "crate_stack", x - 1, y + 1);
        Scatter(a.Passables, t["trash_decal"], x - 2, y - 2, 7, 6, 0, 0.45f, seed + 1);
        Scatter(a.Passables, t["blood_stain"], x - 2, y - 2, 7, 6, 0, 0.12f, seed + 2);
    }

    private static void ScatterDebris(Arena a, Dictionary<string, TileBase> t, int x, int y, int w, int h, int seed)
    {
        Random.InitState(seed);
        string[] pool = { "crate_stack", "barrel_red", "rubble_pile" };
        int n = Mathf.Max(2, (w * h) / 22);
        for (int k = 0; k < n; k++)
        {
            SafeProp(a, t, pool[Random.Range(0, pool.Length)], Random.Range(x, x + w), Random.Range(y, y + h));
        }
        Scatter(a.Passables, t["trash_decal"], x, y, w, h, 0, 0.15f, seed + 1);
    }

    private static void DroppedTrail(Arena a, Dictionary<string, TileBase> t, int x0, int y0, int x1, int y1, int seed)
    {
        Random.InitState(seed);
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            float u = steps == 0 ? 0f : (float)i / steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, u)) + Random.Range(-1, 2);
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, u)) + Random.Range(-1, 2);
            if (Random.value < 0.7f)
            {
                Place(a.Passables, t["trash_decal"], x, y, 0);
            }
        }
    }

    private static void BloodTrail(Arena a, Dictionary<string, TileBase> t, int x0, int y0, int x1, int y1, int seed)
    {
        Random.InitState(seed);
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
        for (int i = 0; i <= steps; i++)
        {
            float u = steps == 0 ? 0f : (float)i / steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, u)) + Random.Range(-1, 2);
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, u)) + Random.Range(-1, 2);
            if (Random.value < 0.75f)
            {
                Place(a.Passables, t["blood_stain"], x, y, 0);
            }
        }
    }

    // ---------------------------------------------------------------- tilemap primitives

    private static void FillRect(Tilemap map, TileBase tile, int x, int y, int w, int h, int z)
    {
        for (int ix = x; ix < x + w; ix++)
        {
            for (int iy = y; iy < y + h; iy++)
            {
                map.SetTile(new Vector3Int(ix, iy, z), tile);
            }
        }
    }

    private static void Place(Tilemap map, TileBase tile, int x, int y, int z)
    {
        if (map != null && tile != null)
        {
            map.SetTile(new Vector3Int(x, y, z), tile);
        }
    }

    private static void Scatter(Tilemap map, TileBase tile, int x, int y, int w, int h, int z, float chance, int seed)
    {
        Random.InitState(seed);
        for (int ix = x; ix < x + w; ix++)
        {
            for (int iy = y; iy < y + h; iy++)
            {
                if (Random.value <= chance)
                {
                    map.SetTile(new Vector3Int(ix, iy, z), tile);
                }
            }
        }
    }

    private static void ScatterBlobs(Tilemap map, TileBase tile, int x, int y, int w, int h, int z, int blobs, int radius, float chance, int seed)
    {
        Random.InitState(seed);
        for (int b = 0; b < blobs; b++)
        {
            int cx = Random.Range(x, x + w);
            int cy = Random.Range(y, y + h);
            for (int ix = cx - radius; ix <= cx + radius; ix++)
            {
                for (int iy = cy - radius; iy <= cy + radius; iy++)
                {
                    if (ix < x || iy < y || ix >= x + w || iy >= y + h)
                    {
                        continue;
                    }
                    if ((ix - cx) * (ix - cx) + (iy - cy) * (iy - cy) > radius * radius)
                    {
                        continue;
                    }
                    if (Random.value <= chance)
                    {
                        map.SetTile(new Vector3Int(ix, iy, z), tile);
                    }
                }
            }
        }
    }

    private static void CompressAll(Arena a)
    {
        a.Base.CompressBounds();
        a.Shadows.CompressBounds();
        a.Cliffs.CompressBounds();
        a.Ramps.CompressBounds();
        a.Passables.CompressBounds();
        a.Colliders.CompressBounds();
    }

    // ---------------------------------------------------------------- actors: Level 1

    private static NavMetricsHud SpawnActors(Arena a, IsoNavGrid nav, Sprite unitSprite)
    {
        GameObject actors = new GameObject("Demo Actors");
        actors.transform.SetParent(a.Root.transform, false);

        GameObject player = MakeActor(actors, "Demo Player", unitSprite, new Color(0.31f, 0.76f, 0.97f));
        DemoPlayerController pc = player.AddComponent<DemoPlayerController>();
        pc.nav = nav;
        pc.useStartCell = true;
        pc.startCell = PlayerStart;
        player.transform.position = a.Grid.GetCellCenterWorld(PlayerStart);
        if (AttachCharacterSprites(player, "survivor", true))
        {
            player.GetComponent<SpriteRenderer>().color = new Color(0.78f, 0.88f, 1f);
        }
        SpriteDirectionalAnimator playerAnim = player.GetComponent<SpriteDirectionalAnimator>();
        if (playerAnim != null)
        {
            playerAnim.playerController = pc;
        }
        PlayerHealth health = player.AddComponent<PlayerHealth>();

        // Zombie 1: slow but relentless (walker stats), haunts the ruined SE complex.
        SpawnZombie(a, actors, nav, player, pc, health, unitSprite,
            "Zombie 1", "Zombie 1", new Vector3Int(50, 14, 0),
            new[]
            {
                new Vector3Int(42, 10, 0), new Vector3Int(58, 10, 0),
                new Vector3Int(58, 22, 0), new Vector3Int(42, 22, 0)
            },
            0.95f, 1.85f, 7f, 11f, 4f, Color.white);

        // Zombie 2: wanders the plaza (walker stats).
        SpawnZombie(a, actors, nav, player, pc, health, unitSprite,
            "Zombie 2", "Zombie 2", new Vector3Int(30, 14, 0),
            null,
            0.9f, 1.85f, 6.5f, 10f, 4f, new Color(0.9f, 1f, 0.9f));

        // Zombie 3: fast but short-sighted (runner stats), patrols the road.
        SpawnZombie(a, actors, nav, player, pc, health, unitSprite,
            "Zombie 3", "Zombie 3", new Vector3Int(58, 31, 0),
            new[] { new Vector3Int(4, 31, 0), new Vector3Int(59, 31, 0) },
            1.4f, 2.85f, 5.5f, 6f, 1.2f, new Color(1f, 0.8f, 0.72f));

        // Two escort targets: one patrols the rooftops, one hides inside the SW building.
        NpcController survivorA = SpawnSurvivor(a, actors, nav, player, pc, unitSprite,
            "Survivor A", new Vector3Int(16, 45, 1),
            new[]
            {
                new Vector3Int(12, 51, 2), new Vector3Int(20, 44, 1),
                new Vector3Int(33, 47, 1), new Vector3Int(45, 45, 1)
            },
            4.5f, 6f, 1.5f, 14f,
            new[]
            {
                "We BOTH need to reach the green zone - my friend is hiding in the big building south of the road.",
                "The extraction zone is on the far rooftop. The bridge connects them up here.",
                "Stay off the road if you can - something fast prowls it.",
                "Don't sprint near them - they hear it through walls.",
                "They have to use the ramps, same as us. High ground buys time."
            },
            new[]
            {
                "Agh - it BIT me! Keep them off me, please.",
                "I can take maybe one more hit... stay between me and them.",
                "I'm slowing down... the green zone, quickly."
            },
            new[]
            {
                "One more bite and I'm done... please, the zone, NOW.",
                "I can barely stand... don't let a single one near me.",
                "If I fall, leave-- no. Don't. Just get me there."
            });

        NpcController survivorB = SpawnSurvivor(a, actors, nav, player, pc, unitSprite,
            "Survivor B", new Vector3Int(9, 12, 0),
            new[] { new Vector3Int(9, 12, 0), new Vector3Int(12, 20, 0) },
            4.5f, 6f, 1.5f, 14f,
            new[]
            {
                "Get us BOTH to the green zone - my friend is up on the north-west rooftop.",
                "The ones out there are slow, but once they see you they don't stop.",
                "Something faster patrols the road. Cross it quickly and quietly.",
                "Walk, don't run - running brings them through the walls."
            },
            new[]
            {
                "It got me... I can't outrun them like this.",
                "I'm hurt - watch the corners, they came out of nowhere.",
                "Two of three... I don't like those odds."
            },
            new[]
            {
                "I'm bleeding out... one more and it's over.",
                "Get me to the zone... I can't take another hit.",
                "Don't stop for anything... please."
            });

        FinishLevel(a, nav, pc, health, new[] { survivorA, survivorB }, L1Extraction, out NavMetricsHud metrics);
        return metrics;
    }

    // ---------------------------------------------------------------- actors: Level 2

    private static NavMetricsHud SpawnHordeActors(Arena a, IsoNavGrid nav, Sprite unitSprite)
    {
        GameObject actors = new GameObject("Demo Actors");
        actors.transform.SetParent(a.Root.transform, false);

        GameObject player = MakeActor(actors, "Demo Player", unitSprite, new Color(0.31f, 0.76f, 0.97f));
        DemoPlayerController pc = player.AddComponent<DemoPlayerController>();
        pc.nav = nav;
        pc.useStartCell = true;
        pc.startCell = MazePlayerStart;
        player.transform.position = a.Grid.GetCellCenterWorld(MazePlayerStart);
        if (AttachCharacterSprites(player, "survivor", true))
        {
            player.GetComponent<SpriteRenderer>().color = new Color(0.78f, 0.88f, 1f);
        }
        SpriteDirectionalAnimator playerAnim = player.GetComponent<SpriteDirectionalAnimator>();
        if (playerAnim != null)
        {
            playerAnim.playerController = pc;
        }
        PlayerHealth health = player.AddComponent<PlayerHealth>();

        // The coordinating horde brain (flow field + interception + learning).
        GameObject dirGo = new GameObject("Horde Director");
        dirGo.transform.SetParent(a.Root.transform, false);
        HordeDirector director = dirGo.AddComponent<HordeDirector>();
        director.nav = nav;
        director.player = player.transform;
        director.playerController = pc;

        Vector3Int[] starts =
        {
            new Vector3Int(7, 105, 0), new Vector3Int(112, 8, 0), new Vector3Int(60, 8, 0),
            new Vector3Int(60, 112, 0), new Vector3Int(25, 25, 0), new Vector3Int(95, 95, 0)
        };
        for (int i = 0; i < starts.Length; i++)
        {
            SpawnHordeZombie(a, actors, nav, director, player, pc, health, unitSprite, "Zombie " + (i + 1), starts[i]);
        }

        // Two escort targets hidden in opposite corners - collect both before extraction.
        NpcController survivorA = SpawnSurvivor(a, actors, nav, player, pc, unitSprite,
            "Survivor A", L2SurvivorStart,
            new[] { new Vector3Int(6, 6, 0), new Vector3Int(10, 10, 0) },
            6f, 4.5f, 1.0f, 15f,
            new[]
            {
                "There's another survivor hiding somewhere in the north - we ALL leave together.",
                "The green extraction zone is in the far north-east corner.",
                "These ones hunt as a pack. If it goes quiet, they're circling you.",
                "Don't take the same corridor twice - I swear they remember."
            },
            new[]
            {
                "It bit me... they're herding us, I know it.",
                "I'm hurt - we move smarter now, not faster.",
                "One pack nearly had me. Don't let them close again."
            },
            new[]
            {
                "One more bite ends me... don't let them circle us.",
                "Everything's spinning... north-east corner, hurry.",
                "If they corner us now... just don't stop moving."
            });

        NpcController survivorB = SpawnSurvivor(a, actors, nav, player, pc, unitSprite,
            "Survivor B", new Vector3Int(33, 101, 0),
            new[] { new Vector3Int(32, 100, 0), new Vector3Int(35, 103, 0) },
            6f, 4.5f, 1.0f, 15f,
            new[]
            {
                "My friend is hiding in the south-west corner - don't leave them behind!",
                "Extraction is the green zone north-east of here. So close...",
                "They cut off escape routes. Always keep a second way out in mind.",
                "Walk unless you have to run - the whole maze hears running."
            },
            new[]
            {
                "I'm bit! They knew where I'd run...",
                "Hurting bad - don't lead them past me twice.",
                "I can still walk. Barely. Keep going."
            },
            new[]
            {
                "I won't survive another one... please, the green zone.",
                "So close to extraction... don't let me die here.",
                "My legs are giving out... stay close to me."
            });

        FinishLevel(a, nav, pc, health, new[] { survivorA, survivorB }, L2Extraction, out NavMetricsHud metrics);
        metrics.director = director;
        return metrics;
    }

    /// <summary>Shared tail: objective wiring + metrics HUD.</summary>
    private static void FinishLevel(Arena a, IsoNavGrid nav, DemoPlayerController pc, PlayerHealth health,
        NpcController[] survivors, Vector3Int extraction, out NavMetricsHud metrics)
    {
        GameObject objGo = new GameObject("Level Objective");
        objGo.transform.SetParent(a.Root.transform, false);
        LevelObjective objective = objGo.AddComponent<LevelObjective>();
        objective.nav = nav;
        objective.player = pc;
        objective.playerHealth = health;
        objective.survivors = survivors;
        objective.extractionCell = extraction;
        // Wider than the survivors' follow distance, so standing at the centre always
        // pulls the following survivors inside too.
        objective.zoneRadius = 3f;

        // Screen arrows pointing at survivors who have been away from the player a while:
        // first pulse after 10s of separation, visible 5s, then 10s between pulses.
        SurvivorArrowIndicator arrows = objGo.AddComponent<SurvivorArrowIndicator>();
        arrows.player = pc;
        arrows.survivors = survivors;
        arrows.objective = objective;
        arrows.awayDelay = 10f;
        arrows.showDuration = 5f;
        arrows.cooldown = 10f;

        GameObject hud = new GameObject("Demo HUD");
        hud.transform.SetParent(a.Root.transform, false);
        metrics = hud.AddComponent<NavMetricsHud>();
        metrics.nav = nav;
        metrics.playerHealth = health;
    }

    /// <summary>Spawns one escort-target survivor with per-level nerves and health-tiered dialogue.
    /// Movement speeds mirror the player exactly (walk pace following, sprint pace when
    /// fleeing or catching up), and survivors hold anchored at their spawn until found.</summary>
    private static NpcController SpawnSurvivor(Arena a, GameObject actors, IsoNavGrid nav, GameObject player,
        DemoPlayerController pc, Sprite unitSprite, string name, Vector3Int start, Vector3Int[] patrolCells,
        float engageRange, float panicRange, float calmDelay, float leashRange,
        string[] hints, string[] hurtHints, string[] criticalHints)
    {
        GameObject survivor = MakeActor(actors, name, unitSprite, new Color(0.40f, 0.73f, 0.42f));
        NpcController sc = survivor.AddComponent<NpcController>();
        sc.behavior = NpcController.NpcBehavior.Civilian;
        sc.displayName = name;
        sc.nav = nav;
        sc.player = player.transform;
        sc.playerController = pc;
        sc.useStartCell = true;
        sc.startCell = start;
        sc.followSpeed = pc.walkSpeed;      // Keeps step with a walking player - no lag, no rubber-banding.
        sc.activeSpeed = pc.sprintSpeed;    // Flee and catch-up run at the player's sprint pace.
        sc.patrolSpeed = 1.2f;
        sc.anchoredUntilContact = true;
        sc.panicRange = panicRange;
        sc.calmDelay = calmDelay;
        sc.followPlayerWhenSafe = true;
        sc.followEngageRange = engageRange;
        sc.followDistance = 1.2f;
        sc.followLeashRange = leashRange;
        sc.hintLines = hints;
        sc.hintLinesHurt = hurtHints;
        sc.hintLinesCritical = criticalHints;
        if (patrolCells != null && patrolCells.Length > 0)
        {
            sc.patrolPoints = MakePatrol(actors, a.Grid, name + " Patrol", patrolCells);
        }
        survivor.transform.position = a.Grid.GetCellCenterWorld(start);
        AttachCharacterSprites(survivor, "survivor", true);
        return sc;
    }

    private static void SpawnZombie(Arena a, GameObject actors, IsoNavGrid nav, GameObject player,
        DemoPlayerController pc, PlayerHealth health, Sprite unitSprite, string name, string displayName,
        Vector3Int start, Vector3Int[] patrolCells, float patrolSpeed, float activeSpeed,
        float sightRange, float hearingRange, float loseSightGrace, Color spriteTint)
    {
        GameObject zombie = MakeActor(actors, name, unitSprite, new Color(0.90f, 0.22f, 0.21f));
        NpcController c = zombie.AddComponent<NpcController>();
        c.behavior = NpcController.NpcBehavior.Zombie;
        c.displayName = displayName;
        c.nav = nav;
        c.player = player.transform;
        c.playerController = pc;
        c.playerHealth = health;
        c.useStartCell = true;
        c.startCell = start;
        c.patrolSpeed = patrolSpeed;
        c.activeSpeed = activeSpeed;
        c.sightRange = sightRange;
        c.hearingRange = hearingRange;
        c.loseSightGrace = loseSightGrace;
        if (patrolCells != null && patrolCells.Length > 0)
        {
            c.patrolPoints = MakePatrol(actors, a.Grid, name + " Patrol", patrolCells);
        }
        zombie.transform.position = a.Grid.GetCellCenterWorld(start);
        if (AttachCharacterSprites(zombie, "zombie", true))
        {
            zombie.GetComponent<SpriteRenderer>().color = spriteTint;
        }
    }

    private static void SpawnHordeZombie(Arena a, GameObject actors, IsoNavGrid nav, HordeDirector director,
        GameObject player, DemoPlayerController pc, PlayerHealth health, Sprite unitSprite, string name, Vector3Int start)
    {
        GameObject zombie = MakeActor(actors, name, unitSprite, new Color(0.90f, 0.22f, 0.21f));
        HordeZombieController c = zombie.AddComponent<HordeZombieController>();
        c.nav = nav;
        c.director = director;
        c.player = player.transform;
        c.playerController = pc;
        c.playerHealth = health;
        c.displayName = name;
        c.useStartCell = true;
        c.startCell = start;
        zombie.transform.position = a.Grid.GetCellCenterWorld(start);
        AttachCharacterSprites(zombie, "zombie", true);
    }

    private static GameObject MakeActor(GameObject parent, string name, Sprite sprite, Color tint)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = tint;
        go.AddComponent<YSorter>();
        return go;
    }

    private static Transform[] MakePatrol(GameObject parent, Grid grid, string name, Vector3Int[] cells)
    {
        GameObject group = new GameObject(name);
        group.transform.SetParent(parent.transform, false);
        Transform[] points = new Transform[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            GameObject p = new GameObject(name + "_" + i);
            p.transform.SetParent(group.transform, false);
            p.transform.position = grid.GetCellCenterWorld(cells[i]);
            points[i] = p.transform;
        }
        return points;
    }

    private static bool AttachCharacterSprites(GameObject actor, string character, bool addAnimator)
    {
        Sprite[] idleFront = LoadFrames(character + "_idle", "f");
        if (idleFront.Length == 0)
        {
            return false;
        }

        SpriteRenderer sr = actor.GetComponent<SpriteRenderer>();
        sr.sprite = idleFront[0];
        sr.color = Color.white;

        if (!addAnimator)
        {
            return true;
        }
        SpriteDirectionalAnimator anim = actor.AddComponent<SpriteDirectionalAnimator>();
        anim.npc = actor.GetComponent<NpcController>();
        anim.idleFront = idleFront;
        anim.idleRear = LoadFrames(character + "_idle", "r");
        anim.walkFront = LoadFrames(character + "_walk", "f");
        anim.walkRear = LoadFrames(character + "_walk", "r");
        anim.runFront = LoadFrames(character + "_run", "f");
        anim.runRear = LoadFrames(character + "_run", "r");
        anim.attackFront = LoadFrames(character + "_attack", "f");
        anim.attackRear = LoadFrames(character + "_attack", "r");
        anim.deathFront = LoadFrames(character + "_death", "f");
        anim.deathRear = LoadFrames(character + "_death", "r");
        return true;
    }

    private static Sprite[] LoadFrames(string sheetName, string rowSuffix)
    {
        string path = "Assets/Tiles/Sprites/Jay/NpcDemo/Characters/" + sheetName + ".png";
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
        List<Sprite> frames = new List<Sprite>();
        string prefix = sheetName + "_" + rowSuffix;
        for (int i = 0; i < assets.Length; i++)
        {
            Sprite s = assets[i] as Sprite;
            if (s != null && s.name.StartsWith(prefix))
            {
                frames.Add(s);
            }
        }
        frames.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
        return frames.ToArray();
    }

    // ---------------------------------------------------------------- game scene

    /// <summary>Creates (or replaces) the single Game scene with a camera + GameManager and opens it.</summary>
    private static void CreateGameScene()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
        {
            AssetDatabase.CreateFolder("Assets", "Scenes");
        }

        UnityEngine.SceneManagement.Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        Camera cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 7f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
        camGo.transform.position = new Vector3(0f, 0f, -10f);
        camGo.AddComponent<AudioListener>();

        GameObject gm = new GameObject("GameManager");
        gm.AddComponent<GameManager>();

        string scenePath = "Assets/Scenes/NpcDemoGame.unity";
        EditorSceneManager.SaveScene(scene, scenePath);

        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == scenePath);
        scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
#endif
