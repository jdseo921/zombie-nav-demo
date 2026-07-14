using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;

/// <summary>
/// Heightmap navigation + A* pathfinding over the generated isometric tilemaps.
///
/// WHY A HEIGHTMAP (and not stacked z-cells with ramp adjacency):
/// The old model made every ramp depend on an exact z-adjacency between a hand-placed
/// ramp cell (z=0) and a platform cell (z=1). A single coordinate slip or a later tile
/// overwrite silently produced a ramp you could not climb. This model removes that whole
/// class of bug:
///
/// - Each column (x, y) stores ONE walkable floor: its integer level and a float height.
/// - A ramp column sits at an intermediate height (level + 0.5).
/// - You may step between two adjacent columns only when the height difference is small
///   (<= MaxStep = 0.6). So ground(0) -> ramp(0.5) -> platform(1) is walkable on both
///   sides, while a cliff (ground 0 -> platform 1, delta 1.0) is always blocked.
///
/// Consequences that make this robust for the future:
/// - If a ramp tile is present, the 0.5/0.5 geometry GUARANTEES it is climbable; you
///   cannot accidentally build an unclimbable ramp.
/// - If a ramp is missing or overwritten, the platform is simply unreachable (fails safe
///   and visible) instead of showing a dead ramp - and BuildAndReport / the arena
///   validator will flag it.
/// - Even a forgotten cliff collider cannot be walked up, because the height delta gates
///   it independently of colliders.
///
/// Assumption: each column has at most ONE walkable floor (platforms/bridges clear the
/// ground beneath them). The arena builder guarantees this; Build() logs a warning if it
/// ever finds two stacked walkable tiles in a column.
///
/// The tilemap layers read: "Base"/"Ramps" provide floor tiles; "Colliders" marks blocked
/// cells (walls, obstacle props, cliff faces). A column's floor is the highest z that has
/// a Base or Ramps tile and no Collider tile.
/// </summary>
public class IsoNavGrid : MonoBehaviour
{
    [Header("Tilemap layers (auto-found by child name if left empty)")]
    public Tilemap baseMap;
    public Tilemap rampsMap;
    public Tilemap collidersMap;

    /// <summary>Maximum climbable height difference between adjacent columns (in levels).</summary>
    public const float MaxStep = 0.6f;

    private Grid grid;

    // Heightmap arrays, indexed by Index(x, y).
    private int minX, minY, width, height2d;
    private bool[] walkArr;
    private int[] levelArr;
    private float[] heightArr;
    private bool[] rampArr;

    private float zOffsetY;
    private Vector2 csOrigin;
    private Vector2 basisX;
    private Vector2 basisY;
    private float basisDet;

    /// <summary>World-space Y offset per +1 elevation level.</summary>
    public float ZOffsetY { get { return zOffsetY; } }

    // Grid bounds, exposed so systems like the horde's occupancy map can build
    // parallel per-column arrays.
    public int GridMinX { get { return minX; } }
    public int GridMinY { get { return minY; } }
    public int GridColumnsX { get { return width; } }
    public int GridColumnsY { get { return height2d; } }

    // Pathfinding metrics (paper: "path success, path speed, stuck cases, performance cost").
    public int PathsRequested { get; private set; }
    public int PathsSucceeded { get; private set; }
    public int PathsFailed { get; private set; }
    public float TotalPathMilliseconds { get; private set; }
    public float AveragePathMilliseconds
    {
        get { return PathsRequested > 0 ? TotalPathMilliseconds / PathsRequested : 0f; }
    }

    private readonly System.Diagnostics.Stopwatch pathStopwatch = new System.Diagnostics.Stopwatch();

    private static readonly Vector2Int[] Cardinal =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1)
    };

    private void Awake()
    {
        // Make sprite/tile draw order follow world Y so actors sort against walls.
        GraphicsSettings.transparencySortMode = TransparencySortMode.CustomAxis;
        GraphicsSettings.transparencySortAxis = Vector3.up;
        Build();
    }

    public void Build()
    {
        grid = GetComponent<Grid>();
        if (grid == null)
        {
            grid = GetComponentInParent<Grid>();
        }

        if (baseMap == null || rampsMap == null || collidersMap == null)
        {
            Tilemap[] maps = GetComponentsInChildren<Tilemap>(true);
            for (int i = 0; i < maps.Length; i++)
            {
                if (maps[i].name == "Base") { baseMap = baseMap != null ? baseMap : maps[i]; }
                if (maps[i].name == "Ramps") { rampsMap = rampsMap != null ? rampsMap : maps[i]; }
                if (maps[i].name == "Colliders") { collidersMap = collidersMap != null ? collidersMap : maps[i]; }
            }
        }

        if (grid == null || baseMap == null || collidersMap == null)
        {
            Debug.LogError("IsoNavGrid: needs a Grid plus 'Base' and 'Colliders' tilemaps under it.");
            enabled = false;
            return;
        }

        zOffsetY = grid.CellToWorld(new Vector3Int(0, 0, 1)).y - grid.CellToWorld(Vector3Int.zero).y;
        if (Mathf.Approximately(zOffsetY, 0f))
        {
            zOffsetY = grid.cellSize.y;   // Fallback if this Unity version keeps z out of CellToWorld.
        }

        Vector3 origin = grid.CellToWorld(Vector3Int.zero);
        Vector3 ex = grid.CellToWorld(new Vector3Int(1, 0, 0)) - origin;
        Vector3 ey = grid.CellToWorld(new Vector3Int(0, 1, 0)) - origin;
        csOrigin = new Vector2(origin.x, origin.y);
        basisX = new Vector2(ex.x, ex.y);
        basisY = new Vector2(ey.x, ey.y);
        basisDet = basisX.x * basisY.y - basisX.y * basisY.x;

        BuildHeightmap();
    }

    private void BuildHeightmap()
    {
        int maxX = int.MinValue, maxY = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
        minX = int.MaxValue; minY = int.MaxValue;
        Tilemap[] sources = { baseMap, rampsMap, collidersMap };
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] == null) { continue; }
            BoundsInt b = sources[i].cellBounds;
            minX = Mathf.Min(minX, b.xMin); minY = Mathf.Min(minY, b.yMin); minZ = Mathf.Min(minZ, b.zMin);
            maxX = Mathf.Max(maxX, b.xMax); maxY = Mathf.Max(maxY, b.yMax); maxZ = Mathf.Max(maxZ, b.zMax);
        }
        if (minX > maxX)
        {
            minX = 0; minY = 0; maxX = 1; maxY = 1; minZ = 0; maxZ = 1;
        }

        width = maxX - minX;
        height2d = maxY - minY;
        int count = Mathf.Max(1, width * height2d);
        walkArr = new bool[count];
        levelArr = new int[count];
        heightArr = new float[count];
        rampArr = new bool[count];

        // FAILSAFE RULE: the TOPMOST floor tile in a column decides everything. Lower tiles
        // are buried visuals (ground kept under platforms fills the seams). The column is
        // blocked only when a collider sits at that top floor level - colliders below are
        // buried, and movement between columns is gated purely by height difference, so a
        // missing collider can never create a walkable cliff.
        int walkable = 0, ramps = 0;
        for (int x = minX; x < maxX; x++)
        {
            for (int y = minY; y < maxY; y++)
            {
                int idx = Index(x, y);
                for (int z = maxZ - 1; z >= minZ; z--)
                {
                    Vector3Int c = new Vector3Int(x, y, z);
                    bool hasBase = baseMap.HasTile(c);
                    bool hasRamp = rampsMap != null && rampsMap.HasTile(c);
                    if (!hasBase && !hasRamp)
                    {
                        continue;
                    }
                    if (!collidersMap.HasTile(c))
                    {
                        walkArr[idx] = true;
                        levelArr[idx] = z;
                        rampArr[idx] = hasRamp;
                        heightArr[idx] = z + (hasRamp ? 0.5f : 0f);
                        walkable++;
                        if (hasRamp) { ramps++; }
                    }
                    break;   // Topmost floor found - blocked or not, the column is decided.
                }
            }
        }
        TotalWalkableColumns = walkable;
        ComputeChokepoints();

        Debug.Log("IsoNavGrid: heightmap built - " + walkable + " walkable columns, " + ramps + " ramps, bounds " +
                  width + "x" + height2d + ".");
    }

    // ------------------------------------------------------------ chokepoints

    private bool[] chokeArr;

    /// <summary>
    /// Marks narrow passages (doorways, 1-2 wide corridors): cells you can pass THROUGH
    /// along one axis while the perpendicular clearance is at most 2 cells. Used by the
    /// horde's habit-aware ambush allocation.
    /// </summary>
    private void ComputeChokepoints()
    {
        chokeArr = new bool[Mathf.Max(1, width * height2d)];
        for (int x = minX; x < minX + width; x++)
        {
            for (int y = minY; y < minY + height2d; y++)
            {
                if (!ColumnWalkable(x, y))
                {
                    continue;
                }
                bool xThrough = StepAllowed(x, y, x + 1, y) && StepAllowed(x, y, x - 1, y);
                bool yThrough = StepAllowed(x, y, x, y + 1) && StepAllowed(x, y, x, y - 1);
                int runX = 1 + RunLength(x, y, 1, 0) + RunLength(x, y, -1, 0);
                int runY = 1 + RunLength(x, y, 0, 1) + RunLength(x, y, 0, -1);
                chokeArr[Index(x, y)] = (xThrough && runY <= 2) || (yThrough && runX <= 2);
            }
        }
    }

    private int RunLength(int x, int y, int dx, int dy)
    {
        int run = 0;
        int cx = x, cy = y;
        while (run < 3 && StepAllowed(cx, cy, cx + dx, cy + dy))
        {
            cx += dx;
            cy += dy;
            run++;
        }
        return run;
    }

    /// <summary>Is this column a narrow passage (doorway / tight corridor)?</summary>
    public bool IsChokepoint(int x, int y)
    {
        return InBounds(x, y) && chokeArr != null && chokeArr[Index(x, y)];
    }

    /// <summary>Total walkable columns (set by Build; used with CountReachableFrom to detect pockets).</summary>
    public int TotalWalkableColumns { get; private set; }

    /// <summary>Flood-fills from a start cell and counts every column reachable under the step rules.</summary>
    public int CountReachableFrom(Vector3Int start)
    {
        if (!ColumnWalkable(start.x, start.y))
        {
            return 0;
        }
        var visited = new HashSet<Vector2Int>();
        var queue = new Queue<Vector2Int>();
        var s = new Vector2Int(start.x, start.y);
        visited.Add(s);
        queue.Enqueue(s);
        while (queue.Count > 0)
        {
            Vector2Int c = queue.Dequeue();
            for (int d = 0; d < Cardinal.Length; d++)
            {
                Vector2Int n = new Vector2Int(c.x + Cardinal[d].x, c.y + Cardinal[d].y);
                if (visited.Contains(n) || !StepAllowed(c.x, c.y, n.x, n.y))
                {
                    continue;
                }
                visited.Add(n);
                queue.Enqueue(n);
            }
        }
        return visited.Count;
    }

    private int Index(int x, int y)
    {
        return (x - minX) + (y - minY) * width;
    }

    private bool InBounds(int x, int y)
    {
        return x >= minX && y >= minY && x < minX + width && y < minY + height2d;
    }

    // ------------------------------------------------------------ column queries

    public bool ColumnWalkable(int x, int y)
    {
        return InBounds(x, y) && walkArr[Index(x, y)];
    }

    public int ColumnLevel(int x, int y)
    {
        return InBounds(x, y) ? levelArr[Index(x, y)] : 0;
    }

    public float ColumnHeightF(int x, int y)
    {
        return InBounds(x, y) ? heightArr[Index(x, y)] : 0f;
    }

    public bool IsWalkable(Vector3Int cell)
    {
        return ColumnWalkable(cell.x, cell.y) && cell.z == ColumnLevel(cell.x, cell.y);
    }

    public bool HasRamp(Vector3Int cell)
    {
        return InBounds(cell.x, cell.y) && rampArr[Index(cell.x, cell.y)];
    }

    /// <summary>The single walkable cell of a column (z = its floor level).</summary>
    public bool TryGetColumnCell(int x, int y, out Vector3Int cell)
    {
        if (ColumnWalkable(x, y))
        {
            cell = new Vector3Int(x, y, ColumnLevel(x, y));
            return true;
        }
        cell = default;
        return false;
    }

    /// <summary>Can an actor on column A occupy adjacent column B? (walkable + small step)</summary>
    public bool StepAllowed(int ax, int ay, int bx, int by)
    {
        if (!ColumnWalkable(ax, ay) || !ColumnWalkable(bx, by))
        {
            return false;
        }
        return Mathf.Abs(ColumnHeightF(bx, by) - ColumnHeightF(ax, ay)) <= MaxStep + 1e-4f;
    }

    /// <summary>Elevation-aware step test used by the movement body and A*.</summary>
    public bool CanStep(Vector3Int from, Vector3Int to)
    {
        if (!ColumnWalkable(to.x, to.y) || to.z != ColumnLevel(to.x, to.y))
        {
            return false;
        }
        return StepAllowed(from.x, from.y, to.x, to.y);
    }

    // ------------------------------------------------------------ world <-> cell space

    public Vector3 CellCenterWorld(Vector3Int cell)
    {
        return CellSpaceToWorld(CellCenterCellSpace(cell), ColumnHeightF(cell.x, cell.y));
    }

    public Vector2 CellCenterCellSpace(Vector3Int cell)
    {
        return new Vector2(cell.x + 0.5f, cell.y + 0.5f);
    }

    public Vector2 WorldDeltaToCellSpace(Vector3 worldDelta)
    {
        float u = (worldDelta.x * basisY.y - worldDelta.y * basisY.x) / basisDet;
        float v = (worldDelta.y * basisX.x - worldDelta.x * basisX.y) / basisDet;
        return new Vector2(u, v);
    }

    /// <summary>Cell-space position to world, at a (possibly fractional) elevation.</summary>
    public Vector3 CellSpaceToWorld(Vector2 cs, float heightLevels)
    {
        Vector2 flat = csOrigin + basisX * cs.x + basisY * cs.y;
        return new Vector3(flat.x, flat.y + zOffsetY * heightLevels, 0f);
    }

    /// <summary>Column under a world position, ignoring elevation offset.</summary>
    public Vector2Int ColumnAtWorld(Vector3 world)
    {
        Vector2 cs = WorldDeltaToCellSpace(new Vector3(world.x - csOrigin.x, world.y - csOrigin.y, 0f));
        return new Vector2Int(Mathf.FloorToInt(cs.x), Mathf.FloorToInt(cs.y));
    }

    /// <summary>Finds the walkable cell nearest to a world position.</summary>
    public bool FindNearestNode(Vector3 world, out Vector3Int result)
    {
        Vector2Int c = ColumnAtWorld(world);
        if (TryGetColumnCell(c.x, c.y, out result))
        {
            return true;
        }
        for (int radius = 1; radius <= 8; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Mathf.Abs(dx) != radius && Mathf.Abs(dy) != radius)
                    {
                        continue;   // Only the ring at this radius.
                    }
                    if (TryGetColumnCell(c.x + dx, c.y + dy, out result))
                    {
                        return true;
                    }
                }
            }
        }
        result = default;
        return false;
    }

    // ------------------------------------------------------------ footprint collision

    /// <summary>Can the actor on 'from' overlap column (x, y)? (own column, or a small step)</summary>
    public bool PassableFrom(Vector3Int from, int x, int y)
    {
        if (x == from.x && y == from.y)
        {
            return true;
        }
        if (!ColumnWalkable(x, y))
        {
            return false;
        }
        return Mathf.Abs(ColumnHeightF(x, y) - ColumnHeightF(from.x, from.y)) <= MaxStep + 1e-4f;
    }

    /// <summary>Whether a footprint box (cell units) only covers passable columns.</summary>
    public bool BoxPassable(Vector3Int fromCell, Vector2 cs, float radius)
    {
        int x0 = Mathf.FloorToInt(cs.x - radius);
        int x1 = Mathf.FloorToInt(cs.x + radius);
        int y0 = Mathf.FloorToInt(cs.y - radius);
        int y1 = Mathf.FloorToInt(cs.y + radius);
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                if (!PassableFrom(fromCell, x, y))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Straight-line visibility on the column grid; any collider cell blocks sight.</summary>
    public bool HasLineOfSight(Vector3Int from, Vector3Int to)
    {
        int x0 = from.x; int y0 = from.y;
        int x1 = to.x; int y1 = to.y;
        int dx = Mathf.Abs(x1 - x0); int sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0); int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            bool isEndpoint = (x0 == from.x && y0 == from.y) || (x0 == x1 && y0 == y1);
            if (!isEndpoint && !ColumnWalkable(x0, y0))
            {
                return false;   // Wall, obstacle or cliff face between the two points.
            }
            if (x0 == x1 && y0 == y1)
            {
                return true;
            }
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    // ------------------------------------------------------------ A*

    /// <summary>A* over walkable columns. Returns false when no route exists.</summary>
    public bool FindPath(Vector3Int start, Vector3Int goal, List<Vector3Int> outCells)
    {
        PathsRequested++;
        pathStopwatch.Restart();
        bool found = FindPathInternal(start, goal, outCells);
        pathStopwatch.Stop();
        TotalPathMilliseconds += (float)pathStopwatch.Elapsed.TotalMilliseconds;
        if (found) { PathsSucceeded++; } else { PathsFailed++; }
        return found;
    }

    private bool FindPathInternal(Vector3Int start, Vector3Int goal, List<Vector3Int> outCells)
    {
        outCells.Clear();
        if (!ColumnWalkable(start.x, start.y) || !ColumnWalkable(goal.x, goal.y))
        {
            return false;
        }
        Vector2Int startCol = new Vector2Int(start.x, start.y);
        Vector2Int goalCol = new Vector2Int(goal.x, goal.y);
        if (startCol == goalCol)
        {
            outCells.Add(new Vector3Int(start.x, start.y, ColumnLevel(start.x, start.y)));
            return true;
        }

        var open = new List<Vector2Int> { startCol };
        var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        var gScore = new Dictionary<Vector2Int, int> { { startCol, 0 } };
        var fScore = new Dictionary<Vector2Int, int> { { startCol, Heuristic(startCol, goalCol) } };
        var closed = new HashSet<Vector2Int>();
        int safety = 40000;

        while (open.Count > 0 && safety-- > 0)
        {
            int bestIndex = 0;
            int bestF = fScore[open[0]];
            for (int i = 1; i < open.Count; i++)
            {
                if (fScore[open[i]] < bestF)
                {
                    bestF = fScore[open[i]];
                    bestIndex = i;
                }
            }
            Vector2Int current = open[bestIndex];
            open.RemoveAt(bestIndex);

            if (current == goalCol)
            {
                Vector2Int step = goalCol;
                outCells.Add(new Vector3Int(step.x, step.y, ColumnLevel(step.x, step.y)));
                while (cameFrom.TryGetValue(step, out Vector2Int prev))
                {
                    step = prev;
                    outCells.Add(new Vector3Int(step.x, step.y, ColumnLevel(step.x, step.y)));
                }
                outCells.Reverse();
                return true;
            }
            closed.Add(current);

            for (int d = 0; d < Cardinal.Length; d++)
            {
                Vector2Int next = new Vector2Int(current.x + Cardinal[d].x, current.y + Cardinal[d].y);
                if (closed.Contains(next) || !StepAllowed(current.x, current.y, next.x, next.y))
                {
                    continue;
                }
                int tentative = gScore[current] + 1;
                if (gScore.TryGetValue(next, out int known) && tentative >= known)
                {
                    continue;
                }
                cameFrom[next] = current;
                gScore[next] = tentative;
                fScore[next] = tentative + Heuristic(next, goalCol);
                if (!open.Contains(next))
                {
                    open.Add(next);
                }
            }
        }
        return false;
    }

    private static int Heuristic(Vector2Int a, Vector2Int b)
    {
        return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }

    /// <summary>True when a path exists from one cell to another (used by the arena validator).</summary>
    public bool IsReachable(Vector3Int from, Vector3Int to)
    {
        List<Vector3Int> scratch = new List<Vector3Int>();
        return FindPath(from, to, scratch);
    }

    // ------------------------------------------------------------ flow field (Level 2)

    // A single BFS distance map from a goal cell, shared by the whole horde. Computing it
    // once and having every zombie read O(1) steps from it is far cheaper than each zombie
    // running its own A* - the efficient-many-agents technique the report cites.
    private int[] flow;
    private readonly Queue<Vector2Int> flowQueue = new Queue<Vector2Int>();

    public bool HasFlowField { get { return flow != null; } }

    /// <summary>(Re)computes the shared flow field: distance-to-goal for every column.</summary>
    public void ComputeFlowField(Vector3Int goal)
    {
        int count = Mathf.Max(1, width * height2d);
        if (flow == null || flow.Length != count)
        {
            flow = new int[count];
        }
        for (int i = 0; i < flow.Length; i++)
        {
            flow[i] = int.MaxValue;
        }
        if (!ColumnWalkable(goal.x, goal.y))
        {
            return;
        }
        flowQueue.Clear();
        flow[Index(goal.x, goal.y)] = 0;
        flowQueue.Enqueue(new Vector2Int(goal.x, goal.y));
        while (flowQueue.Count > 0)
        {
            Vector2Int c = flowQueue.Dequeue();
            int cd = flow[Index(c.x, c.y)];
            for (int d = 0; d < Cardinal.Length; d++)
            {
                int nx = c.x + Cardinal[d].x;
                int ny = c.y + Cardinal[d].y;
                if (!ColumnWalkable(nx, ny) || !StepAllowed(c.x, c.y, nx, ny))
                {
                    continue;
                }
                int ni = Index(nx, ny);
                if (flow[ni] > cd + 1)
                {
                    flow[ni] = cd + 1;
                    flowQueue.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }
    }

    public int FlowDistance(Vector3Int cell)
    {
        if (flow == null || !InBounds(cell.x, cell.y))
        {
            return int.MaxValue;
        }
        return flow[Index(cell.x, cell.y)];
    }

    /// <summary>Next cell to move to while descending the flow field toward its goal.</summary>
    public bool FlowNextStep(Vector3Int from, out Vector3Int next)
    {
        next = from;
        if (flow == null || !ColumnWalkable(from.x, from.y))
        {
            return false;
        }
        int here = flow[Index(from.x, from.y)];
        if (here == 0 || here == int.MaxValue)
        {
            return false;
        }
        int best = here;
        for (int d = 0; d < Cardinal.Length; d++)
        {
            int nx = from.x + Cardinal[d].x;
            int ny = from.y + Cardinal[d].y;
            if (!ColumnWalkable(nx, ny) || !StepAllowed(from.x, from.y, nx, ny))
            {
                continue;
            }
            int nd = flow[Index(nx, ny)];
            if (nd < best)
            {
                best = nd;
                next = new Vector3Int(nx, ny, ColumnLevel(nx, ny));
            }
        }
        return best < here;
    }
}
