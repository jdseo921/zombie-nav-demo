using UnityEngine;

/// <summary>
/// Continuous grid-collision body shared by the demo player and NPCs.
///
/// The actor's position lives in "cell space" (1 unit = 1 iso cell), where every
/// diamond cell is an axis-aligned unit square. A square footprint of half-size
/// `radius` is tested against blocked columns on every move, resolved per axis
/// (which gives natural wall sliding), so the sprite's feet can NEVER overlap a
/// wall, obstacle collider, cliff face or platform edge - from any direction.
/// Elevation transitions follow the nav grid's ramp rules and the visual height
/// is smoothed so ramps read as slopes instead of pops.
/// </summary>
public class GridBody : MonoBehaviour
{
    public IsoNavGrid nav;
    [Tooltip("Footprint half-size in cell units. Keep below 0.5 so 1-wide gaps stay passable.")]
    public float radius = 0.36f;
    [Tooltip("Visual elevation smoothing, in z-levels per second.")]
    public float climbSpeed = 5f;

    public Vector3Int Cell { get { return cell; } }

    /// <summary>World position on the z=0 plane (no elevation offset) - use for steering math.</summary>
    public Vector3 FlatWorldPosition { get { return nav.CellSpaceToWorld(cellSpace, 0f); } }

    private Vector3Int cell;
    private Vector2 cellSpace;
    private float height;

    public void Initialize(IsoNavGrid grid, Vector3Int startCell)
    {
        nav = grid;
        Snap(startCell);
    }

    /// <summary>Teleports onto a cell center (spawn / respawn / stuck recovery).</summary>
    public void Snap(Vector3Int target)
    {
        cell = target;
        cellSpace = nav.CellCenterCellSpace(target);
        height = nav.ColumnHeightF(target.x, target.y);
        transform.position = nav.CellSpaceToWorld(cellSpace, height);
    }

    /// <summary>
    /// Scales a nominal speed to the player's isometric movement profile (vertical
    /// world speed is halved - see DemoPlayerController). An NPC stepping along
    /// worldDirection at the returned speed covers ground at EXACTLY the pace the
    /// player moves in that direction, so equal nominal speeds mean equal real speeds
    /// on every axis - no more NPCs outrunning the player on vertical stretches.
    /// </summary>
    public static float DirectionalSpeed(float speed, Vector3 worldDirection)
    {
        float lengthSq = worldDirection.x * worldDirection.x + worldDirection.y * worldDirection.y;
        if (lengthSq < 1e-8f)
        {
            return speed;
        }
        float inverseLength = 1f / Mathf.Sqrt(lengthSq);
        float dx = worldDirection.x * inverseLength;
        float dy = worldDirection.y * inverseLength;
        return speed / Mathf.Sqrt(dx * dx + 4f * dy * dy);
    }

    /// <summary>Moves by a flat world-space delta, clamped against blocked columns.</summary>
    public void MoveWorld(Vector3 worldDelta)
    {
        Vector2 delta = nav.WorldDeltaToCellSpace(worldDelta);
        MoveAxis(new Vector2(delta.x, 0f));
        MoveAxis(new Vector2(0f, delta.y));
    }

    private void MoveAxis(Vector2 delta)
    {
        if (delta.sqrMagnitude < 1e-12f)
        {
            return;
        }
        Vector2 target = cellSpace + delta;
        if (nav.BoxPassable(cell, target, radius))
        {
            cellSpace = target;
            UpdateCell();
        }
    }

    private void UpdateCell()
    {
        int x = Mathf.FloorToInt(cellSpace.x);
        int y = Mathf.FloorToInt(cellSpace.y);
        if (x == cell.x && y == cell.y)
        {
            return;
        }
        // The heightmap stores exactly one walkable floor per column, so the destination
        // level is unambiguous - just adopt it. BoxPassable already vetted the column.
        if (nav.ColumnWalkable(x, y))
        {
            cell = new Vector3Int(x, y, nav.ColumnLevel(x, y));
        }
    }

    /// <summary>Advances the climb smoothing and writes the transform. Call once per frame.</summary>
    public void Apply()
    {
        // Smooth toward the column's float height (ramps sit at level + 0.5), so climbing
        // reads as a slope instead of a pop.
        float targetHeight = nav.ColumnHeightF(cell.x, cell.y);
        height = Mathf.MoveTowards(height, targetHeight, climbSpeed * Time.deltaTime);
        transform.position = nav.CellSpaceToWorld(cellSpace, height);
    }
}
