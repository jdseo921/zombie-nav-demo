using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Scripted NPC for the pathfinding demo. Two behaviour profiles, built around the
/// steering behaviours (pursue / flee / wander / path following / obstacle avoidance)
/// and navigation-reliability goals described in the research report.
///
/// Zombie   — Patrols waypoints, or WANDERS to random reachable cells when it has none.
///            Sees the player within sight range (walls block line of sight), hears a
///            SPRINTING player through walls, and chases using A* across collisions,
///            walls and ramps, re-pathing as the player moves. Entering a chase it
///            "screams", alerting nearby zombies to investigate. Attacks when adjacent
///            (damaging the player), investigates the last known position when it loses
///            the trail, then returns to patrol. Archetypes (Walker/Runner) are tuned
///            through the speed/sense fields.
/// Civilian — Patrols waypoints; joins the player as a teammate (Follow) when the player
///            comes close, keeping a small distance. Panics and flees from ZOMBIES, not
///            the player, and calms down when the danger is gone. Offers short dialogue
///            hints when the player stands nearby.
///
/// All movement follows IsoNavGrid paths, so NPCs demonstrably respect walls,
/// obstacle colliders and elevation ramps.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class NpcController : MonoBehaviour
{
    public enum NpcBehavior { Zombie, Civilian }
    public enum NpcState { Patrol, Investigate, Chase, Attack, Flee, Follow }

    [Header("Wiring")]
    public IsoNavGrid nav;
    public Transform player;
    public DemoPlayerController playerController;
    public PlayerHealth playerHealth;
    public Transform[] patrolPoints;

    [Header("Behaviour")]
    public NpcBehavior behavior = NpcBehavior.Zombie;
    [Tooltip("Shown in the state label, e.g. 'Walker' or 'Runner'.")]
    public string displayName;
    public float patrolSpeed = 1.4f;
    public float activeSpeed = 2.6f;          // Chase, flee or follow speed.
    [Tooltip("Wander range used while patrolling with no patrol points (Reynolds-style wander).")]
    public float wanderRadius = 6f;

    [Header("Zombie senses")]
    public float sightRange = 7f;
    public float sprintSightBonus = 1.3f;     // Sprinting players are easier to spot.
    public float hearingRange = 10f;          // Sprinting is heard through walls.
    public float attackRange = 0.8f;
    public float attackCooldown = 1.2f;
    public int attackDamage = 1;
    public float loseSightGrace = 2.5f;       // Keeps chasing this long after losing sight.
    [Tooltip("Starting a chase alerts other patrolling zombies within this range.")]
    public float alertRadius = 10f;

    [Header("Civilian nerves")]
    public float panicRange = 5f;             // Distance at which a VISIBLE zombie causes panic.
    public float calmDelay = 1.5f;            // Calms down this long after last sensing a threat.
    public float fleeDistance = 8f;
    public bool followPlayerWhenSafe;
    public float followEngageRange = 4.5f;    // Starts following when the player is this close.
    public float followDistance = 2f;         // Keeps roughly this distance while following.
    public float followLeashRange = 12f;      // Stops following when the player is this far.
    [Tooltip("Follow walk pace; set to the player's walk speed so escorts keep step exactly.")]
    public float followSpeed = 3f;
    [Tooltip("Hold near the spawn point (tiny shuffle) until the player arrives or a zombie threatens - never wanders into the horde alone. Re-anchors wherever the player leaves them.")]
    public bool anchoredUntilContact;
    [Tooltip("Dialogue lines shown when the player stands nearby (full health).")]
    public string[] hintLines;
    [Tooltip("Dialogue when hurt (2 of 3 HP). Falls back to hintLines when empty.")]
    public string[] hintLinesHurt;
    [Tooltip("Dialogue when critical (1 HP). Falls back to hintLines when empty.")]
    public string[] hintLinesCritical;

    [Header("Civilian survival")]
    public int maxHealth = 3;
    public float damageInvulnerability = 1f;

    [Header("Demo helpers")]
    public bool showStateLabel = true;
    public bool useStartCell;
    public Vector3Int startCell;
    public UnityEvent onAttack;

    public NpcState CurrentState { get { return state; } }
    public Vector3 Velocity { get; private set; }
    public Vector3Int Cell { get { return currentCell; } }
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    /// <summary>Total stuck recoveries across all NPCs this run (shown on the metrics HUD).</summary>
    public static int StuckRecoveries;

    /// <summary>All live NpcControllers (zombies scan this for survivor prey, survivors for threats).</summary>
    public static readonly List<NpcController> Registry = new List<NpcController>();

    private NpcState state = NpcState.Patrol;
    private SpriteRenderer spriteRenderer;
    private GridBody body;
    private TextMesh label;
    private TextMesh hintLabel;
    private SpriteRenderer hintBubble;
    private SpriteRenderer hintBubbleTail;

    private static Sprite bubbleSprite;       // Shared 9-sliced rounded rectangle.
    private static Sprite bubbleTailSprite;   // Shared little down-pointing tail.

    private Vector3Int currentCell;
    private readonly List<Vector3Int> pathCells = new List<Vector3Int>();
    private readonly List<Vector3> pathPoints = new List<Vector3>();
    private int pathIndex;
    private Vector3Int pathGoal;

    private int patrolIndex;
    private float repathTimer;
    private float attackTimer;
    private float lastSeenTime = -999f;
    private float lastThreatTime = -999f;
    private Vector3Int investigateCell;
    private float waitTimer;
    private float stuckTimer;
    private float alertFlashTimer;
    private float speechEndTime;
    private int hintIndex;
    private string[] activeHintSet;

    // One survivor talks at a time, with a shared cooldown between utterances.
    private static NpcController currentSpeaker;
    private static float speechCooldownUntil;
    private float playerDistance;
    private NpcController victimSurvivor;   // Zombie brain: currently hunted survivor (null = player).
    private float invulnerabilityTimer;
    private float damageFlashTimer;
    private Color baseColor = Color.white;
    private bool anchored;
    private Vector3Int homeCell;

    private const float RepathInterval = 0.5f;
    private const float MinRepathInterval = 0.2f;
    private const float WaypointReachDistance = 0.06f;
    private const float StuckRecoveryDelay = 0.9f;
    private const float HintRange = 3.5f;
    private const float SpeechDuration = 4f;           // How long one utterance stays up.
    private const float SpeechCooldownSeconds = 5f;    // Global gap between utterances.
    private const float SpeechAverageInterval = 8f;    // Average extra wait before someone pipes up.

    /// <summary>Zombies bite survivors too. Only civilians take damage; dying fails the level.</summary>
    public void TakeDamage(int amount, Vector3 attackerPosition)
    {
        if (behavior != NpcBehavior.Civilian || IsDead || invulnerabilityTimer > 0f)
        {
            return;
        }
        invulnerabilityTimer = damageInvulnerability;
        damageFlashTimer = 0.35f;
        CurrentHealth -= amount;
        lastThreatTime = Time.time;
        if (state != NpcState.Flee)
        {
            state = NpcState.Flee;
            FleeFrom(attackerPosition);
        }
        if (CurrentHealth <= 0)
        {
            CurrentHealth = 0;
            IsDead = true;
            Velocity = Vector3.zero;
            ClearPath();
            spriteRenderer.color = new Color(0.55f, 0.3f, 0.3f);
            SpriteDirectionalAnimator anim = GetComponent<SpriteDirectionalAnimator>();
            if (anim != null)
            {
                anim.PlayDeath();
            }
            if (label != null)
            {
                label.text = displayName + ": DOWN";
                label.color = Color.red;
            }
            StopSpeaking(false);
            Debug.Log(displayName + " was killed - level failed.");
        }
        else
        {
            // A bite always gets a voice: reset the shared cooldown and take the stage
            // immediately (interrupting whoever was talking) with the new hurt line.
            speechCooldownUntil = 0f;
            if (currentSpeaker != null && currentSpeaker != this)
            {
                currentSpeaker.StopSpeaking(false);
            }
            if (hintLabel != null && DemoUI.ShowTips)
            {
                activeHintSet = CurrentHintSet();
                hintIndex = activeHintSet.Length - 1;   // Next pick = the set's first line.
                StartSpeaking();
            }
        }
    }

    private void OnEnable()
    {
        Registry.Add(this);
    }

    private void OnDisable()
    {
        Registry.Remove(this);
        if (currentSpeaker == this)
        {
            currentSpeaker = null;   // Never leave a destroyed speaker holding the stage.
        }
    }

    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        baseColor = spriteRenderer.color;
        CurrentHealth = maxHealth;
        if (nav == null)
        {
            nav = FindAnyObjectByType<IsoNavGrid>();
        }
        if (playerController == null && player != null)
        {
            playerController = player.GetComponent<DemoPlayerController>();
        }

        if (useStartCell)
        {
            currentCell = startCell;
        }
        else
        {
            nav.FindNearestNode(transform.position, out currentCell);
        }
        body = GetComponent<GridBody>();
        if (body == null)
        {
            body = gameObject.AddComponent<GridBody>();
        }
        body.Initialize(nav, currentCell);
        anchored = anchoredUntilContact;
        homeCell = currentCell;

        if (showStateLabel)
        {
            CreateLabel();
        }
        if (hintLines != null && hintLines.Length > 0)
        {
            CreateHintLabel();
        }
    }

    private void Update()
    {
        if (nav == null || player == null || IsDead)
        {
            return;
        }

        repathTimer -= Time.deltaTime;
        attackTimer -= Time.deltaTime;
        alertFlashTimer -= Time.deltaTime;
        invulnerabilityTimer -= Time.deltaTime;
        if (damageFlashTimer > 0f)
        {
            damageFlashTimer -= Time.deltaTime;
            spriteRenderer.color = Color.Lerp(baseColor, Color.red, Mathf.Clamp01(damageFlashTimer / 0.35f));
        }

        currentCell = body.Cell;
        playerDistance = Vector3.Distance(transform.position, player.position);
        bool playerSprinting = playerController != null && playerController.IsSprinting;
        Vector3Int playerCell = PlayerCell();

        if (behavior == NpcBehavior.Zombie)
        {
            UpdateZombie(playerDistance, playerSprinting, playerCell);
        }
        else
        {
            UpdateCivilian(playerDistance, playerCell);
        }

        float speed = patrolSpeed;
        if (state == NpcState.Chase || state == NpcState.Flee)
        {
            speed = activeSpeed;
        }
        else if (state == NpcState.Follow)
        {
            // Mirror the player's own gait: walk pace normally, sprint pace only while
            // the player sprints - an approaching escort is never faster than the player.
            speed = playerSprinting ? activeSpeed : followSpeed;
        }
        Velocity = Vector3.zero;
        if (state != NpcState.Attack)
        {
            FollowPath(speed);
        }
        body.Apply();
        WatchdogTick();
        UpdateLabel();
        UpdateHint();
    }

    // ---------------------------------------------------------------- zombie brain

    private void UpdateZombie(float distance, bool playerSprinting, Vector3Int playerCell)
    {
        float effectiveSight = playerSprinting ? sightRange * sprintSightBonus : sightRange;
        bool playerVisible = distance <= effectiveSight && nav.HasLineOfSight(currentCell, playerCell);
        bool canHear = playerSprinting && distance <= hearingRange;

        // Victim = nearest VISIBLE target; living survivors are prey just like the player.
        SelectVictim(playerVisible, distance, playerCell,
            out Vector3Int victimCell, out float victimDistance, out bool canSeeVictim, out NpcController seenSurvivor);

        if (canSeeVictim)
        {
            lastSeenTime = Time.time;
            investigateCell = victimCell;
            victimSurvivor = seenSurvivor;   // null = the player.
        }
        if (victimSurvivor != null && victimSurvivor.IsDead)
        {
            victimSurvivor = null;           // Fall back to hunting the player.
        }

        // Range/route to whichever victim this zombie is committed to.
        float chaseDistance;
        Vector3 chasePosition;
        Vector3Int chaseCell;
        if (victimSurvivor != null)
        {
            chasePosition = victimSurvivor.transform.position;
            chaseDistance = Vector3.Distance(transform.position, chasePosition);
            chaseCell = victimSurvivor.Cell;
        }
        else
        {
            chasePosition = player.position;
            chaseDistance = distance;
            chaseCell = playerCell;
        }

        switch (state)
        {
            case NpcState.Patrol:
                if (canSeeVictim)
                {
                    EnterChase(victimCell);
                }
                else if (canHear)
                {
                    state = NpcState.Investigate;
                    waitTimer = 0f;
                    investigateCell = playerCell;
                    TrySetDestination(investigateCell);
                }
                else
                {
                    PatrolTick();
                }
                break;

            case NpcState.Investigate:
                if (canSeeVictim)
                {
                    EnterChase(victimCell);
                }
                else if (canHear)
                {
                    investigateCell = playerCell;   // Keep tracking the noise.
                    if (repathTimer <= 0f)
                    {
                        TrySetDestination(investigateCell);
                        repathTimer = RepathInterval;
                    }
                }
                else if (PathFinished())
                {
                    waitTimer += Time.deltaTime;
                    if (waitTimer > 2f)
                    {
                        waitTimer = 0f;
                        state = NpcState.Patrol;
                        victimSurvivor = null;
                    }
                }
                break;

            case NpcState.Chase:
                if (chaseDistance <= attackRange)
                {
                    state = NpcState.Attack;
                    ClearPath();
                }
                else if (!canSeeVictim && Time.time - lastSeenTime > loseSightGrace)
                {
                    state = NpcState.Investigate;
                    waitTimer = 0f;
                    TrySetDestination(investigateCell);
                }
                else if ((chaseCell != pathGoal || PathFinished()) && repathTimer <= 0f)
                {
                    // Victim moved to a new cell: recompute the route (rate-limited).
                    TrySetDestination(chaseCell);
                    repathTimer = MinRepathInterval;
                }
                break;

            case NpcState.Attack:
                FaceTowards(chasePosition);
                if (attackTimer <= 0f)
                {
                    attackTimer = attackCooldown;
                    if (victimSurvivor != null)
                    {
                        Debug.Log(name + " attacks " + victimSurvivor.displayName + "!");
                        victimSurvivor.TakeDamage(attackDamage, transform.position);
                    }
                    else
                    {
                        Debug.Log(name + " attacks the player!");
                        if (playerHealth != null)
                        {
                            playerHealth.TakeDamage(attackDamage, transform.position);
                        }
                    }
                    if (onAttack != null)
                    {
                        onAttack.Invoke();
                    }
                }
                if (chaseDistance > attackRange * 1.4f)
                {
                    state = NpcState.Chase;
                }
                break;

            default:
                state = NpcState.Patrol;
                break;
        }
    }

    /// <summary>Nearest visible target: the player, or a living survivor if one is closer.</summary>
    private void SelectVictim(bool playerVisible, float playerDistance, Vector3Int playerCell,
        out Vector3Int victimCell, out float victimDistance, out bool visible, out NpcController survivor)
    {
        survivor = null;
        victimCell = playerCell;
        victimDistance = playerDistance;
        visible = playerVisible;
        for (int i = 0; i < Registry.Count; i++)
        {
            NpcController other = Registry[i];
            if (other == this || other.behavior != NpcBehavior.Civilian || other.IsDead)
            {
                continue;
            }
            float d = Vector3.Distance(transform.position, other.transform.position);
            if (d > sightRange || (visible && d >= victimDistance))
            {
                continue;
            }
            if (!nav.HasLineOfSight(currentCell, other.currentCell))
            {
                continue;
            }
            visible = true;
            victimDistance = d;
            victimCell = other.currentCell;
            survivor = other;
        }
    }

    private void EnterChase(Vector3Int playerCell)
    {
        if (state != NpcState.Chase)
        {
            state = NpcState.Chase;
            AlertNearbyZombies(playerCell);
        }
    }

    /// <summary>The "scream": patrolling zombies nearby move to investigate the target.</summary>
    private void AlertNearbyZombies(Vector3Int targetCell)
    {
        for (int i = 0; i < Registry.Count; i++)
        {
            NpcController other = Registry[i];
            if (other == this || other.behavior != NpcBehavior.Zombie)
            {
                continue;
            }
            if (other.state != NpcState.Patrol && other.state != NpcState.Investigate)
            {
                continue;
            }
            if (Vector3.Distance(other.transform.position, transform.position) > alertRadius)
            {
                continue;
            }
            other.state = NpcState.Investigate;
            other.waitTimer = 0f;
            other.investigateCell = targetCell;
            other.TrySetDestination(targetCell);
            other.alertFlashTimer = 1.5f;
        }
    }

    // ---------------------------------------------------------------- civilian brain

    private void UpdateCivilian(float distanceToPlayer, Vector3Int playerCell)
    {
        // Threat sensing: an idle zombie only scares the survivor when it is close AND
        // visible (walls/cliffs block sight); a zombie actively hunting scares it from
        // further away regardless. Standing near the player gives courage against
        // zombies that are not actively hunting. Scans BOTH zombie kinds (Level 1
        // NpcController zombies and Level 2 horde zombies).
        Vector3 threatPos;
        Vector3Int threatCell;
        bool aggro;
        float threatDistance = NearestZombie(out threatPos, out threatCell, out aggro);
        bool sensed = false;
        if (threatDistance < float.MaxValue)
        {
            float range = aggro ? panicRange * 1.5f : panicRange;
            sensed = threatDistance <= range &&
                     (aggro || nav.HasLineOfSight(currentCell, threatCell));
            if (sensed && !aggro && distanceToPlayer <= followEngageRange)
            {
                sensed = false;   // Safety in numbers.
            }
        }
        if (sensed)
        {
            lastThreatTime = Time.time;
        }

        // Anchored survivors hold near their spawn - a tiny shuffle radius - until the
        // player comes for them or a zombie forces them out. They RE-anchor wherever
        // the player leaves them, so they never wander into the horde on their own.
        if (anchored)
        {
            if (sensed)
            {
                anchored = false;   // Threat contact: the survival brain takes over.
            }
            else if (followPlayerWhenSafe && distanceToPlayer <= followEngageRange)
            {
                anchored = false;   // The player found them.
                state = NpcState.Follow;
            }
            else
            {
                state = NpcState.Patrol;
                AnchorTick();
                return;
            }
        }

        switch (state)
        {
            case NpcState.Patrol:
                if (sensed)
                {
                    state = NpcState.Flee;
                    FleeFrom(threatPos);
                }
                else if (followPlayerWhenSafe && distanceToPlayer <= followEngageRange)
                {
                    state = NpcState.Follow;
                }
                else
                {
                    PatrolTick();
                }
                break;

            case NpcState.Follow:
                if (sensed)
                {
                    state = NpcState.Flee;
                    FleeFrom(threatPos);
                }
                else if (distanceToPlayer > followLeashRange)
                {
                    Anchor(currentCell);   // Left behind: hold here until the player returns.
                    ClearPath();
                }
                else if (distanceToPlayer > followDistance + 0.6f)
                {
                    if ((playerCell != pathGoal || PathFinished()) && repathTimer <= 0f)
                    {
                        if (TrySetDestination(playerCell))
                        {
                            TrimLastWaypoint();   // Stand next to the player, not on them.
                        }
                        repathTimer = 0.3f;
                    }
                }
                else
                {
                    ClearPath();   // Close enough - wait.
                }
                break;

            case NpcState.Flee:
                if (Time.time - lastThreatTime > calmDelay)
                {
                    Anchor(currentCell);   // Hold here; re-follows the moment the player is near.
                    ClearPath();
                }
                else if (sensed && (repathTimer <= 0f || PathFinished()))
                {
                    FleeFrom(threatPos);
                    repathTimer = 0.4f;
                }
                break;

            default:
                state = NpcState.Patrol;
                break;
        }
    }

    /// <summary>
    /// Nearest zombie of EITHER kind (Level 1 NpcController zombies or Level 2 horde
    /// zombies). Returns float.MaxValue when none exist.
    /// </summary>
    private float NearestZombie(out Vector3 threatPos, out Vector3Int threatCell, out bool aggro)
    {
        threatPos = Vector3.zero;
        threatCell = default;
        aggro = false;
        float best = float.MaxValue;

        for (int i = 0; i < Registry.Count; i++)
        {
            NpcController other = Registry[i];
            if (other == this || other.behavior != NpcBehavior.Zombie)
            {
                continue;
            }
            float d = Vector3.Distance(transform.position, other.transform.position);
            if (d < best)
            {
                best = d;
                threatPos = other.transform.position;
                threatCell = other.currentCell;
                aggro = other.state == NpcState.Chase || other.state == NpcState.Attack;
            }
        }

        List<HordeZombieController> horde = HordeZombieController.Registry;
        for (int i = 0; i < horde.Count; i++)
        {
            HordeZombieController other = horde[i];
            if (other == null)
            {
                continue;
            }
            float d = Vector3.Distance(transform.position, other.transform.position);
            if (d < best)
            {
                best = d;
                threatPos = other.transform.position;
                threatCell = other.CurrentCell;
                aggro = other.IsHunting;
            }
        }
        return best;
    }

    private void FleeFrom(Vector3 threatPosition)
    {
        Vector2 away = (transform.position - threatPosition);
        if (away.sqrMagnitude < 0.001f)
        {
            away = Vector2.right;
        }
        away.Normalize();

        // Try straight away from the threat first, then rotate the escape direction.
        float[] angles = { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f };
        for (int i = 0; i < angles.Length; i++)
        {
            Vector2 dir = Rotate(away, angles[i]);
            Vector3 targetWorld = transform.position + new Vector3(dir.x, dir.y * 0.5f, 0f) * fleeDistance;
            Vector3Int targetCell;
            if (nav.FindNearestNode(targetWorld, out targetCell) && targetCell != currentCell)
            {
                if (TrySetDestination(targetCell))
                {
                    return;
                }
            }
        }
    }

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r);
        float sin = Mathf.Sin(r);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    // ---------------------------------------------------------------- movement

    private void PatrolTick()
    {
        if (patrolPoints == null || patrolPoints.Length == 0)
        {
            WanderTick();
            return;
        }
        if (PathFinished())
        {
            waitTimer += Time.deltaTime;
            if (waitTimer > 1.2f)
            {
                waitTimer = 0f;
                patrolIndex = (patrolIndex + 1) % patrolPoints.Length;
                Vector3Int targetCell;
                if (nav.FindNearestNode(patrolPoints[patrolIndex].position, out targetCell))
                {
                    TrySetDestination(targetCell);
                }
            }
        }
    }

    /// <summary>Drops anchor at a cell: hold position there (escort survivors only;
    /// plain civilians keep their patrol behaviour).</summary>
    private void Anchor(Vector3Int cell)
    {
        anchored = anchoredUntilContact;
        homeCell = cell;
        state = NpcState.Patrol;
    }

    /// <summary>Anchored hold: shuffle within a couple of cells of home, pausing between
    /// steps, so the survivor stays findable exactly where they were last seen.</summary>
    private void AnchorTick()
    {
        if (!PathFinished())
        {
            return;
        }
        waitTimer += Time.deltaTime;
        if (waitTimer < 2.4f)
        {
            return;
        }
        waitTimer = 0f;
        Vector3 targetWorld = nav.CellCenterWorld(homeCell) +
            new Vector3(Random.Range(-1f, 1f), Random.Range(-0.5f, 0.5f), 0f);
        Vector3Int targetCell;
        if (nav.FindNearestNode(targetWorld, out targetCell) && targetCell != currentCell)
        {
            TrySetDestination(targetCell);
        }
    }

    /// <summary>Reynolds-style wander: amble to a random reachable cell, pause, repeat.</summary>
    private void WanderTick()
    {
        if (!PathFinished())
        {
            return;
        }
        waitTimer += Time.deltaTime;
        if (waitTimer < 1.5f)
        {
            return;
        }
        waitTimer = 0f;
        float angle = Random.Range(0f, Mathf.PI * 2f);
        float radius = Random.Range(2f, wanderRadius);
        Vector3 targetWorld = transform.position + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius * 0.5f, 0f);
        Vector3Int targetCell;
        if (nav.FindNearestNode(targetWorld, out targetCell) && targetCell != currentCell)
        {
            TrySetDestination(targetCell);
        }
    }

    private bool TrySetDestination(Vector3Int goal)
    {
        // Record the goal even on failure so failed searches don't retry every frame.
        pathGoal = goal;
        if (!nav.FindPath(currentCell, goal, pathCells))
        {
            pathPoints.Clear();
            pathIndex = 0;
            return false;
        }
        pathPoints.Clear();
        for (int i = 0; i < pathCells.Count; i++)
        {
            pathPoints.Add(nav.CellCenterWorld(pathCells[i]));
        }
        // Skip the leading waypoint when it is the cell we are already in - walking
        // back to its center on every re-path is what caused the visible stutter.
        pathIndex = (pathCells.Count > 1 && pathCells[0] == currentCell) ? 1 : 0;
        return true;
    }

    private void TrimLastWaypoint()
    {
        if (pathCells.Count >= 2 && pathPoints.Count >= 2)
        {
            pathCells.RemoveAt(pathCells.Count - 1);
            pathPoints.RemoveAt(pathPoints.Count - 1);
            if (pathIndex > pathPoints.Count)
            {
                pathIndex = pathPoints.Count;
            }
        }
    }

    private void ClearPath()
    {
        pathCells.Clear();
        pathPoints.Clear();
        pathIndex = 0;
    }

    private bool PathFinished()
    {
        return pathIndex >= pathPoints.Count;
    }

    private void FollowPath(float speed)
    {
        if (PathFinished())
        {
            return;
        }
        // Steer in FLAT space (no elevation offset) and move through the collision body,
        // so an NPC's footprint can never overlap walls or cliffs even mid re-path.
        Vector3 targetFlat = nav.CellSpaceToWorld(nav.CellCenterCellSpace(pathCells[pathIndex]), 0f);
        Vector3 currentFlat = body.FlatWorldPosition;
        float directionalSpeed = GridBody.DirectionalSpeed(speed, targetFlat - currentFlat);
        Vector3 stepped = Vector3.MoveTowards(currentFlat, targetFlat, directionalSpeed * Time.deltaTime);
        body.MoveWorld(stepped - currentFlat);
        currentCell = body.Cell;

        // Use the ACTUAL movement after collision clamping, so the stuck watchdog and
        // the animator both see the truth when the body is blocked.
        Vector3 moved = body.FlatWorldPosition - currentFlat;
        Velocity = Time.deltaTime > 0f ? moved / Time.deltaTime : Vector3.zero;

        if (Mathf.Abs(moved.x) > 0.0001f)
        {
            spriteRenderer.flipX = moved.x < 0f;
        }

        if (Vector3.Distance(body.FlatWorldPosition, targetFlat) < WaypointReachDistance)
        {
            pathIndex++;
        }
    }

    /// <summary>
    /// Recovery for the rare cases where an NPC ends up without usable progress
    /// (repeated path failures, stale cell tracking). Re-syncs the grid cell and
    /// forces a fresh path. Each trigger increments the HUD's stuck counter.
    /// </summary>
    private void WatchdogTick()
    {
        bool wantsProgress =
            (!PathFinished() && Velocity.sqrMagnitude < 0.0009f && state != NpcState.Attack) ||
            (PathFinished() && (state == NpcState.Chase || state == NpcState.Flee));

        if (!wantsProgress)
        {
            stuckTimer = 0f;
            return;
        }
        stuckTimer += Time.deltaTime;
        if (stuckTimer < StuckRecoveryDelay)
        {
            return;
        }
        stuckTimer = 0f;
        StuckRecoveries++;

        Vector3Int resync;
        if (nav.FindNearestNode(transform.position, out resync))
        {
            body.Snap(resync);
            currentCell = body.Cell;
        }
        ClearPath();
        repathTimer = 0f;   // Let the state logic issue a fresh path immediately.
    }

    private void FaceTowards(Vector3 worldPosition)
    {
        float dx = worldPosition.x - transform.position.x;
        if (Mathf.Abs(dx) > 0.0001f)
        {
            spriteRenderer.flipX = dx < 0f;
        }
    }

    private Vector3Int PlayerCell()
    {
        if (playerController != null)
        {
            return playerController.CurrentCell;
        }
        Vector3Int cell;
        nav.FindNearestNode(player.position, out cell);
        return cell;
    }

    // ---------------------------------------------------------------- demo label / hints / gizmos

    private void CreateLabel()
    {
        label = MakeTextMesh("StateLabel", new Vector3(0f, 1.1f, 0f), 0.12f);
    }

    /// <summary>
    /// Dialogue speech bubble: white rounded bubble + tail sitting ABOVE the status
    /// label (which lives at y 1.1), so the two never overlap. The text mesh grows
    /// upward from its anchor; the bubble is laid out around it every frame it shows.
    /// </summary>
    private void CreateHintLabel()
    {
        hintLabel = MakeTextMesh("HintLabel", new Vector3(0f, 1.62f, 0f), 0.09f);
        hintLabel.color = new Color(0.12f, 0.10f, 0.08f);   // Dark ink on the white bubble.
        EnsureBubbleSprites();
        hintBubble = MakeBubbleRenderer("HintBubble", bubbleSprite, 31998);
        hintBubble.drawMode = SpriteDrawMode.Sliced;
        hintBubbleTail = MakeBubbleRenderer("HintBubbleTail", bubbleTailSprite, 31999);
    }

    private SpriteRenderer MakeBubbleRenderer(string objectName, Sprite sprite, int order)
    {
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;                 // Under the text (32000), above the world.
        sr.color = new Color(1f, 1f, 1f, 0.95f);
        sr.enabled = false;
        return sr;
    }

    private static void EnsureBubbleSprites()
    {
        if (bubbleSprite != null)
        {
            return;
        }

        // Rounded rectangle with a thin outline, 9-sliced so any bubble size keeps
        // crisp corners.
        const int S = 32;
        Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        Color clear = new Color(0f, 0f, 0f, 0f);
        Color ink = new Color(0.30f, 0.27f, 0.22f, 1f);
        for (int x = 0; x < S; x++)
        {
            for (int y = 0; y < S; y++)
            {
                bool outer = InsideRounded(x, y, S, 9f, 0f);
                bool inner = InsideRounded(x, y, S, 9f, 1.6f);
                tex.SetPixel(x, y, inner ? Color.white : outer ? ink : clear);
            }
        }
        tex.Apply();
        bubbleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 64f, 0,
            SpriteMeshType.FullRect, new Vector4(12f, 12f, 12f, 12f));

        // Down-pointing tail.
        const int TW = 14, TH = 10;
        Texture2D tail = new Texture2D(TW, TH, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int x = 0; x < TW; x++)
        {
            for (int y = 0; y < TH; y++)
            {
                float half = (y / (float)(TH - 1)) * (TW * 0.5f - 1f);
                tail.SetPixel(x, y, Mathf.Abs(x - (TW - 1) * 0.5f) <= half ? Color.white : clear);
            }
        }
        tail.Apply();
        bubbleTailSprite = Sprite.Create(tail, new Rect(0, 0, TW, TH), new Vector2(0.5f, 1f), 64f);
    }

    private static bool InsideRounded(int x, int y, int size, float radius, float inset)
    {
        float min = inset;
        float max = size - 1 - inset;
        if (x < min || y < min || x > max || y > max)
        {
            return false;
        }
        // Corner circles share centres regardless of inset (concentric outline ring).
        float r = radius - inset;
        float dx = Mathf.Max(Mathf.Max(min + r - x, x - (max - r)), 0f);
        float dy = Mathf.Max(Mathf.Max(min + r - y, y - (max - r)), 0f);
        return dx * dx + dy * dy <= r * r;
    }

    private TextMesh MakeTextMesh(string objectName, Vector3 localPosition, float characterSize)
    {
        GameObject labelObject = new GameObject(objectName);
        labelObject.transform.SetParent(transform, false);
        labelObject.transform.localPosition = localPosition;
        TextMesh text = labelObject.AddComponent<TextMesh>();
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.characterSize = characterSize;
        text.fontSize = 40;
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtin != null)
        {
            text.font = builtin;
            labelObject.GetComponent<MeshRenderer>().material = builtin.material;
        }
        labelObject.GetComponent<MeshRenderer>().sortingOrder = 32000;   // Always on top.
        return text;
    }

    private void UpdateLabel()
    {
        if (label == null)
        {
            return;
        }
        bool show = DemoUI.ShowHud;
        label.gameObject.SetActive(show);
        if (!show)
        {
            return;
        }
        string prefix = string.IsNullOrEmpty(displayName) ? "" : displayName + ": ";
        string suffix = alertFlashTimer > 0f ? " !" : "";
        string stateText = behavior == NpcBehavior.Civilian && anchored ? "Waiting" : state.ToString();
        label.text = prefix + stateText + suffix;

        if (alertFlashTimer > 0f)
        {
            label.color = new Color(1f, 0.55f, 0.1f);
            return;
        }
        switch (state)
        {
            case NpcState.Chase: label.color = new Color(1f, 0.35f, 0.3f); break;
            case NpcState.Attack: label.color = Color.red; break;
            case NpcState.Flee: label.color = new Color(1f, 0.85f, 0.3f); break;
            case NpcState.Investigate: label.color = new Color(1f, 0.7f, 0.4f); break;
            case NpcState.Follow: label.color = new Color(0.55f, 1f, 0.6f); break;
            default: label.color = new Color(0.7f, 0.9f, 1f); break;
        }
    }

    /// <summary>Dialogue set for the survivor's current condition (3 HP: calm, 2: hurt, 1: critical).</summary>
    private string[] CurrentHintSet()
    {
        if (CurrentHealth <= 1 && hintLinesCritical != null && hintLinesCritical.Length > 0)
        {
            return hintLinesCritical;
        }
        if (CurrentHealth == 2 && hintLinesHurt != null && hintLinesHurt.Length > 0)
        {
            return hintLinesHurt;
        }
        return hintLines;
    }

    private void UpdateHint()
    {
        if (hintLabel == null)
        {
            return;
        }
        string[] lines = CurrentHintSet();
        if (lines != activeHintSet)
        {
            activeHintSet = lines;   // Health changed: restart on the new dialogue set.
            hintIndex = lines != null && lines.Length > 0 ? lines.Length - 1 : 0;   // Next pick = line 0.
        }

        if (currentSpeaker == this)
        {
            // Keep the bubble up until the utterance ends (or the world invalidates it).
            if (!DemoUI.ShowTips || IsDead || Time.time >= speechEndTime)
            {
                StopSpeaking(true);
            }
            else
            {
                LayoutBubble();
            }
            return;
        }

        // Not the speaker: stay quiet. Randomly claim the stage when eligible - ONE
        // voice at a time, and only after the shared cooldown between utterances.
        bool eligible = DemoUI.ShowTips && behavior == NpcBehavior.Civilian && !IsDead &&
                        activeHintSet != null && activeHintSet.Length > 0 &&
                        playerDistance <= HintRange && state != NpcState.Flee;
        if (eligible && currentSpeaker == null && Time.time >= speechCooldownUntil &&
            Random.value < Time.deltaTime / SpeechAverageInterval)
        {
            StartSpeaking();
        }
    }

    /// <summary>Claims the shared stage and shows this survivor's next dialogue line.</summary>
    private void StartSpeaking()
    {
        currentSpeaker = this;
        speechEndTime = Time.time + SpeechDuration;
        hintIndex = (hintIndex + 1) % activeHintSet.Length;
        hintLabel.text = WrapText(activeHintSet[hintIndex], 26);
        LayoutBubble();
    }

    /// <summary>Releases the stage; optionally arms the shared between-utterance cooldown.</summary>
    private void StopSpeaking(bool armCooldown)
    {
        if (currentSpeaker == this)
        {
            currentSpeaker = null;
        }
        if (hintLabel != null)
        {
            hintLabel.text = "";
        }
        HideBubble();
        if (armCooldown)
        {
            speechCooldownUntil = Time.time + SpeechCooldownSeconds;
        }
    }

    /// <summary>Fits the bubble (and its tail) around the dialogue text every frame.</summary>
    private void LayoutBubble()
    {
        if (hintBubble == null || hintBubbleTail == null)
        {
            return;
        }
        Bounds textBounds = hintLabel.GetComponent<MeshRenderer>().bounds;
        hintBubble.enabled = true;
        hintBubbleTail.enabled = true;
        hintBubble.size = new Vector2(textBounds.size.x + 0.26f, textBounds.size.y + 0.18f);
        hintBubble.transform.position = textBounds.center;
        hintBubbleTail.transform.position = new Vector3(
            textBounds.center.x, textBounds.min.y - 0.055f, textBounds.center.z);
    }

    private void HideBubble()
    {
        if (hintBubble != null)
        {
            hintBubble.enabled = false;
        }
        if (hintBubbleTail != null)
        {
            hintBubbleTail.enabled = false;
        }
    }

    /// <summary>Word-wraps dialogue (TextMesh has no wrapping) so bubbles stay compact.</summary>
    private static string WrapText(string text, int maxLineLength)
    {
        string[] words = text.Split(' ');
        System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length + 8);
        int lineLength = 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (lineLength > 0 && lineLength + 1 + words[i].Length > maxLineLength)
            {
                sb.Append('\n');
                lineLength = 0;
            }
            else if (lineLength > 0)
            {
                sb.Append(' ');
                lineLength++;
            }
            sb.Append(words[i]);
            lineLength += words[i].Length;
        }
        return sb.ToString();
    }

    private void OnDrawGizmos()
    {
        if (pathPoints == null || pathPoints.Count < 2)
        {
            return;
        }
        Gizmos.color = behavior == NpcBehavior.Zombie ? Color.red : Color.yellow;
        for (int i = Mathf.Max(pathIndex, 1); i < pathPoints.Count; i++)
        {
            Gizmos.DrawLine(pathPoints[i - 1], pathPoints[i]);
        }
    }
}
