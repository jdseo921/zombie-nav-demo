using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Single-scene game shell for the demo. Draws the main menu, settings, pause menu and
/// win/fail overlays with OnGUI (robust in editor and builds alike), and loads each
/// level's arena prefab at runtime from Resources.
///
/// - Main menu: Level 1 (easy escort, independent A*), Level 2 (hard maze, coordinated
///   flow-field horde), Settings, Quit.
/// - Settings (from menu or pause): resolution, fullscreen, on-screen text toggles.
/// - In game: ESC toggles the pause menu (Resume / Restart / Settings / Main Menu).
/// - Win: player + survivor reach the extraction zone (LevelObjective). Fail: player
///   dies. Both freeze the game and offer Restart / Main Menu.
///
/// The level prefabs are self-contained (arena + actors + HUD + objective); this shell
/// only wires the camera's DemoCameraFollow to the instantiated player.
/// </summary>
public class GameManager : MonoBehaviour
{
    private enum State { Menu, Playing, Paused, Settings, Won, Failed }

    [Tooltip("Resources paths (no extension) of the two level prefabs.")]
    public string level1Resource = "NpcDemo/Level1";
    public string level2Resource = "NpcDemo/Level2";

    private State state = State.Menu;
    private State settingsReturn = State.Menu;
    private GameObject levelInstance;
    private LevelObjective objective;
    private string currentResource;
    private Camera cam;

    private Resolution[] resolutions;
    private int resolutionIndex;
    private bool fullscreen;
    private Vector2 settingsScroll;
    private bool resolutionDropdownOpen;

    private GUIStyle titleStyle;
    private GUIStyle bigTitleStyle;
    private GUIStyle labelStyle;
    private GUIStyle panelStyle;
    private Texture2D panelTexture;
    private float vw, vh;   // Virtual (scale-adjusted) screen size for layout.

    private void Start()
    {
        cam = Camera.main;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        cam.orthographic = true;
        cam.backgroundColor = new Color(0.05f, 0.06f, 0.08f);

        BuildResolutionList();
        fullscreen = Screen.fullScreen;
        Time.timeScale = 1f;
    }

    private void BuildResolutionList()
    {
        // Distinct width x height (ignore refresh-rate duplicates).
        var list = new System.Collections.Generic.List<Resolution>();
        Resolution[] all = Screen.resolutions;
        for (int i = 0; i < all.Length; i++)
        {
            bool dup = false;
            for (int j = 0; j < list.Count; j++)
            {
                if (list[j].width == all[i].width && list[j].height == all[i].height) { dup = true; break; }
            }
            if (!dup) { list.Add(all[i]); }
        }
        if (list.Count == 0)
        {
            list.Add(new Resolution { width = Screen.width, height = Screen.height });
        }
        resolutions = list.ToArray();
        for (int i = 0; i < resolutions.Length; i++)
        {
            if (resolutions[i].width == Screen.width && resolutions[i].height == Screen.height)
            {
                resolutionIndex = i;
                break;
            }
        }
    }

    private void Update()
    {
        // End-of-level detection.
        if (state == State.Playing && objective != null)
        {
            if (objective.Outcome == LevelObjective.State.Won)
            {
                state = State.Won;
                Time.timeScale = 0f;
            }
            else if (objective.Outcome == LevelObjective.State.Failed)
            {
                state = State.Failed;
                Time.timeScale = 0f;
            }
        }

        Keyboard kb = Keyboard.current;
        if (kb == null)
        {
            return;
        }
        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (state == State.Playing) { SetPaused(true); }
            else if (state == State.Paused) { SetPaused(false); }
            else if (state == State.Settings && settingsReturn == State.Paused) { state = State.Paused; }
        }
    }

    private void SetPaused(bool paused)
    {
        state = paused ? State.Paused : State.Playing;
        Time.timeScale = paused ? 0f : 1f;
    }

    // ------------------------------------------------------------ level loading

    private void LoadLevel(string resourcePath)
    {
        UnloadLevel();
        GameObject prefab = Resources.Load<GameObject>(resourcePath);
        if (prefab == null)
        {
            Debug.LogError("GameManager: level prefab not found at Resources/" + resourcePath + ". Run Tools > CP5030 > Setup NPC Demo Arena.");
            return;
        }
        currentResource = resourcePath;
        levelInstance = Instantiate(prefab);
        objective = levelInstance.GetComponentInChildren<LevelObjective>();

        DemoPlayerController player = levelInstance.GetComponentInChildren<DemoPlayerController>();
        DemoCameraFollow follow = cam.GetComponent<DemoCameraFollow>();
        if (follow == null) { follow = cam.gameObject.AddComponent<DemoCameraFollow>(); }
        if (player != null)
        {
            follow.target = player.transform;
            cam.transform.position = player.transform.position + new Vector3(0f, 0f, -10f);
        }

        Time.timeScale = 1f;
        state = State.Playing;
    }

    private void UnloadLevel()
    {
        if (levelInstance != null)
        {
            Destroy(levelInstance);
            levelInstance = null;
        }
        objective = null;
    }

    private void RestartLevel()
    {
        if (!string.IsNullOrEmpty(currentResource))
        {
            LoadLevel(currentResource);
        }
        else
        {
            ReturnToMenu();
        }
    }

    private void ReturnToMenu()
    {
        UnloadLevel();
        Time.timeScale = 1f;
        state = State.Menu;
    }

    // ------------------------------------------------------------ GUI

    private void EnsureStyles()
    {
        if (titleStyle == null)
        {
            titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }
        if (bigTitleStyle == null)
        {
            bigTitleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        }
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }
        if (panelTexture == null)
        {
            // Near-opaque backdrop so menus and end screens read clearly over the arena.
            panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            panelTexture.SetPixel(0, 0, new Color(0.09f, 0.10f, 0.14f, 0.97f));
            panelTexture.Apply();
        }
        if (panelStyle == null)
        {
            panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.padding = new RectOffset(28, 28, 24, 24);
        }
        panelStyle.normal.background = panelTexture;
        // Base sizes here are in virtual pixels; the GUI.matrix scale enlarges them.
        titleStyle.fontSize = 24;
        bigTitleStyle.fontSize = 32;
        labelStyle.fontSize = 16;
    }

    private void OnGUI()
    {
        EnsureStyles();

        float s = DemoUI.Scale;
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        vw = Screen.width / s;
        vh = Screen.height / s;
        GUI.skin.label.fontSize = 16;
        GUI.skin.button.fontSize = 16;
        GUI.skin.toggle.fontSize = 15;

        switch (state)
        {
            case State.Menu: DrawMainMenu(); break;
            case State.Paused: DrawPauseMenu(); break;
            case State.Settings: DrawSettings(); break;
            case State.Won: DrawEndScreen(true); break;
            case State.Failed: DrawEndScreen(false); break;
        }

        GUI.matrix = previous;
    }

    private Rect CenterRect(float w, float h)
    {
        return new Rect((vw - w) * 0.5f, (vh - h) * 0.5f, w, h);
    }

    private void DrawMainMenu()
    {
        GUILayout.BeginArea(CenterRect(420, 470), panelStyle);
        GUILayout.Space(6);
        GUILayout.Label("ZOMBIE NAV DEMO", titleStyle);
        GUILayout.Space(10);
        GUILayout.Label("Escort BOTH survivors to the extraction zone. Dying restarts the level.", labelStyle);
        GUILayout.Space(14);
        GUILayout.Label("Level 1 (easy) - open campus, independent A* zombies", labelStyle);
        GUILayout.Space(4);
        if (GUILayout.Button("Play Level 1", GUILayout.Height(44)))
        {
            LoadLevel(level1Resource);
        }
        GUILayout.Space(12);
        GUILayout.Label("Level 2 (hard) - maze; a pack that hunts probabilistically, searches where you might be, and learns your routes", labelStyle);
        GUILayout.Space(4);
        if (GUILayout.Button("Play Level 2", GUILayout.Height(44)))
        {
            LoadLevel(level2Resource);
        }
        GUILayout.Space(18);
        if (GUILayout.Button("Settings", GUILayout.Height(36)))
        {
            settingsReturn = State.Menu;
            state = State.Settings;
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Quit", GUILayout.Height(36)))
        {
            Application.Quit();
        }
        GUILayout.EndArea();
    }

    private void DrawPauseMenu()
    {
        GUILayout.BeginArea(CenterRect(340, 360), panelStyle);
        GUILayout.Space(6);
        GUILayout.Label("PAUSED", titleStyle);
        GUILayout.Space(14);
        if (GUILayout.Button("Resume", GUILayout.Height(42)))
        {
            SetPaused(false);
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Restart Level", GUILayout.Height(42)))
        {
            RestartLevel();
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Settings", GUILayout.Height(42)))
        {
            settingsReturn = State.Paused;
            state = State.Settings;
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Main Menu", GUILayout.Height(42)))
        {
            ReturnToMenu();
        }
        GUILayout.EndArea();
    }

    private void DrawEndScreen(bool won)
    {
        string title = won ? "EXTRACTED!" : (objective != null ? objective.FailTitle : "YOU DIED");
        string message = won
            ? "You reached the extraction zone with every survivor. Level complete."
            : (objective != null ? objective.FailMessage : "The horde got you.");
        GUILayout.BeginArea(CenterRect(480, 380), panelStyle);
        GUILayout.Space(14);
        GUILayout.Label(title, bigTitleStyle);
        GUILayout.Space(16);
        GUILayout.Label(message, labelStyle);
        GUILayout.Space(22);
        if (GUILayout.Button(won ? "Play Again" : "Restart Level", GUILayout.Height(46)))
        {
            RestartLevel();
        }
        GUILayout.Space(8);
        if (GUILayout.Button("Main Menu", GUILayout.Height(46)))
        {
            ReturnToMenu();
        }
        GUILayout.EndArea();
    }

    private void DrawSettings()
    {
        // Panel height is clamped to the window and the middle section scrolls, so the
        // menu can never flow out of view no matter how many resolutions the GPU lists.
        float panelH = Mathf.Min(resolutionDropdownOpen ? 500f : 350f, vh - 12f);
        GUILayout.BeginArea(CenterRect(440, panelH), panelStyle);
        GUILayout.Space(6);
        GUILayout.Label("SETTINGS", titleStyle);
        GUILayout.Space(10);

        settingsScroll = GUILayout.BeginScrollView(settingsScroll);

        GUILayout.Label("Resolution");
        Resolution current = resolutions[Mathf.Clamp(resolutionIndex, 0, resolutions.Length - 1)];
        string face = current.width + " x " + current.height + (resolutionDropdownOpen ? "   ^" : "   v");
        if (GUILayout.Button(face, GUILayout.Height(36)))
        {
            resolutionDropdownOpen = !resolutionDropdownOpen;
        }
        if (resolutionDropdownOpen)
        {
            for (int i = 0; i < resolutions.Length; i++)
            {
                string label = resolutions[i].width + " x " + resolutions[i].height +
                               (i == resolutionIndex ? "   <" : "");
                if (GUILayout.Button(label, GUILayout.Height(28)))
                {
                    resolutionIndex = i;
                    resolutionDropdownOpen = false;
                }
            }
            GUILayout.Space(6);
        }

        GUILayout.Space(8);
        fullscreen = GUILayout.Toggle(fullscreen, " Fullscreen");
        GUILayout.Space(10);
        GUILayout.Label("Tip: the OBJECTIVE, GAME INFO and CONTROLS panels can be minimized" +
                        " in-game with the - / + button on each panel.", labelStyle);

        GUILayout.EndScrollView();

        GUILayout.Space(10);
        if (GUILayout.Button("Apply", GUILayout.Height(40)))
        {
            Resolution r = resolutions[Mathf.Clamp(resolutionIndex, 0, resolutions.Length - 1)];
            Screen.SetResolution(r.width, r.height, fullscreen);
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Back", GUILayout.Height(40)))
        {
            resolutionDropdownOpen = false;
            state = settingsReturn;
        }
        GUILayout.EndArea();
    }
}
