using UnityEngine;

/// <summary>
/// Deterministic isometric depth sorting. Instead of relying on the camera's
/// transparency sort axis (which URP's 2D renderer can override), every tall object
/// gets an explicit sortingOrder computed from its ground Y position: lower on screen
/// = closer to the camera = higher order. Static props (walls, trees, crates) get the
/// value baked once by the arena builder; moving actors carry this component so their
/// order follows them every frame. Characters and walls can therefore never draw on
/// the wrong side of each other, in any render pipeline.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class YSorter : MonoBehaviour
{
    // BaseOrder must keep OrderFor POSITIVE at the far corner of the biggest map, or
    // sprites there sink below the tilemap layers (orders 0..7) and vanish under the
    // ground. Max ground Y = (mapW + mapH) * 0.25 (+~1 for elevation); at 120x120 that
    // is ~61 world units -> min order 12000 - 6100 = 5900, safely above the tilemaps.
    // Labels pinned at 31000+ stay above everything. Maps up to a summed size of ~470
    // cells are safe; grow BaseOrder if a bigger arena is ever built.
    public const int BaseOrder = 12000;
    public const float OrdersPerUnit = 100f;

    public static int OrderFor(float groundY)
    {
        return BaseOrder - Mathf.RoundToInt(groundY * OrdersPerUnit);
    }

    private SpriteRenderer spriteRenderer;

    private void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    private void LateUpdate()
    {
        spriteRenderer.sortingOrder = OrderFor(transform.position.y);
    }
}
