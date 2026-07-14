using UnityEngine;

/// <summary>
/// Frame-swapping animator for the demo NPCs - no Animator asset needed.
///
/// Picks an animation set from the NpcController's state (idle / walk / run / attack /
/// death), chooses front or rear frames from vertical movement direction, and mirrors
/// horizontally with flipX. Missing sets fall back gracefully (e.g. the survivor has
/// no rear sheets yet, so front frames are reused when walking away from the camera).
/// Runs in LateUpdate so it wins over any flipX written by the movement scripts.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class SpriteDirectionalAnimator : MonoBehaviour
{
    public NpcController npc;
    [Tooltip("When set, sprint state decides run frames instead of the speed threshold.")]
    public DemoPlayerController playerController;

    [Header("Frames")]
    public Sprite[] idleFront;
    public Sprite[] idleRear;
    public Sprite[] walkFront;
    public Sprite[] walkRear;
    public Sprite[] runFront;
    public Sprite[] runRear;
    public Sprite[] attackFront;
    public Sprite[] attackRear;
    public Sprite[] deathFront;
    public Sprite[] deathRear;

    [Header("Playback")]
    public float framesPerSecond = 6f;
    public float activeFramesPerSecond = 9f;   // Chase/flee/sprint playback speed.
    [Tooltip("Speed above which a controller-less actor (the player) uses run frames.")]
    public float runSpeedThreshold = 4f;
    [Tooltip("Which way the source art faces by default.")]
    public bool frontFacesLeft = true;
    public bool rearFacesLeft = false;

    private SpriteRenderer spriteRenderer;
    private float clock;
    private bool dead;
    private bool rearView;
    private bool faceLeft = true;
    private bool usingRear;
    private Sprite[] lastSet;
    private Vector3 lastPosition;
    private Vector3 fallbackVelocity;
    private float smoothedNominalSpeed;
    private bool runningGait;

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (npc == null)
        {
            npc = GetComponent<NpcController>();
        }
        lastPosition = transform.position;
    }

    /// <summary>NPC velocity when driven by an NpcController, transform delta otherwise.</summary>
    private Vector3 CurrentVelocity
    {
        get { return npc != null ? npc.Velocity : fallbackVelocity; }
    }

    /// <summary>
    /// Whether a controller-less actor should use run frames. Sprint intent beats the raw
    /// speed threshold: vertical/diagonal movement is slower in world units (iso foreshortening)
    /// and would otherwise never cross the threshold.
    /// </summary>
    private bool PlayerRunning
    {
        get
        {
            bool moving = CurrentVelocity.sqrMagnitude > 0.0004f;
            if (playerController != null)
            {
                return moving && playerController.IsSprinting;
            }
            return moving && CurrentVelocity.magnitude > runSpeedThreshold;
        }
    }

    /// <summary>Plays the death animation once and holds the final corpse frame.</summary>
    public void PlayDeath()
    {
        dead = true;
        clock = 0f;
    }

    private void LateUpdate()
    {
        if (Time.deltaTime > 0f)
        {
            fallbackVelocity = (transform.position - lastPosition) / Time.deltaTime;
        }
        lastPosition = transform.position;

        // Gait from CURRENT speed, direction-normalized (vertical world speed is halved
        // by the iso profile, so raw magnitude would misread vertical movement) and
        // smoothed so one short waypoint-arrival step can't flicker the animation.
        // An NPC "runs" above the midpoint of its own amble and top speeds - a survivor
        // matching the player's sprint animates as running, walking alongside stays a walk.
        Vector3 velocity = CurrentVelocity;
        float nominal = Mathf.Sqrt(velocity.x * velocity.x + 4f * velocity.y * velocity.y);
        smoothedNominalSpeed = Mathf.Lerp(smoothedNominalSpeed, nominal, Mathf.Min(1f, Time.deltaTime * 8f));
        runningGait = npc != null
            ? npc.CurrentState == NpcController.NpcState.Attack ||
              smoothedNominalSpeed > (npc.patrolSpeed + npc.activeSpeed) * 0.5f
            : PlayerRunning;

        UpdateFacing();
        Sprite[] set = PickSet();
        if (set == null || set.Length == 0)
        {
            return;
        }
        if (set != lastSet)
        {
            lastSet = set;
            clock = 0f;   // Restart cycles when the animation changes.
        }

        clock += Time.deltaTime * (runningGait ? activeFramesPerSecond : framesPerSecond);

        int frame = dead ? Mathf.Min((int)clock, set.Length - 1) : (int)clock % set.Length;
        spriteRenderer.sprite = set[frame];

        bool viewFacesLeft = usingRear ? rearFacesLeft : frontFacesLeft;
        spriteRenderer.flipX = viewFacesLeft ? !faceLeft : faceLeft;
    }

    private void UpdateFacing()
    {
        if (npc != null && npc.CurrentState == NpcController.NpcState.Attack && npc.player != null)
        {
            float dx = npc.player.position.x - transform.position.x;
            float dy = npc.player.position.y - transform.position.y;
            if (Mathf.Abs(dx) > 0.001f) { faceLeft = dx < 0f; }
            rearView = dy > 0.15f;
            return;
        }

        Vector3 v = CurrentVelocity;
        if (v.sqrMagnitude < 0.0004f)
        {
            return;   // Keep the last facing while standing still.
        }
        if (v.y > 0.02f) { rearView = true; }
        else if (v.y < -0.02f) { rearView = false; }
        if (Mathf.Abs(v.x) > 0.01f) { faceLeft = v.x < 0f; }
    }

    private Sprite[] PickSet()
    {
        if (dead)
        {
            return Select(deathFront, deathRear);
        }

        bool moving = CurrentVelocity.sqrMagnitude > 0.0004f;
        if (npc == null)
        {
            // Controller-less actor (the demo player): run from sprint intent.
            if (PlayerRunning && (HasFrames(runFront) || HasFrames(runRear)))
            {
                return Select(runFront, runRear);
            }
            return moving ? Select(walkFront, walkRear) : Select(idleFront, idleRear);
        }

        if (npc.CurrentState == NpcController.NpcState.Attack)
        {
            return Select(attackFront, attackRear);
        }
        // Speed decides the gait (see LateUpdate): sprint-following survivors run,
        // chasing zombies run, everyone strolling walks.
        if (moving && runningGait && (HasFrames(runFront) || HasFrames(runRear)))
        {
            return Select(runFront, runRear);
        }
        return moving ? Select(walkFront, walkRear) : Select(idleFront, idleRear);
    }

    private Sprite[] Select(Sprite[] front, Sprite[] rear)
    {
        if (rearView && HasFrames(rear))
        {
            usingRear = true;
            return rear;
        }
        usingRear = false;
        return HasFrames(front) ? front : rear;
    }

    private static bool HasFrames(Sprite[] set)
    {
        return set != null && set.Length > 0;
    }
}
