using UnityEngine;

/// <summary>
/// Win/lose logic for a level (the paper's GameFlow "clear goals" criterion made literal):
///
///   WIN  - the player AND every survivor NPC are inside the extraction zone together
///          (nobody gets left behind).
///   FAIL - the player dies to the horde (PlayerHealth.IsDead). No respawns: the
///          GameManager offers a level restart.
///
/// Also draws the objective status panel (top-right, scaled, hideable via Settings).
/// </summary>
public class LevelObjective : MonoBehaviour
{
    public enum State { InProgress, Won, Failed }

    [Header("Wiring (set by the builder)")]
    public IsoNavGrid nav;
    public DemoPlayerController player;
    public PlayerHealth playerHealth;
    public NpcController[] survivors;

    [Header("Extraction zone")]
    public Vector3Int extractionCell;
    public float zoneRadius = 3f;   // World units around the zone centre.

    public State Outcome { get; private set; }
    public bool PlayerInZone { get; private set; }
    public int SurvivorsInZone { get; private set; }
    public int SurvivorsTotal { get { return survivors != null ? survivors.Length : 0; } }
    public string FailTitle { get; private set; } = "YOU DIED";
    public string FailMessage { get; private set; } = "The horde got you. The survivors are on their own now...";

    private GUIStyle wrapStyle;

    private void Update()
    {
        if (Outcome != State.InProgress || nav == null || player == null)
        {
            return;
        }

        if (playerHealth != null && playerHealth.IsDead)
        {
            FailTitle = "YOU DIED";
            FailMessage = "The horde got you. The survivors are on their own now...";
            Outcome = State.Failed;
            return;
        }

        Vector3 zoneCenter = nav.CellCenterWorld(extractionCell);
        PlayerInZone = Vector3.Distance(player.transform.position, zoneCenter) <= zoneRadius;

        int inZone = 0;
        if (survivors != null)
        {
            for (int i = 0; i < survivors.Length; i++)
            {
                if (survivors[i] == null)
                {
                    continue;
                }
                if (survivors[i].IsDead)
                {
                    FailTitle = "SURVIVOR LOST";
                    FailMessage = survivors[i].displayName + " was torn apart by the horde. " +
                                  "Nobody gets left behind - keep them close and away from the dead.";
                    Outcome = State.Failed;
                    return;
                }
                if (Vector3.Distance(survivors[i].transform.position, zoneCenter) <= zoneRadius)
                {
                    inZone++;
                }
            }
        }
        SurvivorsInZone = inZone;

        if (PlayerInZone && SurvivorsInZone >= SurvivorsTotal)
        {
            Outcome = State.Won;
        }
    }

    private void OnGUI()
    {
        if (Outcome != State.InProgress)
        {
            return;
        }
        if (wrapStyle == null)
        {
            wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }
        wrapStyle.fontSize = 14;

        float s = DemoUI.Scale;
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));

        // Compact objective panel, top-left, with a minimize/expand button.
        float h = DemoUI.IsCollapsed("hud.objective") ? DemoUI.PanelBarHeight : 104f;
        GUILayout.BeginArea(new Rect(10, 10, 270, h), GUI.skin.box);
        if (DemoUI.PanelHeader("hud.objective", "OBJECTIVE"))
        {
            GUILayout.Label("Reach the green EXTRACTION zone with ALL survivors.", wrapStyle);
            GUILayout.Label("You: " + (PlayerInZone ? "IN ZONE" : "not in zone") +
                            "   Survivors: " + SurvivorsInZone + "/" + SurvivorsTotal, wrapStyle);
        }
        GUILayout.EndArea();

        GUI.matrix = previous;
    }
}
