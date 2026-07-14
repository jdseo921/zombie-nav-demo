#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

/// <summary>
/// Imports the AI-generated zombie/survivor sheets from &lt;project&gt;/SpriteStaging/ into
/// sliced, game-ready sprites in Assets/Tiles/Sprites/Jay/NpcDemo/Characters/.
///
/// The generated PNGs have no alpha channel - the checkerboard "transparency" is baked
/// into the pixels. This importer:
/// 1. Keys the checkerboard out (flood fill from the image border, so enclosed light
///    pixels like the zombie's white eyes survive).
/// 2. Detects each character frame as a connected pixel island.
/// 3. Groups islands into rows: top row = front view (_f#), bottom row = rear view (_r#).
///    Single-row "idle" sheets with two islands are treated as [front, rear].
/// 4. Slices the texture with bottom-center pivots and a pixels-per-unit chosen so every
///    character stands about 1.4 world units tall (fits the 1 x 0.5 iso cells).
///
/// Expected staging files: zombie_idle, zombie_walk, zombie_attack, zombie_death,
/// survivor_idle, survivor_walk, survivor_run (.png). Extra files are processed too.
/// </summary>
public static class JayNpcSpriteImporter
{
    private const string StagingFolder = "SpriteStaging";
    private const string OutputFolder = "Assets/Tiles/Sprites/Jay/NpcDemo/Characters";
    private const float TargetWorldHeight = 1.4f;

    public static void ProcessAll()
    {
        if (!Directory.Exists(StagingFolder))
        {
            Debug.LogError("JayNpcSpriteImporter: staging folder not found at <project>/" + StagingFolder + "/. Put the raw PNGs there first.");
            return;
        }

        EnsureFolder("Assets/Tiles", "Sprites");
        EnsureFolder("Assets/Tiles/Sprites", "Jay");
        EnsureFolder("Assets/Tiles/Sprites/Jay", "NpcDemo");
        EnsureFolder("Assets/Tiles/Sprites/Jay/NpcDemo", "Characters");

        string[] files = Directory.GetFiles(StagingFolder, "*.png");
        if (files.Length == 0)
        {
            Debug.LogError("JayNpcSpriteImporter: no PNG files in " + StagingFolder + "/.");
            return;
        }

        int done = 0;
        foreach (string file in files)
        {
            try
            {
                if (ProcessOne(file))
                {
                    done++;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("JayNpcSpriteImporter: failed on " + file + " - " + e.Message);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("JayNpcSpriteImporter: processed " + done + "/" + files.Length + " sheets into " + OutputFolder + ". Now rebuild the demo arena to wire them onto the actors.");
    }

    private static bool ProcessOne(string filePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(File.ReadAllBytes(filePath));

        Color32[] pixels = tex.GetPixels32();
        int w = tex.width;
        int h = tex.height;

        RemoveCheckerBackground(pixels, w, h);

        List<RectInt> islands = FindIslands(pixels, w, h);
        if (islands.Count == 0)
        {
            Debug.LogWarning("JayNpcSpriteImporter: no character islands found in " + baseName + " - skipped.");
            Object.DestroyImmediate(tex);
            return false;
        }

        // Write the cleaned RGBA texture into the project.
        Texture2D outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        outTex.SetPixels32(pixels);
        outTex.Apply();
        string assetPath = OutputFolder + "/" + baseName + ".png";
        File.WriteAllBytes(assetPath, outTex.EncodeToPNG());
        Object.DestroyImmediate(outTex);
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        // Frame naming: rows -> front/rear.
        List<SpriteRect> spriteRects = BuildSpriteRects(baseName, islands, h);

        // Import settings: PPU so the tallest frame is ~1.4 world units.
        int tallest = 1;
        for (int i = 0; i < islands.Count; i++)
        {
            tallest = Mathf.Max(tallest, islands[i].height);
        }
        float ppu = Mathf.Round(tallest / TargetWorldHeight);

        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit = ppu;
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        importer.maxTextureSize = 4096;

        var factory = new SpriteDataProviderFactories();
        factory.Init();
        ISpriteEditorDataProvider provider = factory.GetSpriteEditorDataProviderFromObject(importer);
        provider.InitSpriteEditorDataProvider();
        provider.SetSpriteRects(spriteRects.ToArray());
        var nameIdProvider = provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
        if (nameIdProvider != null)
        {
            var pairs = new List<SpriteNameFileIdPair>();
            for (int i = 0; i < spriteRects.Count; i++)
            {
                pairs.Add(new SpriteNameFileIdPair(spriteRects[i].name, spriteRects[i].spriteID));
            }
            nameIdProvider.SetNameFileIdPairs(pairs);
        }
        provider.Apply();
        importer.SaveAndReimport();

        Debug.Log("JayNpcSpriteImporter: " + baseName + " -> " + spriteRects.Count + " frames, PPU " + ppu + ".");
        return true;
    }

    // ---------------------------------------------------------------- background keying

    /// <summary>Flood-fills checker-pattern pixels from the border and clears them.</summary>
    private static void RemoveCheckerBackground(Color32[] px, int w, int h)
    {
        bool[] clear = new bool[px.Length];
        Queue<int> queue = new Queue<int>();

        for (int x = 0; x < w; x++)
        {
            TryEnqueue(px, clear, queue, x, w);                    // Bottom row.
            TryEnqueue(px, clear, queue, (h - 1) * w + x, w);      // Top row.
        }
        for (int y = 0; y < h; y++)
        {
            TryEnqueue(px, clear, queue, y * w, w);                // Left column.
            TryEnqueue(px, clear, queue, y * w + w - 1, w);        // Right column.
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w;
            int y = i / w;
            if (x > 0) { TryEnqueue(px, clear, queue, i - 1, w); }
            if (x < w - 1) { TryEnqueue(px, clear, queue, i + 1, w); }
            if (y > 0) { TryEnqueue(px, clear, queue, i - w, w); }
            if (y < h - 1) { TryEnqueue(px, clear, queue, i + w, w); }
        }

        // Two relaxed dilation passes to eat the anti-aliased halo around the outline.
        for (int pass = 0; pass < 2; pass++)
        {
            List<int> extra = new List<int>();
            for (int i = 0; i < px.Length; i++)
            {
                if (clear[i] || !IsBackgroundish(px[i], 190, 40))
                {
                    continue;
                }
                int x = i % w;
                int y = i / w;
                bool nextToCleared =
                    (x > 0 && clear[i - 1]) || (x < w - 1 && clear[i + 1]) ||
                    (y > 0 && clear[i - w]) || (y < h - 1 && clear[i + w]);
                if (nextToCleared)
                {
                    extra.Add(i);
                }
            }
            for (int e = 0; e < extra.Count; e++)
            {
                clear[extra[e]] = true;
            }
        }

        Color32 transparent = new Color32(0, 0, 0, 0);
        for (int i = 0; i < px.Length; i++)
        {
            if (clear[i])
            {
                px[i] = transparent;
            }
        }
    }

    private static void TryEnqueue(Color32[] px, bool[] clear, Queue<int> queue, int i, int w)
    {
        if (!clear[i] && IsBackgroundish(px[i], 205, 22))
        {
            clear[i] = true;
            queue.Enqueue(i);
        }
    }

    /// <summary>Bright, low-saturation pixel - the checkerboard's two shades.</summary>
    private static bool IsBackgroundish(Color32 c, int minChannel, int maxSpread)
    {
        int min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        int max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        return min >= minChannel && (max - min) <= maxSpread;
    }

    // ---------------------------------------------------------------- island detection

    private static List<RectInt> FindIslands(Color32[] px, int w, int h)
    {
        bool[] visited = new bool[px.Length];
        List<RectInt> islands = new List<RectInt>();
        int minArea = Mathf.Max(1500, (w * h) / 500);
        Queue<int> queue = new Queue<int>();

        for (int i = 0; i < px.Length; i++)
        {
            if (visited[i] || px[i].a < 16)
            {
                continue;
            }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int area = 0;
            visited[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                int x = p % w;
                int y = p / w;
                area++;
                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        {
                            continue;
                        }
                        int n = ny * w + nx;
                        if (!visited[n] && px[n].a >= 16)
                        {
                            visited[n] = true;
                            queue.Enqueue(n);
                        }
                    }
                }
            }
            if (area >= minArea)
            {
                islands.Add(new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1));
            }
        }

        // Merge islands whose rects (slightly expanded) overlap - detached hands etc.
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < islands.Count && !merged; a++)
            {
                for (int b = a + 1; b < islands.Count && !merged; b++)
                {
                    RectInt ra = Expand(islands[a], 10);
                    RectInt rb = islands[b];
                    if (ra.Overlaps(rb))
                    {
                        islands[a] = Union(islands[a], islands[b]);
                        islands.RemoveAt(b);
                        merged = true;
                    }
                }
            }
        }
        return islands;
    }

    private static RectInt Expand(RectInt r, int amount)
    {
        return new RectInt(r.x - amount, r.y - amount, r.width + amount * 2, r.height + amount * 2);
    }

    private static RectInt Union(RectInt a, RectInt b)
    {
        int minX = Mathf.Min(a.xMin, b.xMin);
        int minY = Mathf.Min(a.yMin, b.yMin);
        int maxX = Mathf.Max(a.xMax, b.xMax);
        int maxY = Mathf.Max(a.yMax, b.yMax);
        return new RectInt(minX, minY, maxX - minX, maxY - minY);
    }

    // ---------------------------------------------------------------- slicing

    private static List<SpriteRect> BuildSpriteRects(string baseName, List<RectInt> islands, int texHeight)
    {
        // Texture y=0 is the BOTTOM, so the "front" row (visually on top) has larger y.
        float minCenter = float.MaxValue;
        float maxCenter = float.MinValue;
        for (int i = 0; i < islands.Count; i++)
        {
            float cy = islands[i].y + islands[i].height * 0.5f;
            if (cy < minCenter) { minCenter = cy; }
            if (cy > maxCenter) { maxCenter = cy; }
        }
        bool twoRows = (maxCenter - minCenter) > texHeight * 0.22f && islands.Count > 2;
        float split = (maxCenter + minCenter) * 0.5f;

        List<RectInt> front = new List<RectInt>();
        List<RectInt> rear = new List<RectInt>();
        if (twoRows)
        {
            for (int i = 0; i < islands.Count; i++)
            {
                float cy = islands[i].y + islands[i].height * 0.5f;
                (cy >= split ? front : rear).Add(islands[i]);
            }
        }
        else if (islands.Count == 2 && baseName.Contains("idle"))
        {
            islands.Sort((a, b) => a.x.CompareTo(b.x));
            front.Add(islands[0]);
            rear.Add(islands[1]);
        }
        else
        {
            front.AddRange(islands);
        }
        front.Sort((a, b) => a.x.CompareTo(b.x));
        rear.Sort((a, b) => a.x.CompareTo(b.x));

        List<SpriteRect> rects = new List<SpriteRect>();
        AddRects(rects, front, baseName + "_f");
        AddRects(rects, rear, baseName + "_r");
        return rects;
    }

    private static void AddRects(List<SpriteRect> rects, List<RectInt> islands, string prefix)
    {
        for (int i = 0; i < islands.Count; i++)
        {
            SpriteRect sr = new SpriteRect();
            sr.name = prefix + i;
            sr.spriteID = GUID.Generate();
            sr.rect = new Rect(islands[i].x, islands[i].y, islands[i].width, islands[i].height);
            sr.alignment = SpriteAlignment.Custom;
            sr.pivot = new Vector2(0.5f, 0.04f);   // Feet just above the frame bottom.
            rects.Add(sr);
        }
    }

    private static void EnsureFolder(string parent, string child)
    {
        string full = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(full))
        {
            AssetDatabase.CreateFolder(parent, child);
        }
    }
}
#endif
