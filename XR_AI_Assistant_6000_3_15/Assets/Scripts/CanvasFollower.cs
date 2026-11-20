using UnityEngine;

public class CanvasFollower : MonoBehaviour
{
    public float distanceFromCamera = 1.0f;
    public float positionLerpSpeed = 5f;
    public Transform cam;

    void OnEnable()
    {
        Vector3 targetPos = cam.position + cam.forward * distanceFromCamera;
        transform.position = targetPos;
        transform.rotation = Quaternion.LookRotation(transform.position - cam.position);
    }

    void LateUpdate()
    {
        Vector3 targetPos = cam.position + cam.forward * distanceFromCamera;
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * positionLerpSpeed);
        transform.rotation = Quaternion.LookRotation(transform.position - cam.position);
    }
}