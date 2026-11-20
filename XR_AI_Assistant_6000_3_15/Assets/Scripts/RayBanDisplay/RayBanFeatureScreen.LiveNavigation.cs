using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public partial class RayBanFeatureScreen
{
    [SerializeField] private RectTransform liveMapInputRoot, liveMapRouteRoot, liveMapHost, liveMapCreditsRoot;
    [SerializeField] private TMP_InputField liveMapInput;
    [SerializeField] private TMP_Text liveMapConnection, liveMapInstructions, liveMapPlaceholder;
    [SerializeField] private TMP_Text liveMapTitle, liveMapTurnText, liveMapDetails, liveMapHealth, liveMapAttribution;
    private bool wasLiveRoute;

    // All place/route information comes from the original controllers. No illustrative route survives in live mode.
    private void BuildLiveNavigation(Transform page)
    {
        liveMapInputRoot = Rect("Phone pairing and destination", page, 0, 84, 600, 516);
        liveMapConnection = Text(liveMapInputRoot, "Phone connection state", "Connect your phone to use Maps.", 24, 8, 552, 50, 20);
        var instructions = Panel(liveMapInputRoot, "Map instructions card", 20, 68, 560, 152);
        liveMapInstructions = Text(instructions.transform, "Map instructions", "Open the phone tracker, choose Speak and say its four-digit room code. Keep the phone unlocked while navigating.", 18, 14, 524, 124, 21);
        var edit = Button(liveMapInputRoot, "Room or destination display", "maps.edit", "", "map", 20, 232, 560, 62, 1, 0);
        var inputRect = Rect("Map input display", edit.transform, 52, 8, 492, 46);
        liveMapInput = inputRect.gameObject.AddComponent<TMP_InputField>();
        var textRect = Rect("Text area", inputRect, 4, 4, 480, 38);
        textRect.gameObject.AddComponent<RectMask2D>();
        var entry = Text(textRect, "Entered text", "", 0, 0, 480, 38, 24);
        liveMapPlaceholder = Text(textRect, "Input placeholder", "Four-digit room code", 0, 0, 480, 38, 24, false, new Color(.76f, .85f, .89f));
        liveMapInput.textViewport = textRect;
        liveMapInput.textComponent = (TextMeshProUGUI)entry;
        liveMapInput.placeholder = liveMapPlaceholder;
        liveMapInput.richText = false; liveMapInput.characterLimit = 200;
        liveMapInput.lineType = TMP_InputField.LineType.SingleLine;
        liveMapInput.navigation = new Navigation { mode = Navigation.Mode.None };
        ConfigureMapReadout();
        Button(liveMapInputRoot, "Dictate room or destination", "maps.listen", "Speak", "mic", 20, 306, 274, 62, 2, 0);
        Button(liveMapInputRoot, "Confirm map input", "maps.submit", "Connect", "check", 306, 306, 274, 62, 2, 1);
        Button(liveMapInputRoot, "Retry phone connection", "maps.retry", "Retry", "route", 20, 380, 274, 52, 3, 0);
        Button(liveMapInputRoot, "Change phone room", "maps.change-room", "Change room", "settings", 306, 380, 274, 52, 3, 1);
        Button(liveMapInputRoot, "Change destination", "maps.change-destination", "Change destination", "map", 20, 444, 560, 52, 4, 0);

        liveMapRouteRoot = Rect("Live walking navigation", page, 0, 84, 600, 516);
        liveMapTitle = Text(liveMapRouteRoot, "Destination name", "Walking route", 24, 4, 552, 34, 23, true);
        var mapFrame = Panel(liveMapRouteRoot, "Live street map", 20, 44, 560, 166, new Color(.08f, .18f, .21f, 1));
        mapFrame.Flat = true;
        mapFrame.gameObject.AddComponent<RectMask2D>();
        liveMapHost = Rect("Existing route renderer host", mapFrame.transform, 0, 0, 560, 166);
        liveMapTurnText = Text(liveMapRouteRoot, "Walking instruction", "No route loaded", 24, 222, 552, 46, 25, true);
        liveMapDetails = Text(liveMapRouteRoot, "Walking distance and time", "", 24, 272, 552, 42, 20);
        liveMapHealth = Text(liveMapRouteRoot, "Walking status", "", 24, 316, 552, 44, 17);
        liveMapAttribution = Text(liveMapRouteRoot, "Map and route attribution", "© OpenStreetMap contributors · Routing: TrailSplits", 24, 364, 552, 23, 13);
        Button(liveMapRouteRoot, "Start walking", "maps.start", "Start", "play", 20, 396, 176, 46, 1, 0);
        Button(liveMapRouteRoot, "End walking", "maps.end", "End", "close", 212, 396, 176, 46, 1, 1);
        Button(liveMapRouteRoot, "Recalculate walking route", "maps.recalculate", "Recalculate", "route", 404, 396, 176, 46, 1, 2);
        Button(liveMapRouteRoot, "Zoom map out", "maps.zoom-out", "", "minus", 20, 450, 56, 46, 2, 0, true);
        Button(liveMapRouteRoot, "Zoom map in", "maps.zoom-in", "", "plus", 84, 450, 56, 46, 2, 1, true);
        Button(liveMapRouteRoot, "Map orientation", "maps.orientation", "Phone-up", "up", 148, 450, 180, 46, 2, 2);
        Button(liveMapRouteRoot, "Map credits", "maps.credits", "Info", "map", 336, 450, 110, 46, 2, 3);
        Button(liveMapRouteRoot, "Map connection controls", "maps.route-back", "Edit", "back", 454, 450, 126, 46, 2, 4);

        liveMapCreditsRoot = Rect("Map attribution links", liveMapRouteRoot, 20, 44, 560, 166);
        Button(liveMapCreditsRoot, "Open map copyright", "maps.copyright", "OpenStreetMap copyright", "map", 0, 0, 560, 46, 3, 0);
        Button(liveMapCreditsRoot, "Report map issue", "maps.report", "Report a map issue", "map", 0, 54, 560, 46, 4, 0);
        Button(liveMapCreditsRoot, "Open route provider", "maps.provider", "TrailSplits routing", "route", 0, 108, 560, 46, 5, 0);
        liveMapCreditsRoot.gameObject.SetActive(false);
        liveMapRouteRoot.gameObject.SetActive(false);
        foreach (var value in new[] { liveMapConnection, liveMapInstructions, liveMapTitle, liveMapTurnText, liveMapDetails, liveMapHealth }) value.richText = false;
    }

    private void ShowLiveNavigation(bool visible)
    {
        var bridge = shell != null ? shell.mapBridge : null;
        if (!visible)
        {
            bool released = bridge != null && bridge.IsAcquired;
            if (bridge != null) bridge.Release();
            if (released && shell.appSettings != null) shell.appSettings.BindVisuals(shell.VisualRoot);
            return;
        }
        ConfigureMapReadout();
        // Candidate names, mapped addresses and the alternative-match hint must
        // remain readable in the existing compact confirmation card.
        liveMapInstructions.enableAutoSizing = true;
        liveMapInstructions.fontSizeMin = 16;
        liveMapInstructions.fontSizeMax = 21;
        if (bridge != null) bridge.Acquire(liveMapHost);
        if (shell.appSettings != null) shell.appSettings.BindVisuals(shell.VisualRoot);
        wasLiveRoute = false;
        TickLiveNavigation();
    }

    private void ConfigureMapReadout()
    {
        if (liveMapInput == null) return;
        // Keep the saved text/placeholder references and layout, but never enter
        // TMP's editing mode: it consumes swipes instead of moving button focus.
        liveMapInput.DeactivateInputField();
        liveMapInput.shouldActivateOnSelect = false;
        liveMapInput.readOnly = true;
        liveMapInput.interactable = false;
        liveMapInput.enabled = false;
        var tile = liveMapInput.GetComponentInParent<RayBanTile>();
        if (tile == null) return;
        tile.interactable = false;
        tile.SetVisualFocus(false);
        foreach (var graphic in tile.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
    }

    private bool HandleLiveNavigation(string action)
    {
        if (string.IsNullOrEmpty(action) || !action.StartsWith("maps.")) return false;
        if (shell.mapBridge != null && action != "maps.edit") shell.mapBridge.Execute(action);
        TickLiveNavigation();
        return true;
    }

    private void TickLiveNavigation()
    {
        if (shell == null || !shell.UsesLiveFeatures || CurrentView != "maps" || liveMapInputRoot == null) return;
        var bridge = shell.mapBridge;
        bool available = bridge != null && bridge.IsAcquired;
        bool route = available && bridge.IsRouteShowing;
        liveMapInputRoot.gameObject.SetActive(!route);
        liveMapRouteRoot.gameObject.SetActive(route);
        if (!route)
        {
            liveMapConnection.text = available ? bridge.ConnectionStatus : "Maps is not connected.";
            liveMapInstructions.text = available ? bridge.Instructions : "The existing Map components are unavailable. Return to the launcher and try again.";
            liveMapInput.SetTextWithoutNotify(available ? bridge.InputText : "");
            liveMapPlaceholder.gameObject.SetActive(string.IsNullOrEmpty(liveMapInput.text));
            liveMapPlaceholder.text = available ? bridge.Placeholder : "Room code or destination";
            SetButtonLabel("maps.submit", available ? bridge.SubmitLabel : "Connect");
            SetButtonLabel("maps.change-destination", available ? bridge.ChangeDestinationLabel : "Change destination");
            SetButtonLabel("maps.listen", available && bridge.IsListening ? "Finish speech" : "Speak");
        }
        else
        {
            var nav = bridge.Navigation;
            liveMapTitle.text = nav.titleText != null ? nav.titleText.text : "Walking route";
            liveMapTurnText.text = nav.instructionText != null ? nav.instructionText.text : "";
            liveMapDetails.text = nav.detailText != null ? nav.detailText.text : "";
            liveMapHealth.text = nav.statusText != null ? nav.statusText.text : "";
            liveMapAttribution.text = "© OpenStreetMap contributors · Routing: TrailSplits";
            SetButtonLabel("maps.orientation", nav.mapView != null && nav.mapView.UsingNorthUp ? "North-up" : "Phone-up");
            liveMapCreditsRoot.gameObject.SetActive(bridge.IsShowingCredits);
        }
        foreach (var control in controls)
            if (control != null && control.ActionId != null && control.ActionId.StartsWith("maps."))
                SetControlAvailable(control.ActionId, control.ActionId != "maps.edit" && available && bridge.Available(control.ActionId));
        if (wasLiveRoute != route)
        { wasLiveRoute = route; FocusAction(route ? "maps.route-back" : "maps.listen"); }
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null && selected.transform.IsChildOf(liveMapInput.transform.parent))
        {
            ConfigureMapReadout();
            FocusAction("back"); // Still escapable when dictation is unavailable.
            FocusAction("maps.listen");
        }
        if (available && selected != null && selected.transform.IsChildOf(bridge.LegacyRoot.transform))
            FocusAction(route ? "maps.route-back" : "maps.submit");
    }
}
