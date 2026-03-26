using UnityEngine;
using TMPro;

public class SpatialAirTag : MonoBehaviour
{
    [Header("Data Source")]
    public PhoneTether phoneTether;

    [Header("Destination Coordinates")]
    public float targetLat = 0f;
    public float targetLon = 0f;

    [Header("UI References")]
    public RectTransform arrowUI;
    public float rotationSmoothing = 5f;
    public TextMeshProUGUI distanceText;

    private const float EarthRadiusMeters = 6371000f;

    public bool HasTarget()
    {
        return !(targetLat == 0f && targetLon == 0f);
    }

    public void ClearTarget()
    {
        targetLat = 0f;
        targetLon = 0f;

        if (arrowUI != null)
            arrowUI.gameObject.SetActive(false);

        // IMPORTANT:
        // Do NOT write to distanceText here.
        // MapSearch owns the top text until a destination is actually set.
    }

    void Update()
    {
        if (!HasTarget())
        {
            if (arrowUI != null)
                arrowUI.gameObject.SetActive(false);
            return;
        }

        if (arrowUI != null)
            arrowUI.gameObject.SetActive(true);

        if (phoneTether == null || arrowUI == null)
            return;

        float currentLat = phoneTether.currentLat;
        float currentLon = phoneTether.currentLon;
        float currentHeading = phoneTether.currentHeading;

        if (currentLat == 0f && currentLon == 0f)
            return;

        float bearing = CalculateBearing(currentLat, currentLon, targetLat, targetLon);
        float arrowAngle = bearing - currentHeading;

        Quaternion targetRotation = Quaternion.Euler(0, 0, -arrowAngle);
        arrowUI.localRotation = Quaternion.Slerp(
            arrowUI.localRotation,
            targetRotation,
            Time.deltaTime * rotationSmoothing
        );

        if (distanceText != null)
        {
            float distance = CalculateHaversineDistance(currentLat, currentLon, targetLat, targetLon);
            distanceText.text = $"{Mathf.RoundToInt(distance)}m to target";
        }
    }

    private float CalculateBearing(float lat1, float lon1, float lat2, float lon2)
    {
        float lat1Rad = lat1 * Mathf.Deg2Rad;
        float lat2Rad = lat2 * Mathf.Deg2Rad;
        float lonDiffRad = (lon2 - lon1) * Mathf.Deg2Rad;

        float y = Mathf.Sin(lonDiffRad) * Mathf.Cos(lat2Rad);
        float x = Mathf.Cos(lat1Rad) * Mathf.Sin(lat2Rad) - Mathf.Sin(lat1Rad) * Mathf.Cos(lat2Rad) * Mathf.Cos(lonDiffRad);

        float bearingRad = Mathf.Atan2(y, x);
        return (bearingRad * Mathf.Rad2Deg + 360f) % 360f;
    }

    private float CalculateHaversineDistance(float lat1, float lon1, float lat2, float lon2)
    {
        float dLat = (lat2 - lat1) * Mathf.Deg2Rad;
        float dLon = (lon2 - lon1) * Mathf.Deg2Rad;

        float a = Mathf.Sin(dLat / 2) * Mathf.Sin(dLat / 2) +
                  Mathf.Cos(lat1 * Mathf.Deg2Rad) * Mathf.Cos(lat2 * Mathf.Deg2Rad) *
                  Mathf.Sin(dLon / 2) * Mathf.Sin(dLon / 2);

        float c = 2 * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }
}