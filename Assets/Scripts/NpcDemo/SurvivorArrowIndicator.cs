using UnityEngine;

/// <summary>
/// Small screen arrows pointing at survivors who have NOT been following the player for
/// a while, so an escort left behind (or not yet found) is never lost. Each arrow sits
/// at the screen edge pointing toward its survivor (or floats over their head when they
/// are on screen but far), with the survivor's name underneath. The arrow appears after
/// awayDelay seconds of separation, stays for showDuration, then rests for cooldown
/// before pulsing again - repeating until reunion (or death) resets the cycle.
/// </summary>
public class SurvivorArrowIndicator : MonoBehaviour
{
    public DemoPlayerController player;
    public NpcController[] survivors;
    public LevelObjective objective;
    [Tooltip("Farther from the player than this counts as 'not following'.")]
    public float awayDistance = 11f;
    [Tooltip("Closer than this resets the away timer (hysteresis so arrows don't flicker).")]
    public float nearDistance = 8f;
    [Tooltip("Seconds of continuous separation before the arrow first appears.")]
    public float awayDelay = 10f;
    [Tooltip("Seconds the arrow stays on screen per pulse.")]
    public float showDuration = 5f;
    [Tooltip("Seconds between pulses while still separated.")]
    public float cooldown = 10f;

    private float[] awayTimers;
    private Texture2D arrowTexture;
    private GUIStyle nameStyle;

    private void Update()
    {
        if (player == null || survivors == null)
        {
            return;
        }
        if (awayTimers == null || awayTimers.Length != survivors.Length)
        {
            awayTimers = new float[survivors.Length];
        }
        for (int i = 0; i < survivors.Length; i++)
        {
            NpcController s = survivors[i];
            if (s == null || s.IsDead)
            {
                awayTimers[i] = 0f;
                continue;
            }
            float d = Vector3.Distance(player.transform.position, s.transform.position);
            if (d > awayDistance)
            {
                awayTimers[i] += Time.deltaTime;
            }
            else if (d < nearDistance)
            {
                awayTimers[i] = 0f;
            }
        }
    }

    private void OnGUI()
    {
        if (player == null || survivors == null || awayTimers == null || Camera.main == null)
        {
            return;
        }
        if (objective != null && objective.Outcome != LevelObjective.State.InProgress)
        {
            return;
        }
        EnsureAssets();

        float s = DemoUI.Scale;
        Matrix4x4 previous = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
        float vw = Screen.width / s;
        float vh = Screen.height / s;

        for (int i = 0; i < survivors.Length; i++)
        {
            NpcController survivor = survivors[i];
            if (survivor == null || survivor.IsDead || !ArrowVisible(awayTimers[i]))
            {
                continue;
            }

            Vector3 screenPoint = Camera.main.WorldToScreenPoint(survivor.transform.position + Vector3.up * 1.6f);
            Vector2 pos = new Vector2(screenPoint.x / s, (Screen.height - screenPoint.y) / s);
            const float margin = 48f;
            Vector2 clamped = new Vector2(
                Mathf.Clamp(pos.x, margin, vw - margin),
                Mathf.Clamp(pos.y, margin, vh - margin - 16f));

            // Point toward the survivor's true position; when they are on screen (no
            // clamping happened) the arrow floats above them pointing down.
            Vector2 toTarget = pos - clamped;
            float angle = toTarget.sqrMagnitude < 1f ? 90f : Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;

            Matrix4x4 beforeRotation = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, clamped);
            GUI.DrawTexture(new Rect(clamped.x - 12f, clamped.y - 8f, 24f, 16f), arrowTexture);
            GUI.matrix = beforeRotation;

            GUI.Label(new Rect(clamped.x - 60f, clamped.y + 10f, 120f, 18f), survivor.displayName, nameStyle);
        }

        GUI.matrix = previous;
    }

    /// <summary>Pulse cycle: hidden for awayDelay, then showDuration on / cooldown off.</summary>
    private bool ArrowVisible(float awayTime)
    {
        if (awayTime < awayDelay)
        {
            return false;
        }
        float cycleTime = (awayTime - awayDelay) % (showDuration + cooldown);
        return cycleTime < showDuration;
    }

    private void EnsureAssets()
    {
        if (arrowTexture == null)
        {
            const int W = 24, H = 16;
            arrowTexture = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Color fill = new Color(1f, 0.85f, 0.25f, 1f);      // Amber - readable on any map.
            Color clear = new Color(0f, 0f, 0f, 0f);
            for (int x = 0; x < W; x++)
            {
                // Right-pointing triangle: full height at the left, apex on the right.
                float half = (1f - x / (float)(W - 1)) * (H * 0.5f - 0.5f);
                for (int y = 0; y < H; y++)
                {
                    arrowTexture.SetPixel(x, y, Mathf.Abs(y - (H - 1) * 0.5f) <= half ? fill : clear);
                }
            }
            arrowTexture.Apply();
        }
        if (nameStyle == null)
        {
            nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            nameStyle.normal.textColor = new Color(1f, 0.9f, 0.45f);
        }
    }
}
