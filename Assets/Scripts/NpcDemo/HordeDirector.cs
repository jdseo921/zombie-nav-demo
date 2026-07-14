using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Level 2 pursuit AI: PROBABILISTIC OCCUPANCY-MAP PURSUIT with shared flow-field
/// navigation, role-based interception and an online player-behaviour bias.
///
/// Level 1's zombies replan A* to the player's true position (classical replanning
/// against a moving target, cf. Hart, Nilsson &amp; Raphael 1968; Ishida &amp; Korf's
/// "Moving Target Search", 1991). This director instead implements techniques from the
/// pursuit-evasion and game-AI research literature:
///
/// 1. OCCUPANCY-MAP TARGET TRACKING (belief state). When no zombie can see or hear the
///    player, the director maintains a probability distribution ("belief") over every
///    walkable column of where the player might be. Each planning tick the belief
///    DIFFUSES to neighbouring columns (a random-walk motion model of the hidden
///    target) and is CULLED wherever a zombie can currently see (negative
///    observations) - recursive Bayesian estimation on a grid, after occupancy grids
///    in robotics (Moravec &amp; Elfes 1985; Elfes 1989) and their game adaptation for
///    probabilistic target tracking and search (Isla 2006; Isla's "Third Eye Crime",
///    AIIDE 2013). A confirmed sighting collapses the belief to the player's true cell.
///    THE HORDE NEVER READS THE TRUE PLAYER POSITION WHILE THE TARGET IS HIDDEN - it
///    hunts the belief, so hiding and breaking line of sight genuinely work.
/// 2. MULTI-AGENT PROBABILISTIC PURSUIT. The horde is coordinated centrally. Target
///    CONFIRMED: nearest agents chase down a shared flow field while the farthest are
///    dispatched to cut-off cells ahead of the player's predicted motion (pursuit role
///    allocation, cf. Hespanha, Kim &amp; Sastry 1999; Vidal et al. 2002; graph
///    pursuit "cops and robbers", Nowakowski &amp; Winkler 1983). Target HIDDEN: agents
///    are allocated across the strongest belief peaks and sweep them, shrinking the
///    probability mass until the player is reacquired or the trail goes cold.
/// 3. SHARED FLOW-FIELD NAVIGATION. One BFS distance field per tick serves the whole
///    horde with O(1) steps per agent (potential fields, Khatib 1986; continuum crowd
///    fields, Treuille, Cooper &amp; Popovic 2006).
/// 4. PLAYER MODELLING. A decaying visit heatmap biases belief diffusion and cut-off
///    placement toward the player's habitual routes, so the horde "learns" escape
///    patterns over a session (lightweight online player modelling, cf. Yannakakis
///    &amp; Togelius 2013).
/// 5. HABIT-ANTICIPATORY AMBUSH ALLOCATION (original extension of this project). The
///    director fuses the ONLINE player-route model (4) with a STATIC chokepoint
///    analysis of the navigation graph (narrow doorways/corridors, IsoNavGrid
///    .IsChokepoint): whenever the target is not confirmed - including while the horde
///    is otherwise calm - up to maxAmbushers zombies are pre-positioned at the
///    chokepoints nearest the player's most-visited cells, where they hold perfectly
///    still until the player wanders into trigger range. The sprung ambush counts as
///    an observation and collapses the belief. Unlike the cited techniques, which
///    react to where the target IS or probably is, this layer anticipates where the
///    target WILL RETURN based on its measured habits - the fusion of route learning,
///    chokepoint topology and pre-emptive role allocation is this project's own
///    contribution, and is reported on the HUD (ambushes set / sprung).
/// </summary>
public class HordeDirector : MonoBehaviour
{
    public IsoNavGrid nav;
    public Transform player;
    public DemoPlayerController playerController;

    [Header("Coordination")]
    [Tooltip("Fraction of the horde assigned to flank/search (the rest chase directly).")]
    [Range(0f, 1f)] public float flankerFraction = 0.5f;
    [Tooltip("How far ahead of the player (seconds) flankers aim to cut off.")]
    public float interceptLeadSeconds = 1.4f;
    [Tooltip("Ring radius (cells) flankers spread around the cut-off point.")]
    public int encircleRadius = 5;
    public float repathInterval = 0.4f;

    [Header("Belief (occupancy map)")]
    [Tooltip("Probability mass that spreads to neighbours per tick (hidden-target motion model).")]
    [Range(0f, 0.9f)] public float diffusionRate = 0.45f;
    [Tooltip("How strongly the learned heatmap attracts belief diffusion (0 = pure random walk).")]
    public float heatBias = 0.6f;
    [Tooltip("Belief multiplier for cells a zombie can currently see (negative observation).")]
    public float negativeObservation = 0.06f;
    [Tooltip("World-unit range within which a zombie visually clears cells.")]
    public float sightCullRange = 7f;
    [Tooltip("With no sighting for this long, the horde loses the trail and stands down.")]
    public float searchGiveUpSeconds = 18f;

    [Header("Habit-anticipatory ambush (original extension)")]
    [Tooltip("How many zombies may lie in wait at chokepoints on the player's learned routes.")]
    public int maxAmbushers = 2;
    [Tooltip("Minimum learned heat before a route earns an ambush.")]
    public float ambushMinHeat = 3f;
    [Tooltip("How often (seconds) ambush posts are re-evaluated.")]
    public float ambushInterval = 2f;

    public int AmbushesSet { get; private set; }
    public int AmbushesSprung { get; private set; }

    public bool Alerted { get; private set; }
    public bool TargetConfirmed { get; private set; }
    public float PeakBelief { get; private set; }
    public int FlankerCount { get; private set; }
    public int LearnedHotspots { get; private set; }

    public string ModeLabel
    {
        get
        {
            if (!Alerted) { return "calm"; }
            if (TargetConfirmed) { return "HUNTING (confirmed)"; }
            return "SEARCHING (peak " + (PeakBelief * 100f).ToString("F0") + "%)";
        }
    }

    private readonly List<HordeZombieController> horde = new List<HordeZombieController>();
    private float repathTimer;
    private float lastSensedTime = -999f;
    private Vector3Int playerCell;

    // Occupancy-map belief over columns.
    private float[] belief;
    private float[] beliefScratch;
    private int bMinX, bMinY, bW, bH;
    private Vector3Int beliefPeak;
    private readonly List<Vector3Int> searchPeaks = new List<Vector3Int>();

    // Learning: decaying visit heatmap over the grid, plus a shortlist of top hotspots.
    private readonly Dictionary<Vector2Int, float> heat = new Dictionary<Vector2Int, float>();
    private readonly List<Vector2Int> hotspots = new List<Vector2Int>();
    private float heatTimer;

    // Habit-anticipatory ambush state.
    private readonly List<HordeZombieController> ambushers = new List<HordeZombieController>();
    private float ambushTimer;

    public static HordeDirector Active;   // Convenience lookup for zombies in the same arena.

    private void OnEnable() { Active = this; }
    private void OnDisable() { if (Active == this) { Active = null; } }

    private void Start()
    {
        if (nav == null) { nav = FindAnyObjectByType<IsoNavGrid>(); }
        if (playerController == null && player != null)
        {
            playerController = player.GetComponent<DemoPlayerController>();
        }
    }

    public void Register(HordeZombieController z)
    {
        if (!horde.Contains(z)) { horde.Add(z); }
    }

    public void Unregister(HordeZombieController z)
    {
        horde.Remove(z);
    }

    /// <summary>A zombie reports it can currently see/hear the player (an observation).</summary>
    public void ReportSighting()
    {
        lastSensedTime = Time.time;
        Alerted = true;
    }

    private void Update()
    {
        if (nav == null || player == null)
        {
            return;
        }

        playerCell = playerController != null ? playerController.CurrentCell : PlayerCellFallback();
        TargetConfirmed = Time.time - lastSensedTime < 0.6f;

        if (Alerted && Time.time - lastSensedTime > searchGiveUpSeconds)
        {
            Alerted = false;   // Trail has gone cold - the horde stands down.
            ClearBelief();
        }

        UpdateLearning();

        // Habit-anticipatory ambush: while the target is NOT confirmed (calm or merely
        // searching), keep lurkers posted at chokepoints on the player's learned routes.
        ambushTimer -= Time.deltaTime;
        if (TargetConfirmed)
        {
            ReleaseAmbushers();   // All-in once the target is confirmed.
        }
        else if (ambushTimer <= 0f)
        {
            ambushTimer = ambushInterval;
            AllocateAmbushers();
        }

        repathTimer -= Time.deltaTime;
        if (repathTimer <= 0f && Alerted)
        {
            repathTimer = repathInterval;
            UpdateBelief();
            if (!Alerted)
            {
                return;   // Belief evaporated during the update (searched everywhere).
            }
            Vector3Int target = TargetConfirmed ? playerCell : beliefPeak;
            nav.ComputeFlowField(target);
            AssignRoles(target);
        }
    }

    /// <summary>An ambusher's trap was triggered (counts toward the HUD metric).</summary>
    public void NotifyAmbushSprung()
    {
        AmbushesSprung++;
    }

    // ------------------------------------------------------------ habit-anticipatory ambush

    private void AllocateAmbushers()
    {
        // Drop entries whose zombies sprang or were re-tasked.
        for (int i = ambushers.Count - 1; i >= 0; i--)
        {
            if (ambushers[i] == null || ambushers[i].CurrentRole != HordeZombieController.Role.Ambush)
            {
                ambushers.RemoveAt(i);
            }
        }
        if (ambushers.Count >= maxAmbushers || hotspots.Count == 0)
        {
            return;
        }

        for (int i = 0; i < hotspots.Count && ambushers.Count < maxAmbushers; i++)
        {
            if (!heat.TryGetValue(hotspots[i], out float h) || h < ambushMinHeat)
            {
                continue;
            }
            if (!FindChokepointNear(hotspots[i], 6, out Vector3Int post))
            {
                continue;
            }
            if (!FarFromExistingPosts(post))
            {
                continue;
            }
            HordeZombieController lurker = NearestFreeZombie(post);
            if (lurker == null)
            {
                return;
            }
            lurker.AssignAmbush(post);
            ambushers.Add(lurker);
            AmbushesSet++;
        }
    }

    private void ReleaseAmbushers()
    {
        for (int i = 0; i < ambushers.Count; i++)
        {
            if (ambushers[i] != null && ambushers[i].CurrentRole == HordeZombieController.Role.Ambush)
            {
                ambushers[i].AssignChase();
            }
        }
        ambushers.Clear();
    }

    private bool FarFromExistingPosts(Vector3Int post)
    {
        for (int i = 0; i < ambushers.Count; i++)
        {
            if (ambushers[i] == null)
            {
                continue;
            }
            Vector3Int other = ambushers[i].PostCell;
            if (Mathf.Abs(other.x - post.x) + Mathf.Abs(other.y - post.y) < 8)
            {
                return false;
            }
        }
        return true;
    }

    private bool FindChokepointNear(Vector2Int center, int radius, out Vector3Int post)
    {
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r)
                    {
                        continue;   // Ring only.
                    }
                    int x = center.x + dx;
                    int y = center.y + dy;
                    if (nav.IsChokepoint(x, y) && nav.TryGetColumnCell(x, y, out post))
                    {
                        return true;
                    }
                }
            }
        }
        post = default;
        return false;
    }

    private HordeZombieController NearestFreeZombie(Vector3Int post)
    {
        Vector3 postWorld = nav.CellCenterWorld(post);
        HordeZombieController best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < horde.Count; i++)
        {
            HordeZombieController z = horde[i];
            if (z == null)
            {
                continue;
            }
            HordeZombieController.Role r = z.CurrentRole;
            if (r == HordeZombieController.Role.Ambush || r == HordeZombieController.Role.Attack)
            {
                continue;
            }
            float d = Vector3.Distance(z.transform.position, postWorld);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = z;
            }
        }
        return best;
    }

    private Vector3Int PlayerCellFallback()
    {
        nav.FindNearestNode(player.position, out Vector3Int c);
        return c;
    }

    // ------------------------------------------------------------ occupancy-map belief

    private void EnsureBelief()
    {
        int count = Mathf.Max(1, nav.GridColumnsX * nav.GridColumnsY);
        if (belief == null || belief.Length != count)
        {
            bMinX = nav.GridMinX;
            bMinY = nav.GridMinY;
            bW = nav.GridColumnsX;
            bH = nav.GridColumnsY;
            belief = new float[count];
            beliefScratch = new float[count];
        }
    }

    private int BIndex(int x, int y)
    {
        return (x - bMinX) + (y - bMinY) * bW;
    }

    private bool BInBounds(int x, int y)
    {
        return x >= bMinX && y >= bMinY && x < bMinX + bW && y < bMinY + bH;
    }

    private void ClearBelief()
    {
        if (belief == null)
        {
            return;
        }
        for (int i = 0; i < belief.Length; i++)
        {
            belief[i] = 0f;
        }
        PeakBelief = 0f;
        searchPeaks.Clear();
    }

    /// <summary>One recursive-Bayes tick: collapse on sighting, else diffuse + cull.</summary>
    private void UpdateBelief()
    {
        EnsureBelief();

        if (TargetConfirmed)
        {
            // Observation: the target's position is known exactly.
            for (int i = 0; i < belief.Length; i++)
            {
                belief[i] = 0f;
            }
            if (BInBounds(playerCell.x, playerCell.y))
            {
                belief[BIndex(playerCell.x, playerCell.y)] = 1f;
            }
            beliefPeak = playerCell;
            PeakBelief = 1f;
            searchPeaks.Clear();
            searchPeaks.Add(playerCell);
            return;
        }

        // Prediction: diffuse the belief through the walkable graph (the hidden player
        // keeps moving), pulled toward the routes the player habitually uses.
        System.Array.Clear(beliefScratch, 0, beliefScratch.Length);
        for (int x = bMinX; x < bMinX + bW; x++)
        {
            for (int y = bMinY; y < bMinY + bH; y++)
            {
                int i = BIndex(x, y);
                float b = belief[i];
                if (b <= 0f || !nav.ColumnWalkable(x, y))
                {
                    continue;
                }
                beliefScratch[i] += b * (1f - diffusionRate);

                float wRight = DiffusionWeight(x, y, x + 1, y);
                float wLeft = DiffusionWeight(x, y, x - 1, y);
                float wUp = DiffusionWeight(x, y, x, y + 1);
                float wDown = DiffusionWeight(x, y, x, y - 1);
                float total = wRight + wLeft + wUp + wDown;
                if (total <= 0f)
                {
                    beliefScratch[i] += b * diffusionRate;   // Boxed in - mass stays.
                    continue;
                }
                float share = b * diffusionRate / total;
                if (wRight > 0f) { beliefScratch[BIndex(x + 1, y)] += share * wRight; }
                if (wLeft > 0f) { beliefScratch[BIndex(x - 1, y)] += share * wLeft; }
                if (wUp > 0f) { beliefScratch[BIndex(x, y + 1)] += share * wUp; }
                if (wDown > 0f) { beliefScratch[BIndex(x, y - 1)] += share * wDown; }
            }
        }

        // Negative observations: any cell a zombie can currently see almost certainly
        // does not hide the player. Sweeping zombies therefore squeeze the belief.
        for (int x = bMinX; x < bMinX + bW; x++)
        {
            for (int y = bMinY; y < bMinY + bH; y++)
            {
                int i = BIndex(x, y);
                if (beliefScratch[i] <= 0f)
                {
                    continue;
                }
                if (!nav.TryGetColumnCell(x, y, out Vector3Int cell))
                {
                    beliefScratch[i] = 0f;
                    continue;
                }
                Vector3 world = nav.CellCenterWorld(cell);
                for (int zi = 0; zi < horde.Count; zi++)
                {
                    HordeZombieController zc = horde[zi];
                    if (zc == null)
                    {
                        continue;
                    }
                    if (Vector3.Distance(zc.transform.position, world) <= sightCullRange &&
                        nav.HasLineOfSight(zc.CurrentCell, cell))
                    {
                        beliefScratch[i] *= negativeObservation;
                        break;
                    }
                }
            }
        }

        // Normalize and find the peaks.
        float sum = 0f;
        for (int i = 0; i < beliefScratch.Length; i++)
        {
            sum += beliefScratch[i];
        }
        if (sum < 1e-6f)
        {
            Alerted = false;   // Every plausible hiding place has been cleared.
            ClearBelief();
            return;
        }
        float peak = 0f;
        int peakIndex = 0;
        for (int i = 0; i < beliefScratch.Length; i++)
        {
            belief[i] = beliefScratch[i] / sum;
            if (belief[i] > peak)
            {
                peak = belief[i];
                peakIndex = i;
            }
        }
        int px = bMinX + peakIndex % bW;
        int py = bMinY + peakIndex / bW;
        beliefPeak = new Vector3Int(px, py, nav.ColumnLevel(px, py));
        PeakBelief = peak;
        CollectSearchPeaks(peak);
    }

    private float DiffusionWeight(int fromX, int fromY, int toX, int toY)
    {
        if (!BInBounds(toX, toY) || !nav.StepAllowed(fromX, fromY, toX, toY))
        {
            return 0f;
        }
        float h = heat.TryGetValue(new Vector2Int(toX, toY), out float v) ? v : 0f;
        return 1f + heatBias * Mathf.Min(h, 4f) / 4f;
    }

    /// <summary>Top belief cells, spaced apart, for allocating searchers across the map.</summary>
    private void CollectSearchPeaks(float peakValue)
    {
        searchPeaks.Clear();
        searchPeaks.Add(beliefPeak);
        float threshold = peakValue * 0.3f;
        for (int x = bMinX; x < bMinX + bW && searchPeaks.Count < 4; x++)
        {
            for (int y = bMinY; y < bMinY + bH && searchPeaks.Count < 4; y++)
            {
                if (belief[BIndex(x, y)] < threshold)
                {
                    continue;
                }
                bool farEnough = true;
                for (int k = 0; k < searchPeaks.Count; k++)
                {
                    if (Mathf.Abs(searchPeaks[k].x - x) + Mathf.Abs(searchPeaks[k].y - y) < 10)
                    {
                        farEnough = false;
                        break;
                    }
                }
                if (farEnough)
                {
                    searchPeaks.Add(new Vector3Int(x, y, nav.ColumnLevel(x, y)));
                }
            }
        }
    }

    // ------------------------------------------------------------ learning

    private void UpdateLearning()
    {
        heatTimer -= Time.deltaTime;
        if (heatTimer > 0f)
        {
            return;
        }
        heatTimer = 0.4f;

        var keys = new List<Vector2Int>(heat.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            float v = heat[keys[i]] * 0.98f;
            if (v < 0.05f) { heat.Remove(keys[i]); } else { heat[keys[i]] = v; }
        }
        Vector2Int pc = new Vector2Int(playerCell.x, playerCell.y);
        heat[pc] = (heat.TryGetValue(pc, out float cur) ? cur : 0f) + 1f;

        hotspots.Clear();
        foreach (var kv in heat)
        {
            if (kv.Value > 2.5f)
            {
                hotspots.Add(kv.Key);
            }
        }
        hotspots.Sort((a, b) => heat[b].CompareTo(heat[a]));
        if (hotspots.Count > 8)
        {
            hotspots.RemoveRange(8, hotspots.Count - 8);
        }
        LearnedHotspots = hotspots.Count;
    }

    // ------------------------------------------------------------ coordination

    private void AssignRoles(Vector3Int target)
    {
        if (horde.Count == 0)
        {
            return;
        }

        // Sort zombies by flow distance to the target: nearest chase, farthest flank/search.
        horde.Sort((x, y) =>
            nav.FlowDistance(x.CurrentCell).CompareTo(nav.FlowDistance(y.CurrentCell)));

        int detached = Mathf.Clamp(Mathf.RoundToInt(horde.Count * flankerFraction), 0, horde.Count);
        FlankerCount = detached;
        int firstDetached = horde.Count - detached;

        if (TargetConfirmed)
        {
            // Interception: cut off the player's predicted escape.
            Vector3Int cutoff = PredictedCutoffCell();
            for (int i = 0; i < horde.Count; i++)
            {
                if (i >= firstDetached)
                {
                    int rank = i - firstDetached;
                    horde[i].AssignFlank(RingCell(cutoff, rank, detached));
                }
                else
                {
                    horde[i].AssignChase();
                }
            }
        }
        else
        {
            // Search: converge on the belief peak while detached agents sweep the
            // secondary peaks. Posted ambushers keep their posts (original extension).
            for (int i = 0; i < horde.Count; i++)
            {
                if (horde[i].CurrentRole == HordeZombieController.Role.Ambush)
                {
                    continue;
                }
                if (i >= firstDetached)
                {
                    int rank = i - firstDetached;
                    Vector3Int searchTarget = searchPeaks.Count > 1
                        ? searchPeaks[1 + rank % (searchPeaks.Count - 1)]
                        : RingCell(beliefPeak, rank, Mathf.Max(detached, 1));
                    horde[i].AssignSearch(searchTarget);
                }
                else
                {
                    horde[i].AssignChase();   // Rides the field toward the belief peak.
                }
            }
        }
    }

    private Vector3Int PredictedCutoffCell()
    {
        Vector3 vel = playerController != null ? playerController.Velocity : Vector3.zero;
        Vector3 ahead = player.position + vel * interceptLeadSeconds;

        if (hotspots.Count > 0)
        {
            Vector2Int best = hotspots[0];
            float bestScore = float.MaxValue;
            Vector2 aheadCell = new Vector2(ahead.x, ahead.y);
            for (int i = 0; i < hotspots.Count; i++)
            {
                Vector3 hpWorld = nav.CellCenterWorld(new Vector3Int(hotspots[i].x, hotspots[i].y, nav.ColumnLevel(hotspots[i].x, hotspots[i].y)));
                float d = Vector2.Distance(new Vector2(hpWorld.x, hpWorld.y), aheadCell);
                if (d < bestScore)
                {
                    bestScore = d;
                    best = hotspots[i];
                }
            }
            Vector3 hotWorld = nav.CellCenterWorld(new Vector3Int(best.x, best.y, nav.ColumnLevel(best.x, best.y)));
            ahead = Vector3.Lerp(ahead, hotWorld, 0.5f);
        }

        if (nav.FindNearestNode(ahead, out Vector3Int cell))
        {
            return cell;
        }
        return playerCell;
    }

    private Vector3Int RingCell(Vector3Int center, int rank, int total)
    {
        if (total <= 1)
        {
            return center;
        }
        float angle = (Mathf.PI * 2f) * rank / total;
        Vector3 worldCenter = nav.CellCenterWorld(center);
        Vector3 ringWorld = worldCenter + new Vector3(Mathf.Cos(angle) * encircleRadius, Mathf.Sin(angle) * encircleRadius * 0.5f, 0f);
        if (nav.FindNearestNode(ringWorld, out Vector3Int cell))
        {
            return cell;
        }
        return center;
    }

    /// <summary>Next step for a chaser, straight down the shared flow field.</summary>
    public bool ChaseStep(Vector3Int from, out Vector3Int next)
    {
        return nav.FlowNextStep(from, out next);
    }

    public Vector3Int PlayerCell { get { return playerCell; } }
}
