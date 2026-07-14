using UnityEngine;
using System.Collections;

public class PlayerAnimations : MonoBehaviour
{
    private Animator animator;
    private PlayerController playerController;

    [Header("Unarmed")]
    private readonly string[] staticDirections = { "Static_L", "Static_R", "Static_TL", "Static_TR" };
    private readonly string[] runDirections = { "Run_L", "Run_R", "Run_TL", "Run_TR" };

    [Header("HG Armed")]
    private readonly string[] hgStaticDirections = { "HG_Static_L", "HG_Static_R", "HG_Static_TL", "HG_Static_TR" };
    private readonly string[] hgRunDirections = { "HG_Run_L", "HG_Run_R", "HG_Run_TL", "HG_Run_TR" };
    private readonly string[] hgEquipDirections = { "HG_Equip_L", "HG_Equip_R", "HG_Equip_TL", "HG_Equip_TR" };
    private readonly string[] hgShootDirections = { "HG_Shoot_L", "HG_Shoot_R", "HG_Shoot_TL", "HG_Shoot_TR" };

    private int lastDirectionIndex = 0;
    private string currentState = "";
    private bool isArmed = false;
    private bool isEquipping = false;
    private bool isShooting = false;

    private void Awake()
    {
        animator = GetComponent<Animator>();

        if (animator == null)
        {
            Transform sprite = transform.Find("Sprite") ?? transform.Find("sprite");
            if (sprite != null) animator = sprite.GetComponent<Animator>();
        }
        if (animator == null)
            animator = GetComponentInChildren<Animator>(true);

        playerController = GetComponentInParent<PlayerController>();
    }

    // Called by PlayerWeaponHandler
    public void SetArmed(bool armed)
    {
        isArmed = armed;
        currentState = "";

        if (armed)
            StartCoroutine(PlayOneShot(hgEquipDirections[lastDirectionIndex], () => isEquipping = false));

        isEquipping = armed;
    }

    // Called by Shooting.cs when a bullet is fired
    public void PlayShoot()
    {
        if (!isArmed || isEquipping) return;
        StartCoroutine(PlayOneShot(hgShootDirections[lastDirectionIndex], () => isShooting = false));
        isShooting = true;
    }

    private IEnumerator PlayOneShot(string stateName, System.Action onComplete)
    {
        animator.Play(stateName);
        currentState = stateName;

        // Wait for the animation to start
        yield return null;
        yield return null;

        // Wait for it to finish
        while (animator.GetCurrentAnimatorStateInfo(0).normalizedTime < 1f
               && animator.GetCurrentAnimatorStateInfo(0).IsName(stateName))
        {
            yield return null;
        }

        onComplete?.Invoke();
        currentState = "";
    }

    private void LateUpdate()
    {
        if (playerController == null || animator == null) return;

        // Let one-shot animations finish
        if (isEquipping || isShooting) return;

        Vector2 movement = playerController.MovementInput;
        bool isMoving = movement.sqrMagnitude > 0.01f;

        if (isMoving)
            lastDirectionIndex = GetDirectionIndex(movement);

        string targetState = isArmed
            ? (isMoving ? hgRunDirections[lastDirectionIndex] : hgStaticDirections[lastDirectionIndex])
            : (isMoving ? runDirections[lastDirectionIndex] : staticDirections[lastDirectionIndex]);

        ChangeAnimationState(targetState);
    }

    private void ChangeAnimationState(string newState)
    {
        if (currentState == newState && animator.GetCurrentAnimatorStateInfo(0).IsName(newState))
            return;
        animator.Play(newState);
        animator.speed = 1f;
        currentState = newState;
    }

    private static readonly int[] SectorToIndex = { 1, 3, 3, 2, 0, 0, 1, 1 };
    private int GetDirectionIndex(Vector2 dir)
    {
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        return SectorToIndex[sector];
    }
}