using UnityEngine;

/// <summary>
/// Smooth camera follow for the NPC demo. Also forces Y-axis transparency sorting
/// on the camera so actors sort correctly against walls and platforms.
/// </summary>
[RequireComponent(typeof(Camera))]
public class DemoCameraFollow : MonoBehaviour
{
    public Transform target;
    public float smoothTime = 0.2f;
    public float orthographicSize = 7f;

    private Vector3 velocity;

    private void Awake()
    {
        Camera cam = GetComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = orthographicSize;
        cam.transparencySortMode = TransparencySortMode.CustomAxis;
        cam.transparencySortAxis = Vector3.up;
    }

    private void LateUpdate()
    {
        if (target == null)
        {
            return;
        }
        Vector3 goal = new Vector3(target.position.x, target.position.y, transform.position.z);
        transform.position = Vector3.SmoothDamp(transform.position, goal, ref velocity, smoothTime);
    }
}
