using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Grid-aware demo player. WASD / arrows to move, hold Left Shift to sprint.
///
/// Movement goes through a GridBody footprint (same collision the NPCs use), so the
/// player obeys exactly the same rules the NPCs path with: walls, obstacle colliders,
/// cliff faces and platform edges are impossible to overlap from any direction, and
/// elevation only changes across ramp cells. Sprinting matters: NPCs "hear" a
/// sprinting player through walls.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class DemoPlayerController : MonoBehaviour
{
    public IsoNavGrid nav;
    public float walkSpeed = 3f;
    public float sprintSpeed = 5f;

    [Tooltip("Snap to this cell on start (set by the demo builder).")]
    public bool useStartCell;
    public Vector3Int startCell;

    public bool IsSprinting { get; private set; }
    public Vector3Int CurrentCell { get { return body != null ? body.Cell : startCell; } }

    /// <summary>World-space velocity (used by the Level 2 director to predict where you flee).</summary>
    public Vector3 Velocity { get; private set; }

    private GridBody body;
    private SpriteRenderer spriteRenderer;
    private Vector3 lastPosition;

    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (nav == null)
        {
            nav = FindAnyObjectByType<IsoNavGrid>();
        }

        body = GetComponent<GridBody>();
        if (body == null)
        {
            body = gameObject.AddComponent<GridBody>();
        }

        Vector3Int spawn = startCell;
        if (!useStartCell && !nav.FindNearestNode(transform.position, out spawn))
        {
            spawn = startCell;
        }
        body.Initialize(nav, spawn);
        lastPosition = transform.position;
    }

    private void Update()
    {
        if (body == null || nav == null)
        {
            return;
        }

        Keyboard kb = Keyboard.current;
        Vector2 input = Vector2.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) { input.y += 1f; }
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) { input.y -= 1f; }
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) { input.x += 1f; }
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) { input.x -= 1f; }
        }

        IsSprinting = kb != null && kb.leftShiftKey.isPressed && input.sqrMagnitude > 0.01f;

        if (input.sqrMagnitude > 0.01f)
        {
            input.Normalize();
            // Halve vertical speed for the classic isometric movement feel.
            Vector3 delta = new Vector3(input.x, input.y * 0.5f, 0f) * (IsSprinting ? sprintSpeed : walkSpeed) * Time.deltaTime;
            body.MoveWorld(delta);

            if (Mathf.Abs(input.x) > 0.01f)
            {
                spriteRenderer.flipX = input.x < 0f;
            }
        }

        body.Apply();

        Velocity = Time.deltaTime > 0f ? (transform.position - lastPosition) / Time.deltaTime : Vector3.zero;
        lastPosition = transform.position;
    }

    /// <summary>Snaps the player onto a cell (used by respawn).</summary>
    public void Teleport(Vector3Int cell)
    {
        if (body != null)
        {
            body.Snap(cell);
        }
        else
        {
            startCell = cell;
            useStartCell = true;
        }
    }
}
