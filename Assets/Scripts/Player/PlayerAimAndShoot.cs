using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerAimAndShoot : MonoBehaviour
{
    [SerializeField] private GameObject aimDirection;

    private GameObject bulletInstance;

    private Rigidbody2D rb;
    private Vector2 worldPosition;
    private Vector2 direction;
    private float angle;
     private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    private void Update()
    {
        HandleWeaponRotation();
    }

    private void HandleWeaponRotation()
    {
        // Rotate the weapon towards mouse position
        worldPosition = Camera.main.ScreenToWorldPoint(Mouse.current.position.ReadValue());
        direction = worldPosition - rb.position;
        aimDirection.transform.right = direction;

        // Flip the gun when it reaches 90 degree threshold
        angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

        Vector3 localScale = new Vector3(1f, 1f, 1f);
        if (angle > 90 || angle < -90)
        {
            localScale.y = -1f;
        }
        else
        {
            localScale.y = 1f;
        }

        aimDirection.transform.localScale = localScale;
    }

}
