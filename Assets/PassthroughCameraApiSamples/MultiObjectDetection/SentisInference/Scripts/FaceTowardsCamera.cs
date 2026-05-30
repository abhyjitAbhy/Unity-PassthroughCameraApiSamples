using UnityEngine;

public class FaceTowardsCamera : MonoBehaviour
{
    private Camera mainCam;

    private void Start()
    {
        mainCam = Camera.main;
    }

    private void LateUpdate()
    {
        if (mainCam == null) return;

        transform.LookAt(transform.position + mainCam.transform.forward);
    }
}