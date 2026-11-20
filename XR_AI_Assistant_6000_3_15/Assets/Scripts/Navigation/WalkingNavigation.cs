using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Page-local walking directions. The phone supplies location; the route service supplies paths and turns.
public class WalkingNavigation : MonoBehaviour
{
    public PhoneTether phone;
    public SpatialAirTag compass;
    public MapSearch search;
    public WalkingRouteProvider provider;
    public RouteMapView mapView;
    public GameObject navigationPanel;
    public GameObject[] inputObjects;
    public TextMeshProUGUI titleText, instructionText, detailText, statusText, attributionText;
    public Button startButton, endButton, backButton, recalculateButton, orientationButton, zoomInButton, zoomOutButton, creditsButton;
    public TextMeshProUGUI startLabel, orientationLabel;
    public Button copyrightButton, reportMapButton, providerCreditButton;

    public bool HasDestination { get; private set; }
    public bool HasRoute => route != null;
    public bool IsNavigating { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsShowing => navigationPanel != null && navigationPanel.activeInHierarchy;
    public WalkingRoute CurrentRoute => route;

    private readonly WalkingRouteProgress progress = new WalkingRouteProgress();
    private WalkingRoute route;
    private GeoCoordinate destination;
    private GeoCoordinate routeOrigin;
    private string destinationName = "", message = "";
    private bool headingUp = true, showingCredits;
    private int requestVersion;
    private uint lastLocationRevision;
    private float nextRefresh;

    private void OnEnable()
    {
        Add(startButton, StartNavigation); Add(endButton, EndNavigation); Add(backButton, BackToSearch);
        Add(recalculateButton, Recalculate); Add(orientationButton, ToggleOrientation);
        Add(zoomInButton, ZoomIn); Add(zoomOutButton, ZoomOut); Add(creditsButton, ToggleCredits);
        Add(copyrightButton, OpenCopyright); Add(reportMapButton, ReportMapIssue);
        Add(providerCreditButton, OpenProviderCredit);
        SetPlain(titleText); SetPlain(instructionText); SetPlain(detailText); SetPlain(statusText); SetPlain(attributionText);
        if (navigationPanel != null && navigationPanel.activeSelf) Show();
    }

    private void OnDisable()
    {
        Remove(startButton, StartNavigation); Remove(endButton, EndNavigation); Remove(backButton, BackToSearch);
        Remove(recalculateButton, Recalculate); Remove(orientationButton, ToggleOrientation);
        Remove(zoomInButton, ZoomIn); Remove(zoomOutButton, ZoomOut); Remove(creditsButton, ToggleCredits);
        Remove(copyrightButton, OpenCopyright); Remove(reportMapButton, ReportMapIssue);
        Remove(providerCreditButton, OpenProviderCredit);
        CancelRequest();
    }

    public void PreviewDestination(float latitude, float longitude, string name)
    {
        ClearDestination();
        if (!Valid(latitude, longitude)) return;
        HasDestination = true;
        destination = new GeoCoordinate(latitude, longitude);
        destinationName = string.IsNullOrWhiteSpace(name) ? "Destination" : name;
        Show();
        Recalculate();
    }

    public void Show()
    {
        if (!HasDestination || navigationPanel == null) return;
        if (inputObjects != null) foreach (var item in inputObjects) if (item != null) item.SetActive(false);
        if (compass != null) compass.enabled = false;
        navigationPanel.SetActive(true);
        if (mapView != null)
        {
            mapView.SetHeadingUp(headingUp);
            mapView.SetOverview(!IsNavigating);
        }
        Refresh();
        FocusPrimary();
    }

    public void BackToSearch()
    {
        bool wasShowing = navigationPanel != null && navigationPanel.activeSelf;
        if (navigationPanel != null) navigationPanel.SetActive(false);
        if (wasShowing && inputObjects != null) foreach (var item in inputObjects) if (item != null) item.SetActive(true);
        if (compass != null) compass.enabled = true;
        if (wasShowing && isActiveAndEnabled && search != null && search.micToggle != null) search.micToggle.Select();
    }

    public void ClearDestination()
    {
        CancelRequest();
        HasDestination = false;
        IsNavigating = false;
        route = null;
        destinationName = message = "";
        showingCredits = false;
        progress.Reset(null);
        if (mapView != null) mapView.Clear();
        BackToSearch();
    }

    private void CancelRequest()
    {
        requestVersion++;
        if (provider != null) provider.Cancel();
        if (IsLoading) message = "Route loading paused. Choose Recalculate to try again.";
        IsLoading = false;
    }

    public void Recalculate()
    {
        CancelRequest();
        if (!isActiveAndEnabled || !HasDestination) return;
        showingCredits = false;
        if (phone == null || !phone.HasUsableLocation)
        { message = "Waiting for accurate phone GPS. Back opens connection controls."; Refresh(); return; }
        if (provider == null) { message = "Walking route service is unavailable."; Refresh(); return; }
        IsNavigating = false;
        route = null;
        progress.Reset(null);
        if (mapView != null) mapView.Clear();
        IsLoading = true;
        message = "Finding a walking route…";
        int version = requestVersion;
        var origin = new GeoCoordinate(phone.currentLat, phone.currentLon);
        provider.RequestRoute(origin, destination,
            result =>
            {
                if (version != requestVersion || !isActiveAndEnabled || !HasDestination) return;
                IsLoading = false;
                route = result;
                routeOrigin = origin;
                progress.Reset(route);
                lastLocationRevision = 0;
                message = "Review the route, then choose Start walking.";
                if (mapView != null) { mapView.SetRoute(route); mapView.SetOverview(true); mapView.SetHeadingUp(headingUp); }
                Refresh();
                if (IsShowing) FocusPrimary();
            }, error =>
            {
                if (version != requestVersion || !isActiveAndEnabled) return;
                IsLoading = false;
                message = error;
                Refresh();
                if (IsShowing) FocusPrimary();
            });
        Refresh();
    }

    public void StartNavigation()
    {
        if (!HasRoute || IsLoading || phone == null || !phone.HasUsableLocation) return;
        if (GeoDistance(new GeoCoordinate(phone.currentLat, phone.currentLon), routeOrigin) > 50)
        {
            Recalculate();
            message = "Your starting point changed. Review the refreshed route before starting.";
            Refresh();
            return;
        }
        progress.Reset(route);
        lastLocationRevision = 0;
        IsNavigating = true;
        showingCredits = false;
        message = "";
        if (mapView != null) mapView.SetOverview(false);
        Refresh();
        if (endButton != null) endButton.Select();
    }

    public void EndNavigation()
    {
        IsNavigating = false;
        showingCredits = false;
        message = "Navigation ended. The route preview is still available.";
        if (mapView != null) mapView.SetOverview(true);
        Refresh();
        FocusPrimary();
    }

    public void ToggleOrientation()
    {
        headingUp = !headingUp;
        if (mapView != null) mapView.SetHeadingUp(headingUp);
        Refresh();
    }
    public void ZoomIn() { if (mapView != null) mapView.ZoomIn(); }
    public void ZoomOut() { if (mapView != null) mapView.ZoomOut(); }
    public void ToggleCredits() { showingCredits = !showingCredits; Refresh(); }
    public void OpenCopyright() { Application.OpenURL("https://www.openstreetmap.org/copyright"); }
    public void ReportMapIssue() { Application.OpenURL("https://www.openstreetmap.org/fixthemap"); }
    public void OpenProviderCredit() { Application.OpenURL("https://trailsplits.com/"); }

    private void Update()
    {
        if (!HasDestination || phone == null) return;
        bool fresh = phone.HasFreshLocation;
        if (mapView != null && IsShowing)
            mapView.SetPosition(new GeoCoordinate(phone.currentLat, phone.currentLon), fresh, phone.currentHeading, phone.HasFreshHeading);
        if (route != null && fresh && lastLocationRevision != phone.locationRevision)
        {
            lastLocationRevision = phone.locationRevision;
            progress.Update(phone.currentLat, phone.currentLon, phone.currentAccuracyMeters, Time.realtimeSinceStartupAsDouble);
        }
        if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .2f; Refresh(); }
    }

    private void Refresh()
    {
        if (!IsShowing) return;
        Set(titleText, destinationName);
        Set(orientationLabel, !IsNavigating ? "North-up preview" : headingUp ? "Phone-up" : "North-up");
        Set(attributionText, "© OpenStreetMap contributors · Routing: TrailSplits");
        Set(startLabel, "Start walking");
        bool fresh = phone != null && phone.HasFreshLocation;
        bool usable = phone != null && phone.HasUsableLocation;
        SetButton(startButton, HasRoute && !IsNavigating && !IsLoading && usable);
        SetButton(endButton, IsNavigating);
        SetButton(recalculateButton, HasDestination && !IsLoading && usable);
        SetButton(orientationButton, IsNavigating);
        SetButton(zoomInButton, HasRoute && !showingCredits);
        SetButton(zoomOutButton, HasRoute && !showingCredits);
        if (copyrightButton != null) copyrightButton.gameObject.SetActive(showingCredits);
        if (reportMapButton != null) reportMapButton.gameObject.SetActive(showingCredits);
        if (providerCreditButton != null) providerCreditButton.gameObject.SetActive(showingCredits);
        if (mapView != null) mapView.gameObject.SetActive(!showingCredits);
        RefreshNavigation();
        if (showingCredits)
        {
            Set(instructionText, "Map data: © OpenStreetMap contributors\nWalking routes: TrailSplits");
            Set(detailText, "openstreetmap.org/copyright\nReport map issues: openstreetmap.org/fixthemap");
            Set(statusText, "Route requests share start/destination with TrailSplits. Tiles use OpenStreetMap servers.");
            return;
        }
        string instruction = IsLoading ? "Finding a walking route…" : "Walking route preview";
        string details = route == null ? "" : Distance(route.DistanceMeters) + " · about " + Minutes(route.DurationSeconds);
        string health = message;
        if (IsNavigating)
        {
            instruction = progress.HasArrived ? "Arrived near your destination" : progress.NextManeuver != null
                ? progress.NextManeuver.Instruction : "Follow the walking route";
            details = progress.HasArrived ? "Choose End to finish." : Distance(progress.DistanceToNextManeuverMeters) + " to next step\n" +
                Distance(progress.DistanceRemainingMeters) + " remaining · about " + Minutes(progress.DurationRemainingSeconds);
            health = "Keep the phone tracker visible and the phone unlocked.";
            if (progress.IsOffRoute) health = "You appear off route. Choose Recalculate.";
            else if (!progress.IsLocationReliable) health = "Position is uncertain. Waiting for a better GPS fix.";
        }
        if (!fresh)
        {
            health = "Location paused. Unlock the phone and reopen its tracker. Back opens connection controls.";
            if (IsNavigating)
            {
                instruction = "Navigation paused";
                details = "Waiting for fresh phone GPS.";
            }
        }
        else if (!usable)
        {
            health = "GPS accuracy is low. Wait in an open area for a better fix.";
            if (IsNavigating)
            {
                instruction = "Navigation paused";
                details = "Waiting for accurate phone GPS.";
            }
        }
        else if (!IsNavigating && HasRoute && mapView != null && mapView.TilesUnavailable)
            health = "Street tiles unavailable; the walking route remains visible. Review it before starting.";
        else if (IsNavigating && !progress.IsOffRoute && progress.IsLocationReliable)
        {
            if (headingUp && !phone.HasFreshHeading) health = "Compass unavailable; map is north-up until it returns.";
            else if (mapView != null && !string.IsNullOrEmpty(mapView.StatusMessage)) health = mapView.StatusMessage;
        }
        Set(instructionText, instruction);
        Set(detailText, details);
        Set(statusText, health);
        Set(attributionText, "© OpenStreetMap contributors · Routing: TrailSplits");
    }

    // The existing microgesture navigator follows Selectable links. Skip disabled actions so
    // a missing GPS fix never traps selection behind Start or Recalculate.
    private void RefreshNavigation()
    {
        Button[] controls = { backButton, startButton, endButton, recalculateButton, orientationButton,
            zoomOutButton, zoomInButton, creditsButton, copyrightButton, reportMapButton, providerCreditButton };
        for (int i = 0; i < controls.Length; i++)
        {
            Button current = controls[i];
            if (current == null || !current.isActiveAndEnabled || !current.interactable) continue;
            Button previous = current, next = current;
            for (int offset = 1; offset < controls.Length; offset++)
            {
                Button candidate = controls[(i + offset) % controls.Length];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.interactable) { next = candidate; break; }
            }
            for (int offset = 1; offset < controls.Length; offset++)
            {
                Button candidate = controls[(i - offset + controls.Length) % controls.Length];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.interactable) { previous = candidate; break; }
            }
            current.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = previous,
                selectOnLeft = previous, selectOnDown = next, selectOnRight = next };
        }
    }

    private void SetButton(Button button, bool enabled)
    {
        if (button == null) return;
        bool selected = !enabled && button.interactable && UnityEngine.EventSystems.EventSystem.current != null &&
            UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == button.gameObject;
        button.interactable = enabled;
        if (selected && backButton != null) backButton.Select();
    }
    private void FocusPrimary()
    {
        if (startButton != null && startButton.interactable) startButton.Select();
        else if (backButton != null) backButton.Select();
    }
    private static void Add(Button b, UnityEngine.Events.UnityAction action) { if (b != null) b.onClick.AddListener(action); }
    private static void Remove(Button b, UnityEngine.Events.UnityAction action) { if (b != null) b.onClick.RemoveListener(action); }
    private static void Set(TextMeshProUGUI t, string value) { if (t != null && t.text != value) t.text = value; }
    private static void SetPlain(TextMeshProUGUI t) { if (t != null) t.richText = false; }
    private static bool Valid(double lat, double lon) => !double.IsNaN(lat) && !double.IsNaN(lon) &&
        !double.IsInfinity(lat) && !double.IsInfinity(lon) && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
    private static string Distance(double metres) => metres < 1000 ? Math.Max(0, Math.Round(metres)).ToString("0") + " m" : (metres / 1000).ToString("0.0") + " km";
    private static string Minutes(double seconds) => Math.Max(1, Math.Ceiling(seconds / 60)).ToString("0") + " min";
    private static double GeoDistance(GeoCoordinate a, GeoCoordinate b)
    {
        double k = Math.PI / 180, dlat = (b.Latitude - a.Latitude) * k, dlon = (b.Longitude - a.Longitude) * k;
        double v = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(a.Latitude * k) * Math.Cos(b.Latitude * k) * Math.Pow(Math.Sin(dlon / 2), 2);
        return 12742000 * Math.Asin(Math.Sqrt(Math.Min(1, Math.Max(0, v))));
    }
}
