using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Leases the original Map page controllers and map renderer for the redesigned display.</summary>
[DisallowMultipleComponent]
public sealed class RayBanMapBridge : MonoBehaviour
{
    public MapSearch search;
    private readonly Dictionary<Behaviour, bool> components = new Dictionary<Behaviour, bool>();
    private CanvasGroup hiddenGroup;
    private bool addedGroup, groupInteractable, groupBlocks, groupIgnore, originalActive, automaticReconnect, acquired, quitting;
    private float groupAlpha;
    private RectTransform mapRect;
    private Transform mapParent;
    private int mapSibling;
    private Vector2 anchorMin, anchorMax, pivot, size, position;
    private Vector3 scale;
    private Quaternion rotation;

    public bool IsAcquired => acquired;
    public GameObject LegacyRoot => search != null ? search.gameObject : null;
    public WalkingNavigation Navigation => search != null ? search.walkingNavigation : null;
    public bool IsRouteShowing => acquired && Navigation != null && Navigation.navigationPanel != null && Navigation.navigationPanel.activeSelf;
    public bool IsShowingCredits => IsRouteShowing && Navigation.copyrightButton != null && Navigation.copyrightButton.gameObject.activeSelf;
    public string InputText => search != null && search.searchInput != null ? search.searchInput.text : "";
    public string ConnectionStatus => Label(search != null ? search.statusText : null, "Phone connection is unavailable.");
    public string Instructions => search != null && search.IsWaitingForRoomCode && search.phoneTether != null &&
        search.phoneTether.State == PhoneTether.ConnectionState.Idle && PhoneTether.IsValidRoomCode(search.phoneTether.currentRoomCode)
        ? "Saved room restored. Review it, then choose Connect. Keep the phone tracker open and the phone unlocked."
        : Label(search != null ? search.instructionsText : null, "Maps is not connected.");
    public string Placeholder => Label(search != null ? search.placeholderText : null, "Room code or destination");
    public string SubmitLabel => Label(search != null ? search.submitButtonLabel : null, "Connect");
    public string ChangeDestinationLabel => search != null ? search.ChangeDestinationLabel : "Change destination";
    public bool IsListening => acquired && search != null && search.micToggle != null && search.micToggle.isOn;

    public bool Acquire(RectTransform mapHost)
    {
        if (acquired) return true;
        if (!Application.isPlaying || quitting || search == null || mapHost == null || Navigation == null || Navigation.mapView == null) return false;
        var root = search.gameObject;
        originalActive = root.activeSelf;
        root.SetActive(false);
        hiddenGroup = root.GetComponent<CanvasGroup>();
        addedGroup = hiddenGroup == null;
        if (addedGroup) hiddenGroup = root.AddComponent<CanvasGroup>();
        groupAlpha = hiddenGroup.alpha; groupInteractable = hiddenGroup.interactable;
        groupBlocks = hiddenGroup.blocksRaycasts; groupIgnore = hiddenGroup.ignoreParentGroups;
        hiddenGroup.alpha = 0; hiddenGroup.interactable = false; hiddenGroup.blocksRaycasts = false;
        foreach (var component in root.GetComponents<Behaviour>())
        {
            components[component] = component.enabled;
            bool backend = component == search || component == search.phoneTether || component == search.spatialAirTag ||
                component == search.dictationExperience || component == Navigation || component == Navigation.provider || component == hiddenGroup;
            component.enabled = backend;
        }
        if (search.phoneTether != null)
        { automaticReconnect = search.phoneTether.AllowAutomaticReconnect; search.phoneTether.AllowAutomaticReconnect = false; }
        mapRect = Navigation.mapView.transform as RectTransform;
        mapParent = mapRect.parent; mapSibling = mapRect.GetSiblingIndex();
        anchorMin = mapRect.anchorMin; anchorMax = mapRect.anchorMax; pivot = mapRect.pivot;
        size = mapRect.sizeDelta; position = mapRect.anchoredPosition; scale = mapRect.localScale; rotation = mapRect.localRotation;
        mapRect.SetParent(mapHost, false);
        mapRect.anchorMin = Vector2.zero; mapRect.anchorMax = Vector2.one; mapRect.pivot = new Vector2(.5f, .5f);
        mapRect.sizeDelta = mapRect.anchoredPosition = Vector2.zero; mapRect.localScale = Vector3.one; mapRect.localRotation = Quaternion.identity;
        acquired = true;
        root.SetActive(true);
        return true;
    }

    public void SetInput(string text)
    {
        if (acquired && search != null && search.searchInput != null && search.searchInput.text != text)
            search.searchInput.text = text ?? "";
    }

    public bool Available(string action)
    {
        if (!acquired || search == null) return false;
        switch (action)
        {
            case "maps.submit": return Can(search.submitButton);
            case "maps.retry": return Can(search.retryButton);
            case "maps.change-room": return Can(search.changeRoomButton);
            case "maps.change-destination": return Can(search.changeDestinationButton);
            case "maps.listen": return search.dictationExperience != null && search.micToggle != null;
            case "maps.start": return Navigation != null && Can(Navigation.startButton);
            case "maps.end": return Navigation != null && Can(Navigation.endButton);
            case "maps.recalculate": return Navigation != null && Can(Navigation.recalculateButton);
            case "maps.orientation": return Navigation != null && Can(Navigation.orientationButton);
            case "maps.zoom-in": return Navigation != null && Can(Navigation.zoomInButton);
            case "maps.zoom-out": return Navigation != null && Can(Navigation.zoomOutButton);
            default: return true;
        }
    }

    public void Execute(string action)
    {
        if (!Available(action)) return;
        switch (action)
        {
            case "maps.submit": search.SubmitInput(); break;
            case "maps.listen": search.micToggle.isOn = !search.micToggle.isOn; break;
            case "maps.retry": search.RetryConnection(); break;
            case "maps.change-room": search.ChangeRoom(); break;
            case "maps.change-destination": search.ChangeOrNextDestination(); break;
            case "maps.start": Navigation.StartNavigation(); break;
            case "maps.end": Navigation.EndNavigation(); break;
            case "maps.recalculate": Navigation.Recalculate(); break;
            case "maps.orientation": Navigation.ToggleOrientation(); break;
            case "maps.zoom-in": Navigation.ZoomIn(); break;
            case "maps.zoom-out": Navigation.ZoomOut(); break;
            case "maps.route-back": Navigation.BackToSearch(); break;
            case "maps.credits": Navigation.ToggleCredits(); break;
            case "maps.copyright": Navigation.OpenCopyright(); break;
            case "maps.report": Navigation.ReportMapIssue(); break;
            case "maps.provider": Navigation.OpenProviderCredit(); break;
        }
    }

    public void Release(bool restore = true)
    {
        if (!acquired && components.Count == 0) return;
        if (search != null) search.gameObject.SetActive(false); // Cancels dictation, routes, and phone session.
        if (mapRect != null)
        {
            mapRect.SetParent(mapParent, false); mapRect.SetSiblingIndex(mapSibling);
            mapRect.anchorMin = anchorMin; mapRect.anchorMax = anchorMax; mapRect.pivot = pivot;
            mapRect.sizeDelta = size; mapRect.anchoredPosition = position; mapRect.localScale = scale; mapRect.localRotation = rotation;
        }
        if (search != null && search.phoneTether != null) search.phoneTether.AllowAutomaticReconnect = automaticReconnect;
        foreach (var state in components) if (state.Key != null) state.Key.enabled = state.Value;
        components.Clear();
        if (hiddenGroup != null)
        {
            hiddenGroup.alpha = groupAlpha; hiddenGroup.interactable = groupInteractable;
            hiddenGroup.blocksRaycasts = groupBlocks; hiddenGroup.ignoreParentGroups = groupIgnore;
            // Keep the inert runtime group for a same-frame reopen. Unity removes it when Play ends.
        }
        hiddenGroup = null; mapRect = null; acquired = false;
        if (search != null && restore && !quitting) search.gameObject.SetActive(originalActive);
    }

    private static bool Can(Button button) => button != null && button.interactable;
    private static string Label(TMPro.TMP_Text label, string fallback) => label != null ? label.text : fallback;
    private void OnDisable() => Release(!quitting);
    private void OnApplicationQuit() { quitting = true; Release(false); }
}
