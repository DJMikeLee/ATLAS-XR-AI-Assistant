using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Repeatable Editor Play Mode checks; no physical hand/camera claims.</summary>
[InitializeOnLoad]
public static class RayBanDisplayPreviewValidation
{
    private const string PendingKey = "RayBanDisplay.ValidationPending";
    private static readonly List<string> checks = new List<string>();
    private static readonly Queue<Action> steps = new Queue<Action>();
    private static double nextStep;
    private static RayBanDisplayShell shell;
    private static QuestDisplayUI router;
    private static UINavigator navigator;

    static RayBanDisplayPreviewValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            { nextStep = EditorApplication.timeSinceStartup + 1; EditorApplication.update += Begin; }
        };
    }

    [MenuItem("Tools/Ray-Ban Display/Validate preview in Play Mode")]
    public static void Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before testing.");
        SessionState.SetBool(PendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private static void Begin()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        EditorApplication.update -= Begin;
        checks.Clear(); steps.Clear();
        try
        {
            shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
            router = shell.router;
            navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
            Check(shell.OwnsPreview, "Preview owns router");
            Check(shell.IsVisible == router.showMainMenuOnStart, "Original startup visibility respected");
            Check(shell.servicesToSuspend.All(x => !x.activeInHierarchy), "AI and raw-camera groups suspended (" + string.Join(", ", shell.servicesToSuspend.Select(x => x.name + "=" + x.activeInHierarchy)) + ")");
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            checks.Add("INFO: Runtime cameras: " + string.Join(", ", cameras.Select(c => c.name + " [" + c.gameObject.scene.name + "]")));
            var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
            Check(rig != null && rig.centerEyeAnchor.GetComponent<Camera>() != null && rig.leftEyeAnchor.GetComponent<Camera>() != null && rig.rightEyeAnchor.GetComponent<Camera>() != null, "Original three rig eye cameras retained");
            Check(shell.GetComponentsInChildren<Camera>(true).Length == 0, "Preview introduces no cameras into display hierarchy");
            Check(UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 1, "One original EventSystem retained");
            LegacyOff();
            steps.Enqueue(() => { router.ShowMainMenuIfHidden(); navigator.ClickSelected(); Check(shell.IsVisible && shell.CurrentDetail == "", "Reveal tap does not also open app"); });
            steps.Enqueue(() => { shell.SetPage(2); navigator.MoveRight(); Check(Selected().Title == "Instagram", "Right moves focus"); navigator.MoveDown(); Check(Selected().Title == "Messages", "Down moves focus"); });
            steps.Enqueue(() => { navigator.ClickSelected(); router.ShowMainMenuIfHidden(); Check(shell.CurrentDetail == "Messages" && Features.CurrentView == "messages", "Submit opens Messages; parallel reveal event preserves it"); Check(Selected() == FeatureControl("back"), "Feature screens initially focus header Back"); LegacyOff(); });
            steps.Enqueue(() => { navigator.ClickSelected(); Check(shell.CurrentDetail == "" && Selected().Title == "Messages", "Back restores prior app focus"); navigator.MoveLeft(); navigator.MoveLeft(); Check(shell.CurrentPage == 1, "Left edge opens notifications"); navigator.MoveLeft(); Check(shell.CurrentPage == 0, "Left edge opens quick settings"); });
            steps.Enqueue(() => { Select(shell.GetComponentsInChildren<RayBanTile>(true).First(t => t.ActionId == "hide")); navigator.ClickSelected(); router.ShowMainMenuIfHidden(); Check(!shell.IsVisible, "Close and parallel reveal tap stay closed"); });
            steps.Enqueue(() => { router.ShowMainMenuIfHidden(); navigator.ClickSelected(); Check(shell.IsVisible, "Next tap reveals without activating old close selection"); });
            steps.Enqueue(() => { shell.SetPage(2); for (int i = 0; i < 5; i++) navigator.MoveDown(); Check(Selected().Title == "Maps", "Down reaches last app row"); var scroll = shell.GetComponentInChildren<ScrollRect>(); Check(scroll.content.anchoredPosition.y > 250, "Focused lower apps scroll into view"); Check(Selected().GetComponent<CanvasGroup>().alpha > .99f, "Focused last-row app is fully visible"); navigator.MoveRight(); Check(Selected().Title == "Tutorials", "Right reaches final app"); });
            string[] apps = { "WhatsApp", "Instagram", "Messenger", "Messages", "Calls", "Camera", "Music", "Photos", "Captions", "Assistant", "Maps", "Tutorials" };
            string[] viewKeys = { "messages", "instagram", "messages", "messages", "calls", "camera", "music", "photos", "captions", "assistant", "maps", "tutorials" };
            for (int i = 0; i < apps.Length; i++)
            {
                string app = apps[i], view = viewKeys[i];
                steps.Enqueue(() => { shell.SetPage(2); Select(shell.GetComponentsInChildren<RayBanTile>(true).First(t => t.PageId == 2 && t.Title == app)); });
                steps.Enqueue(() => {
                    navigator.ClickSelected();
                    Check(shell.CurrentDetail == app && Features.CurrentView == view, app + " opens its sample screen");
                    Check(Selected() == FeatureControl("back"), app + " focuses the active header Back");
                    navigator.MoveDown();
                    Check(Selected().PageId == -2 && Selected().ActionId != "back" && Selected().gameObject.activeInHierarchy, app + " navigates from header to its controls");
                    navigator.MoveUp();
                    Check(Selected() == FeatureControl("back"), app + " navigates back to its header");
                    if (view == "messages") Check(VisibleText("Heading").text == app, app + " retains its messaging brand");
                    LegacyOff(); ServicesOff(app);
                });
                steps.Enqueue(() => { UseFeature("back"); Check(shell.CurrentDetail == "" && Selected().Title == app, app + " returns to its launcher selection"); });
            }
            QueueFeatureBehaviorChecks();
            steps.Enqueue(() => { shell.SetPage(2); router.HideMainMenu(); Check(!shell.IsVisible && EventSystem.current.currentSelectedGameObject == null, "Hide clears selection"); });
            EditorApplication.update += Tick;
            nextStep = EditorApplication.timeSinceStartup + .1;
        }
        catch (Exception e) { Finish(e); }
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        try
        {
            if (steps.Count == 0) { Finish(null); return; }
            steps.Dequeue()();
            nextStep = EditorApplication.timeSinceStartup + .12;
        }
        catch (Exception e) { Finish(e); }
    }

    private static RayBanFeatureScreen Features => shell.FeatureScreens;
    private static RayBanTile FeatureControl(string action) => Features.Controls.First(t => t != null && t.PageId == -2 && t.ActionId == action && t.gameObject.activeInHierarchy);
    private static TMP_Text VisibleText(string name) => Features.GetComponentsInChildren<TMP_Text>().First(t => t.name == name && t.gameObject.activeInHierarchy);
    private static RawImage VisibleImage(string name) => Features.GetComponentsInChildren<RawImage>().First(t => t.name == name && t.gameObject.activeInHierarchy);
    private static void UseFeature(string action) { Select(FeatureControl(action)); navigator.ClickSelected(); }

    private static void QueueFeatureBehaviorChecks()
    {
        string before = null;
        Texture previousPhoto = null;
        Rect previousCrop = default;
        steps.Enqueue(() => {
            Check(Features.samplePhotos.Length >= 3 && Features.samplePhotos.Take(3).All(t => t != null) && Features.samplePhotos.Take(3).Distinct().Count() == 3, "Three distinct local sample images are assigned");
            shell.OpenApp("Camera");
            Check(Features.CurrentView == "camera" && VisibleImage("Sample viewfinder").texture != null, "Camera displays a local sample viewfinder");
            before = VisibleText("Capture status").text;
        });
        steps.Enqueue(() => {
            UseFeature("camera.capture");
            Check(VisibleText("Capture status").text != before && VisibleText("Capture status").text.Contains("nothing saved"), "Sample shutter updates feedback without claiming a saved capture");
            previousCrop = VisibleImage("Sample viewfinder").uvRect;
            UseFeature("camera.zoom");
            Check(VisibleImage("Sample viewfinder").uvRect != previousCrop, "Sample zoom changes the local viewfinder crop");
            UseFeature("camera.photos");
            Check(Features.CurrentView == "photos", "Camera Photos control opens the sample gallery");
            previousPhoto = VisibleImage("Current sample photo").texture;
        });
        steps.Enqueue(() => {
            UseFeature("photos.next");
            Check(VisibleImage("Current sample photo").texture != previousPhoto, "Next photo displays another local image");
            UseFeature("photos.previous");
            Check(VisibleImage("Current sample photo").texture == previousPhoto, "Previous photo restores the prior image");
            UseFeature("back");
            Check(shell.CurrentDetail == "", "Gallery header returns to launcher");
            ServicesOff("Media controls");
        });
        steps.Enqueue(() => { shell.OpenApp("Music"); before = FeatureControl("music.toggle").Title; });
        steps.Enqueue(() => {
            UseFeature("music.toggle");
            Check(FeatureControl("music.toggle").Title != before && VisibleText("Playback preview status").text.Contains("no audio"), "Music toggles local play/pause feedback without audio");
            UseFeature("music.toggle");
            Check(FeatureControl("music.toggle").Title == before, "Music toggle restores its prior state");
            before = VisibleText("Track title").text;
            UseFeature("music.next");
            Check(VisibleText("Track title").text != before, "Next track updates sample metadata");
            UseFeature("music.previous");
            Check(VisibleText("Track title").text == before, "Previous track restores metadata");
            UseFeature("back");
        });
        steps.Enqueue(() => { shell.OpenApp("Assistant"); before = VisibleText("Sample response").text; });
        steps.Enqueue(() => {
            UseFeature("comm:assistant-listen");
            Check(VisibleText("Sample response").text != before, "Assistant switches between listening and answer samples");
            UseFeature("comm:assistant-listen");
            Check(VisibleText("Sample response").text == before, "Assistant sample answer returns without a service request");
            before = VisibleText("Question").text;
            UseFeature("comm:assistant-question");
            Check(VisibleText("Question").text != before, "Assistant supports another sample question");
            UseFeature("back"); ServicesOff("Assistant controls");
        });
        steps.Enqueue(() => { shell.OpenApp("Captions"); before = FeatureControl("comm:captions-pause").Title; });
        steps.Enqueue(() => {
            UseFeature("comm:captions-pause");
            Check(FeatureControl("comm:captions-pause").Title != before, "Captions toggles its local pause state");
            UseFeature("comm:captions-pause");
            before = VisibleText("Transcript").text;
            UseFeature("comm:captions-language");
            Check(VisibleText("Transcript").text != before, "Caption language control changes the sample transcript");
            UseFeature("comm:captions-language");
            Check(VisibleText("Transcript").text == before, "Caption language control returns to the prior language");
            UseFeature("back");
        });
        steps.Enqueue(() => { shell.OpenApp("Messages"); UseFeature("comm:message-1"); Check(Features.CurrentView == "message-thread", "Selecting a conversation opens its thread"); before = VisibleText("Outgoing message").text; });
        steps.Enqueue(() => {
            UseFeature("comm:message-reply");
            Check(VisibleText("Outgoing message").text != before && (VisibleText("Reply state").text.Contains("Not sent") || VisibleText("Reply state").text.Contains("No messages sent")), "Quick reply changes local mock text without a sent-message claim");
            UseFeature("comm:message-reply");
            Check(VisibleText("Outgoing message").text == before, "Reset reply restores sample conversation");
            UseFeature("back");
            Check(Features.CurrentView == "messages" && shell.CurrentDetail == "Messages" && Selected().ActionId == "comm:message-1", "Thread Back returns to inbox and restores contact focus");
            UseFeature("back");
            Check(shell.CurrentDetail == "", "Inbox Back returns to launcher");
        });
        steps.Enqueue(() => { shell.OpenApp("Calls"); UseFeature("comm:call-2"); Check(Features.CurrentView == "call-preview", "Recent call opens an offline call preview"); before = FeatureControl("comm:call-mute").Title; });
        steps.Enqueue(() => {
            UseFeature("comm:call-mute");
            Check(FeatureControl("comm:call-mute").Title != before && VisibleText("Call state").text.Contains("No connection"), "Call mute changes preview state without a connection");
            shell.Back();
            Check(Features.CurrentView == "calls" && Selected().ActionId == "comm:call-2", "Shared Back returns from call preview to recents");
            UseFeature("comm:call-0");
            UseFeature("comm:call-end");
            Check(Features.CurrentView == "calls" && Selected().ActionId == "comm:call-0", "End preview restores the chosen recent contact");
            UseFeature("back"); ServicesOff("Communication controls");
        });
        steps.Enqueue(() => { shell.OpenApp("Instagram"); previousPhoto = VisibleImage("Sample post photo").texture; before = FeatureControl("comm:instagram-like").Title; });
        steps.Enqueue(() => {
            UseFeature("comm:instagram-like");
            Check(FeatureControl("comm:instagram-like").Title != before, "Instagram Like updates local sample state");
            UseFeature("comm:instagram-like");
            Check(FeatureControl("comm:instagram-like").Title == before, "Instagram second Like restores the prior count");
            UseFeature("comm:instagram-next");
            Check(VisibleImage("Sample post photo").texture != previousPhoto, "Next post changes the local sample photo");
            UseFeature("back");
        });
        steps.Enqueue(() => {
            shell.OpenApp("Maps");
            before = FeatureControl("maps.toggle").Title;
            UseFeature("maps.toggle");
            Check(FeatureControl("maps.toggle").Title != before, "Maps starts or pauses the illustrative route preview");
            before = VisibleText("Turn instruction").text;
            UseFeature("maps.next");
            Check(VisibleText("Turn instruction").text != before, "Maps advances to another sample turn");
            UseFeature("back"); ServicesOff("Map preview controls");
        });
        steps.Enqueue(() => {
            shell.OpenApp("Tutorials");
            before = VisibleText("Tutorial title").text;
            UseFeature("tutorials.next");
            Check(VisibleText("Tutorial title").text != before, "Tutorial Next opens another gesture tip");
            UseFeature("tutorials.previous");
            Check(VisibleText("Tutorial title").text == before, "Tutorial Previous restores the original tip");
            UseFeature("back");
        });
        steps.Enqueue(() => {
            shell.SetPage(1);
            Select(shell.GetComponentsInChildren<RayBanTile>(true).First(t => t.PageId == 1 && t.ItemIndex == 0));
            navigator.ClickSelected();
            Check(Features.CurrentView == "notifications" && VisibleText("Sender").text == "Barbara Johnson", "Notification opens its own sample detail");
            UseFeature("notification.done");
            Check(shell.CurrentDetail == "" && shell.CurrentPage == 1, "Notification Done returns to dashboard");
            shell.OpenApp("Volume");
            Check(VisibleText("Heading").text == "Volume", "Sample volume uses its own heading");
            shell.Back();
            shell.OpenApp("Settings");
            Check(VisibleText("Heading").text == "Settings", "Settings heading resets after an adjustment screen");
            Check(Features.CurrentView == "settings" && FeatureControl("back") != null, "Settings opens a sample screen with header Back");
            navigator.MoveDown();
            Check(Selected().PageId == -2 && Selected().ActionId != "back", "Settings controls are reachable through shared navigation");
            before = VisibleText("Level").text;
            UseFeature(before == "100%" ? "settings.less" : "settings.more");
            Check(VisibleText("Level").text != before, "Settings adjustment changes the local displayed level");
            UseFeature("back");
            Check(shell.CurrentDetail == "", "Settings Back returns to launcher");
            LegacyOff(); ServicesOff("All sample feature interactions");
        });
    }

    private static RayBanTile Selected() => EventSystem.current.currentSelectedGameObject.GetComponent<RayBanTile>();
    private static void Select(RayBanTile tile) => EventSystem.current.SetSelectedGameObject(tile.gameObject);
    private static void LegacyOff() => Check(!router.mainMenuRoot.activeSelf && router.entries.All(e => !e.page.activeSelf), "Legacy launcher and all six feature pages remain inactive");
    private static void ServicesOff(string scenario) => Check(shell.servicesToSuspend.All(x => x != null && !x.activeInHierarchy), scenario + " leaves AI and raw-camera groups suspended");
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks.Add("PASS: " + label);
    }
    private static void Finish(Exception failure)
    {
        EditorApplication.update -= Tick;
        EditorApplication.update -= Begin;
        SessionState.SetBool(PendingKey, false);
        string report = "Ray-Ban Display UI preview / Unity Play Mode\n" + DateTime.Now.ToString("O") + "\n" +
            string.Join("\n", checks) + "\n" + (failure == null ? "RESULT: PASS (" + checks.Count(c => c.StartsWith("PASS:")) + " checks)" : "RESULT: FAIL: " + failure) +
            "\nNo Quest connected. Physical gestures, headset composition and Android build unverified.\n";
        Directory.CreateDirectory("Reports/2026-10-18-RayBanScreens");
        File.WriteAllText("Reports/2026-10-18-RayBanScreens/playmode-validation.txt", report);
        Debug.Log("[RayBanPreviewValidation] " + (failure == null ? "PASS " + checks.Count : "FAIL " + failure.Message));
        EditorApplication.isPlaying = false;
    }
}
