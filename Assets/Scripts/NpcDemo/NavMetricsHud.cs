using UnityEngine;

/// <summary>
/// On-screen game-info overlay, matching the paper's pathfinding evaluation criteria
/// (path success, path compute cost, stuck cases, frame performance) plus player HP.
/// Layout: GAME INFO panel top-right with the CONTROLS panel stacked beneath it; both
/// have window-style minimize/expand buttons (DemoUI.PanelHeader). The OBJECTIVE panel
/// (top-left) lives in LevelObjective. Screenshot the info panel for the report.
/// </summary>
public class NavMetricsHud : MonoBehaviour
{
    public IsoNavGrid nav;
    public PlayerHealth playerHealth;
    [Tooltip("Set per level so the report screenshot names the pathfinding approach.")]
    public string levelLabel = "Level 1";
    public string algorithmLabel = "Independent A* pursuit";
    [Tooltip("Optional Level 2 director, shown as coordination state when present.")]
    public HordeDirector director;

    private float smoothedFps;
    private GUIStyle wrapStyle;

    private void Awake()
    {
        // Static counter survives play sessions when domain reload is off - reset per run.
        NpcController.StuckRecoveries = 0;
    }

    private void Update()
    {
        float instantFps = 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        smoothedFps = smoothedFps <= 0f ? instantFps : Mathf.Lerp(smoothedFps, instantFps, 0.1f);
    }

    private void OnGUI()
    {
        if (wrapStyle == null)
        {
            wrapStyle = new GUIStyle(GUI.skin.label);
            wrapStyle.wordWrap = true;
        }
        wrapStyle.fontSize = 14;   // Virtual pixels; the GUI.matrix scale enlarges it.

        float s = DemoUI.Scale;
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        float vw = Screen.width / s;

        const float panelW = 300f;
        float x = vw - panelW - 10f;

        // GAME INFO (top-right).
        float infoH = DemoUI.IsCollapsed("hud.info")
            ? DemoUI.PanelBarHeight
            : (director != null ? 196f : 152f);
        GUILayout.BeginArea(new Rect(x, 10, panelW, infoH), GUI.skin.box);
        if (DemoUI.PanelHeader("hud.info", levelLabel.ToUpper()))
        {
            GUILayout.Label(algorithmLabel, wrapStyle);
            GUILayout.Label("FPS " + smoothedFps.ToString("F0") +
                            (playerHealth != null ? "   |   HP " + playerHealth.Current + "/" + playerHealth.maxHealth : ""),
                            wrapStyle);
            if (nav != null)
            {
                GUILayout.Label("Paths " + nav.PathsSucceeded + "/" + nav.PathsRequested + " ok - " +
                                nav.AveragePathMilliseconds.ToString("F2") + " ms - stuck " +
                                NpcController.StuckRecoveries, wrapStyle);
            }
            if (director != null)
            {
                GUILayout.Label("Horde: " + director.ModeLabel + " - routes " + director.LearnedHotspots, wrapStyle);
                GUILayout.Label("Ambushes " + director.AmbushesSet + " set / " + director.AmbushesSprung + " sprung", wrapStyle);
            }
        }
        GUILayout.EndArea();

        // CONTROLS (right, beneath the info panel).
        float ctrlH = DemoUI.IsCollapsed("hud.controls") ? DemoUI.PanelBarHeight : 100f;
        GUILayout.BeginArea(new Rect(x, 10 + infoH + 6, panelW, ctrlH), GUI.skin.box);
        if (DemoUI.PanelHeader("hud.controls", "CONTROLS"))
        {
            GUILayout.Label("Move WASD / arrows - sprint hold Left Shift (zombies HEAR it!)", wrapStyle);
            GUILayout.Label("ESC pause - ramps climb platforms", wrapStyle);
        }
        GUILayout.EndArea();

        GUI.matrix = previous;
    }
}
