using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Level 2 zombie. Unlike Level 1's NpcController (independent A* to the player's cell),
/// this zombie takes its orders from the HordeDirector:
/// - CHASE role: steps straight down the shared flow field toward the player (cheap, O(1)).
/// - FLANK role: A* to a director-assigned cut-off cell ahead of / around the player, to
///   help the horde encircle and corner.
/// It still moves through a GridBody, so it obeys the exact same wall / obstacle / cliff /
/// ramp collision rules as everything else, and it shares SpriteDirectionalAnimator,
/// PlayerHealth and the YSorter with Level 1.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class HordeZombieController : MonoBehaviour
{
    public enum Role { Patrol, Chase, Flank, Search, Ambush, Attack }

    [Header("Wiring")]
    public IsoNavGrid nav;
    public HordeDirector director;
    public Transform player;
    public DemoPlayerController playerController;
    public PlayerHealth playerHealth;
    public Transform[] patrolPoints;

    [Header("Movement / senses")]
    public string displayName = "Zombie";
    public float patrolSpeed = 1.0f;
    public float huntSpeed = 2.0f;
    public float sightRange = 8f;
    public float hearingRange = 11f;
    public float attackRange = 0.8f;
    public float attackCooldown = 1.2f;
    public int attackDamage = 1;
    public float wanderRadius = 6f;
    [Tooltip("A lying-in-wait ambusher springs when the player gets this close (world units).")]
    public float ambushTriggerRange = 4f;
    public bool showStateLabel = true;
    public bool useStartCell;
    public Vector3Int startCell;

    public Vector3Int CurrentCell { get { return body != null ? body.Cell : startCell; } }
    public Vector3 Velocity { get; private set; }
    public Role CurrentRole { get { return role; } }
    public bool IsHunting { get { return role == Role.Chase || role == Role.Flank || role == Role.Search || role == Role.Attack; } }

    /// <summary>The cell an ambusher is holding (its assigned post).</summary>
    public Vector3Int PostCell { get { return flankTarget; } }

    /// <summary>All live horde zombies (the survivor NPC scans this to know what to fear).</summary>
    public static readonly List<HordeZombieController> Registry = new List<HordeZombieController>();

    private void OnEnable()
    {
        Registry.Add(this);
    }

    private void OnDisable()
    {
        Registry.Remove(this);
    }

    private Role role = Role.Patrol;
    private GridBody body;
    private SpriteRenderer spriteRenderer;
    private TextMesh label;

    private Vector3Int flankTarget;
    private readonly List<Vector3Int> path = new List<Vector3Int>();
    private int pathIndex;
    private float repathTimer;
    private float attackTimer;
    private float waitTimer;
    private int patrolIndex;

    private const float ReachDistance = 0.06f;

    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (nav == null) { nav = FindAnyObjectByType<IsoNavGrid>(); }
        if (director == null) { director = HordeDirector.Active; }
        if (playerController == null && player != null)
        {
            playerController = player.GetComponent<DemoPlayerController>();
        }

        body = GetComponent<GridBody>();
        if (body == null) { body = gameObject.AddComponent<GridBody>(); }

        Vector3Int spawn = startCell;
        if (!useStartCell && !nav.FindNearestNode(transform.position, out spawn))
        {
            spawn = startCell;
        }
        body.Initialize(nav, spawn);

        if (director != null) { director.Register(this); }
        if (showStateLabel) { CreateLabel(); }
    }

    private void OnDestroy()
    {
        if (director != null) { director.Unregister(this); }
    }

    // Role assignments come from the director each planning tick.
    public void AssignChase()
    {
        if (role != Role.Attack) { role = Role.Chase; }
    }

    public void AssignFlank(Vector3Int target)
    {
        flankTarget = target;
        if (role != Role.Attack && role != Role.Flank)
        {
            role = Role.Flank;
            repathTimer = 0f;
        }
    }

    /// <summary>Search role: sweep an assigned belief peak (target hidden, not confirmed).</summary>
    public void AssignSearch(Vector3Int target)
    {
        flankTarget = target;
        if (role != Role.Attack && role != Role.Search)
        {
            role = Role.Search;
            repathTimer = 0f;
        }
    }

    /// <summary>Ambush role: sneak to a chokepoint post on a learned route and lie in wait.</summary>
    public void AssignAmbush(Vector3Int post)
    {
        flankTarget = post;
        if (role != Role.Attack && role != Role.Ambush)
        {
            role = Role.Ambush;
            repathTimer = 0f;
            ClearPath();
        }
    }

    private void Update()
    {
        if (nav == null || player == null)
        {
            return;
        }

        attackTimer -= Time.deltaTime;
        repathTimer -= Time.deltaTime;

        float distance = Vector3.Distance(transform.position, player.position);
        bool sprinting = playerController != null && playerController.IsSprinting;
        bool canSee = distance <= sightRange && nav.HasLineOfSight(CurrentCell, PlayerCell());
        bool canHear = sprinting && distance <= hearingRange;
        if ((canSee || canHear) && director != null)
        {
            director.ReportSighting();
        }

        bool alerted = director != null && director.Alerted;

        // Survivors are prey too: a living survivor in sight takes priority over the
        // abstract target whenever it is closer than the player.
        NpcController prey = NearestVisibleSurvivor(out Vector3Int preyCell, out float preyDistance);

        Velocity = Vector3.zero;
        if (role == Role.Ambush)
        {
            AmbushTick(distance);   // Holds its post whether the horde is alerted or not.
        }
        else if (alerted && prey != null && preyDistance <= attackRange)
        {
            role = Role.Attack;
            AttackSurvivor(prey, preyDistance);
        }
        else if (distance <= attackRange && alerted)
        {
            role = Role.Attack;
            DoAttack(distance);
        }
        else if (alerted && prey != null && preyDistance < distance)
        {
            role = Role.Chase;
            PursueCell(preyCell);
        }
        else if (alerted)
        {
            if (role == Role.Attack) { role = Role.Chase; }
            Hunt();
        }
        else
        {
            if (role != Role.Patrol) { role = Role.Patrol; ClearPath(); }
            Patrol();
        }

        body.Apply();
        UpdateLabel();
    }

    // ------------------------------------------------------------ ambush (original extension)

    /// <summary>Sneak to the assigned chokepoint post, hold perfectly still, spring the trap.</summary>
    private void AmbushTick(float distanceToPlayer)
    {
        if (!PathFinished())
        {
            StepAlongPath(patrolSpeed);   // Approach quietly at shamble speed.
        }
        else if (CurrentCell != flankTarget && repathTimer <= 0f)
        {
            repathTimer = 0.6f;
            if (nav.FindPath(CurrentCell, flankTarget, path))
            {
                pathIndex = (path.Count > 1 && path[0] == CurrentCell) ? 1 : 0;
            }
        }

        // Spring: the player wandered into the trap.
        if (distanceToPlayer <= ambushTriggerRange)
        {
            role = Role.Chase;
            if (director != null)
            {
                director.ReportSighting();      // The ambush is itself an observation.
                director.NotifyAmbushSprung();
            }
        }
    }

    // ------------------------------------------------------------ hunting

    private void Hunt()
    {
        if (role == Role.Flank || role == Role.Search)
        {
            // Path to the assigned cut-off / search cell; fall back to chasing the flow
            // field once we arrive (or if no route exists).
            Role detachedRole = role;
            if (repathTimer <= 0f || PathFinished())
            {
                repathTimer = 0.4f;
                if (!nav.FindPath(CurrentCell, flankTarget, path))
                {
                    role = Role.Chase;
                }
                else
                {
                    pathIndex = (path.Count > 1 && path[0] == CurrentCell) ? 1 : 0;
                }
            }
            if (role == detachedRole && !PathFinished())
            {
                StepAlongPath(huntSpeed);
                return;
            }
            role = Role.Chase;
        }

        // Chase: descend the shared flow field toward the player.
        if (director != null && director.ChaseStep(CurrentCell, out Vector3Int next))
        {
            MoveTowardCell(next, huntSpeed);
        }
    }

    // ------------------------------------------------------------ patrolling / wander

    private void Patrol()
    {
        if (patrolPoints != null && patrolPoints.Length > 0)
        {
            if (PathFinished())
            {
                waitTimer += Time.deltaTime;
                if (waitTimer > 1.2f)
                {
                    waitTimer = 0f;
                    patrolIndex = (patrolIndex + 1) % patrolPoints.Length;
                    if (nav.FindNearestNode(patrolPoints[patrolIndex].position, out Vector3Int c))
                    {
                        SetPath(c);
                    }
                }
            }
            else
            {
                StepAlongPath(patrolSpeed);
            }
            return;
        }

        // Wander to a random reachable cell.
        if (PathFinished())
        {
            waitTimer += Time.deltaTime;
            if (waitTimer > 1.5f)
            {
                waitTimer = 0f;
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float radius = Random.Range(2f, wanderRadius);
                Vector3 target = transform.position + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius * 0.5f, 0f);
                if (nav.FindNearestNode(target, out Vector3Int c) && c != CurrentCell)
                {
                    SetPath(c);
                }
            }
        }
        else
        {
            StepAlongPath(patrolSpeed);
        }
    }

    // ------------------------------------------------------------ movement helpers

    private void SetPath(Vector3Int goal)
    {
        if (nav.FindPath(CurrentCell, goal, path))
        {
            pathIndex = (path.Count > 1 && path[0] == CurrentCell) ? 1 : 0;
        }
        else
        {
            ClearPath();
        }
    }

    private void ClearPath()
    {
        path.Clear();
        pathIndex = 0;
    }

    private bool PathFinished()
    {
        return pathIndex >= path.Count;
    }

    private void StepAlongPath(float speed)
    {
        if (PathFinished())
        {
            return;
        }
        if (MoveTowardCell(path[pathIndex], speed))
        {
            pathIndex++;
        }
    }

    /// <summary>Steers flat toward a cell center through the collision body. True when reached.</summary>
    private bool MoveTowardCell(Vector3Int cell, float speed)
    {
        Vector3 targetFlat = nav.CellSpaceToWorld(nav.CellCenterCellSpace(cell), 0f);
        Vector3 currentFlat = body.FlatWorldPosition;
        float directionalSpeed = GridBody.DirectionalSpeed(speed, targetFlat - currentFlat);
        Vector3 stepped = Vector3.MoveTowards(currentFlat, targetFlat, directionalSpeed * Time.deltaTime);
        body.MoveWorld(stepped - currentFlat);

        Vector3 moved = body.FlatWorldPosition - currentFlat;
        Velocity = Time.deltaTime > 0f ? moved / Time.deltaTime : Vector3.zero;
        if (Mathf.Abs(moved.x) > 0.0001f)
        {
            spriteRenderer.flipX = moved.x < 0f;
        }
        return Vector3.Distance(body.FlatWorldPosition, targetFlat) < ReachDistance;
    }

    private void DoAttack(float distance)
    {
        FacePlayer();
        if (attackTimer <= 0f)
        {
            attackTimer = attackCooldown;
            if (playerHealth != null)
            {
                playerHealth.TakeDamage(attackDamage, transform.position);
            }
        }
        if (distance > attackRange * 1.4f)
        {
            role = Role.Chase;
        }
    }

    private void AttackSurvivor(NpcController prey, float distance)
    {
        float dx = prey.transform.position.x - transform.position.x;
        if (Mathf.Abs(dx) > 0.0001f)
        {
            spriteRenderer.flipX = dx < 0f;
        }
        if (attackTimer <= 0f)
        {
            attackTimer = attackCooldown;
            prey.TakeDamage(attackDamage, transform.position);
        }
        if (distance > attackRange * 1.4f)
        {
            role = Role.Chase;
        }
    }

    /// <summary>Throttled A* pursuit of a concrete cell (used for chasing visible survivors).</summary>
    private void PursueCell(Vector3Int cell)
    {
        if (repathTimer <= 0f || PathFinished())
        {
            repathTimer = 0.3f;
            if (nav.FindPath(CurrentCell, cell, path))
            {
                pathIndex = (path.Count > 1 && path[0] == CurrentCell) ? 1 : 0;
            }
        }
        if (!PathFinished())
        {
            StepAlongPath(huntSpeed);
        }
    }

    private NpcController NearestVisibleSurvivor(out Vector3Int cell, out float distance)
    {
        NpcController best = null;
        distance = float.MaxValue;
        cell = default;
        List<NpcController> all = NpcController.Registry;
        for (int i = 0; i < all.Count; i++)
        {
            NpcController other = all[i];
            if (other == null || other.behavior != NpcController.NpcBehavior.Civilian || other.IsDead)
            {
                continue;
            }
            float d = Vector3.Distance(transform.position, other.transform.position);
            if (d > sightRange || d >= distance)
            {
                continue;
            }
            if (!nav.HasLineOfSight(CurrentCell, other.Cell))
            {
                continue;
            }
            best = other;
            distance = d;
            cell = other.Cell;
        }
        return best;
    }

    private void FacePlayer()
    {
        float dx = player.position.x - transform.position.x;
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
        nav.FindNearestNode(player.position, out Vector3Int c);
        return c;
    }

    // ------------------------------------------------------------ label

    private void CreateLabel()
    {
        GameObject labelObject = new GameObject("StateLabel");
        labelObject.transform.SetParent(transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
        label = labelObject.AddComponent<TextMesh>();
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.characterSize = 0.12f;
        label.fontSize = 40;
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (builtin != null)
        {
            label.font = builtin;
            labelObject.GetComponent<MeshRenderer>().material = builtin.material;
        }
        labelObject.GetComponent<MeshRenderer>().sortingOrder = 32000;
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
        label.text = displayName + ": " + role;
        switch (role)
        {
            case Role.Chase: label.color = new Color(1f, 0.35f, 0.3f); break;
            case Role.Flank: label.color = new Color(1f, 0.55f, 0.1f); break;
            case Role.Search: label.color = new Color(1f, 0.9f, 0.35f); break;
            case Role.Ambush: label.color = new Color(0.8f, 0.5f, 1f); break;
            case Role.Attack: label.color = Color.red; break;
            default: label.color = new Color(0.7f, 0.9f, 1f); break;
        }
    }
}
