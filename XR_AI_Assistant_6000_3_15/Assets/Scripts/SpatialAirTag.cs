using System;
using UnityEngine;
using TMPro;

public class SpatialAirTag : MonoBehaviour
{
    public enum BearingOrientation
    {
        NorthUp,
        PhoneHeading
    }

    [Header("Data Source")]
    public PhoneTether phoneTether;

    [Header("Destination Coordinates")]
    public float targetLat = 0f;
    public float targetLon = 0f;

    [Header("UI References")]
    public RectTransform arrowUI;
    public float rotationSmoothing = 5f;
    public TextMeshProUGUI distanceText;

    [Tooltip("North-up keeps the arrow geographic. Phone Heading is relative to the phone, not the headset.")]
    public BearingOrientation orientation = BearingOrientation.NorthUp;

    private const double EarthRadiusMeters = 6371000.0;
    private const double DegreesToRadians = Math.PI / 180.0;
    private bool hasTarget;
    private bool isDisplaying;
    private int lastDistanceMeters = -1;
    private BearingOrientation lastOrientation;

    private void OnEnable()
    {
        HideDisplay();
    }

    private void OnDisable()
    {
        HideDisplay();
    }

    public bool HasTarget()
    {
        return hasTarget && IsValidLocation(targetLat, targetLon);
    }

    public void SetTarget(float latitude, float longitude)
    {
        if (!IsValidLocation(latitude, longitude))
        {
            ClearTarget();
            return;
        }

        targetLat = latitude;
        targetLon = longitude;
        // An explicit state allows the valid coordinate (0, 0) to be a destination.
        hasTarget = true;
        HideDisplay();
    }

    public void ClearTarget()
    {
        hasTarget = false;
        targetLat = 0f;
        targetLon = 0f;
        HideDisplay();
    }

    void Update()
    {
        if (!HasTarget() || phoneTether == null || !phoneTether.HasFreshLocation ||
            !IsValidLocation(phoneTether.currentLat, phoneTether.currentLon) ||
            (orientation == BearingOrientation.PhoneHeading &&
             (!phoneTether.HasFreshHeading || !IsFinite(phoneTether.currentHeading))))
        {
            HideDisplay();
            return;
        }

        double currentLat = phoneTether.currentLat;
        double currentLon = phoneTether.currentLon;
        double bearing = CalculateBearing(currentLat, currentLon, targetLat, targetLon);
        double distance = CalculateHaversineDistance(currentLat, currentLon, targetLat, targetLon);
        double arrowAngle = orientation == BearingOrientation.PhoneHeading
            ? bearing - phoneTether.currentHeading
            : bearing;

        if (arrowUI != null)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, 0f, (float)-arrowAngle);
            // Never animate a newly restored arrow from a stale bearing.
            arrowUI.localRotation = !isDisplaying || lastOrientation != orientation
                ? targetRotation
                : Quaternion.Slerp(arrowUI.localRotation, targetRotation,
                    Time.unscaledDeltaTime * Mathf.Max(0f, rotationSmoothing));
            if (!arrowUI.gameObject.activeSelf)
                arrowUI.gameObject.SetActive(true);
        }

        int distanceMeters = (int)Math.Round(distance, MidpointRounding.AwayFromZero);
        if (distanceText != null &&
            (!isDisplaying || distanceMeters != lastDistanceMeters || orientation != lastOrientation))
            distanceText.text = $"{(orientation == BearingOrientation.NorthUp ? "North-up" : "Phone-relative")} | {distanceMeters} m to target";

        lastDistanceMeters = distanceMeters;
        lastOrientation = orientation;
        isDisplaying = true;
    }

    private void HideDisplay()
    {
        if (arrowUI != null && arrowUI.gameObject.activeSelf)
            arrowUI.gameObject.SetActive(false);
        // This label is exclusively for distance; MapSearch owns separate status/instruction labels.
        if (distanceText != null && !string.IsNullOrEmpty(distanceText.text))
            distanceText.text = string.Empty;
        isDisplaying = false;
        lastDistanceMeters = -1;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsValidLocation(double latitude, double longitude)
    {
        return IsFinite(latitude) && IsFinite(longitude) &&
               latitude >= -90.0 && latitude <= 90.0 &&
               longitude >= -180.0 && longitude <= 180.0;
    }

    private static double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double lat1Rad = lat1 * DegreesToRadians;
        double lat2Rad = lat2 * DegreesToRadians;
        double lonDiffRad = (lon2 - lon1) * DegreesToRadians;

        double y = Math.Sin(lonDiffRad) * Math.Cos(lat2Rad);
        double x = Math.Cos(lat1Rad) * Math.Sin(lat2Rad) -
                   Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(lonDiffRad);

        return (Math.Atan2(y, x) / DegreesToRadians + 360.0) % 360.0;
    }

    private static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * DegreesToRadians;
        double dLon = (lon2 - lon1) * DegreesToRadians;

        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                   Math.Cos(lat1 * DegreesToRadians) * Math.Cos(lat2 * DegreesToRadians) *
                   Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
        // Rounding near antipodal points can otherwise make sqrt(1 - a) invalid.
        a = Math.Max(0.0, Math.Min(1.0, a));

        return EarthRadiusMeters * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
    }
}
