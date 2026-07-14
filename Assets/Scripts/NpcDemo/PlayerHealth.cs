using UnityEngine;

/// <summary>
/// Player survival for the demo. Zombies call TakeDamage when they bite; the sprite
/// flashes red and a short invulnerability window prevents instant multi-bites.
/// At 0 HP the player is DEAD - LevelObjective sees IsDead and fails the level, and the
/// GameManager offers a restart. (There is no respawn: dying means the run is over.)
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class PlayerHealth : MonoBehaviour
{
    public int maxHealth = 3;
    public float invulnerabilityTime = 1f;

    public int Current { get; private set; }
    public bool IsDead { get; private set; }

    private SpriteRenderer spriteRenderer;
    private Color baseColor;
    private float invulnerabilityTimer;
    private float flashTimer;
    private const float FlashDuration = 0.35f;

    private void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
        baseColor = spriteRenderer.color;
        Current = maxHealth;
    }

    private void Update()
    {
        if (IsDead)
        {
            return;
        }
        invulnerabilityTimer -= Time.deltaTime;
        if (flashTimer > 0f)
        {
            flashTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(flashTimer / FlashDuration);
            spriteRenderer.color = Color.Lerp(baseColor, Color.red, t);
        }
    }

    public void TakeDamage(int amount, Vector3 attackerPosition)
    {
        if (IsDead || invulnerabilityTimer > 0f)
        {
            return;
        }
        invulnerabilityTimer = invulnerabilityTime;
        flashTimer = FlashDuration;
        Current -= amount;
        Debug.Log("Player took " + amount + " damage (" + Mathf.Max(Current, 0) + "/" + maxHealth + " HP).");
        if (Current <= 0)
        {
            Current = 0;
            IsDead = true;
            spriteRenderer.color = new Color(0.45f, 0.15f, 0.15f);   // Slumped, dark red.
            Debug.Log("Player died - level failed.");
        }
    }
}
