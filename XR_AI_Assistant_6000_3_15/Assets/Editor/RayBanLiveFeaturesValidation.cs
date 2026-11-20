using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Meta.XR;
using Oculus.Voice.Dictation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Offline integration checks. Never starts recognition, connects a relay or requests a route.</summary>
[InitializeOnLoad]
public static class RayBanLiveFeaturesValidation
{
    private const string State = "RayBanDisplay.LiveFeaturesValidation.";
    private const string RoomKey = "XR_AI_Assistant.PhoneTracker.LastSuccessfulRoom";
    private const string Folder = "Reports/2026-10-18-MapReadout";
    private static readonly List<string> checks = new List<string>();
    private static readonly Queue<Action> steps = new Queue<Action>();
    private static RayBanDisplayShell shell;
    private static UINavigator navigator;
    private static AIManager actualAssistant;
    private static GameObject actualAssistantRoot;
    private static LiveTranslationManager actualCaptions;
    private static MapSearch actualMap;
    private static AppDictationExperience actualDictation;
    private static PassthroughCameraAccess actualCamera;
    private static GameObject fakeAssistantRoot;
    private static TMP_Text fakeAnswer;
    private static bool referencesSaved, finishing;
    private static double nextStep;
    private static HashSet<int> audioSources;
    private static int cameraCount, listenerCount;
    private static PageState captionPage, mapPage;
    private static RectState mapRect;
    private static int originalSourceLanguage, originalTargetLanguage;
    private static bool originalTranslationView, originalReconnect;

    private sealed class PageState
    {
        private readonly GameObject root;
        private readonly bool active;
        private readonly Dictionary<Behaviour, bool> components;
        private readonly Dictionary<GameObject, bool> children;
        public PageState(GameObject value, bool saveChildren)
        {
            root = value; active = root.activeSelf;
            components = root.GetComponents<Behaviour>().ToDictionary(c => c, c => c.enabled);
            children = new Dictionary<GameObject, bool>();
            if (saveChildren) foreach (Transform child in root.transform) children[child.gameObject] = child.gameObject.activeSelf;
        }
        public bool Restored => root != null && root.activeSelf == active &&
            components.All(pair => pair.Key != null && pair.Key.enabled == pair.Value) &&
            children.All(pair => pair.Key != null && pair.Key.activeSelf == pair.Value);
    }

    private sealed class RectState
    {
        private readonly RectTransform rect;
        private readonly Transform parent;
        private readonly int sibling;
        private readonly Vector2 anchorMin, anchorMax, pivot, size, position;
        private readonly Vector3 scale;
        private readonly Quaternion rotation;
        public RectState(RectTransform value)
        {
            rect = value; parent = rect.parent; sibling = rect.GetSiblingIndex();
            anchorMin = rect.anchorMin; anchorMax = rect.anchorMax; pivot = rect.pivot;
            size = rect.sizeDelta; position = rect.anchoredPosition; scale = rect.localScale; rotation = rect.localRotation;
        }
        public bool Restored => rect != null && rect.parent == parent && rect.GetSiblingIndex() == sibling &&
            rect.anchorMin == anchorMin && rect.anchorMax == anchorMax && rect.pivot == pivot &&
            rect.sizeDelta == size && rect.anchoredPosition == position && rect.localScale == scale && rect.localRotation == rotation;
    }

    static RayBanLiveFeaturesValidation() { EditorApplication.playModeStateChanged += OnPlayState; }

    [MenuItem("Tools/Ray-Ban Display/Validate Assistant Captions Maps offline")]
    public static void Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before validation.");
        var owner = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
        if (owner == null || owner.mapBridge == null || owner.mapBridge.search == null || owner.mapBridge.search.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Open the saved display scene with its legacy Map page inactive before testing.");
        if (SessionState.GetBool(State + "pending", false)) RestorePreference();
        SessionState.SetBool(State + "roomExists", PlayerPrefs.HasKey(RoomKey));
        SessionState.SetString(State + "room", PlayerPrefs.GetString(RoomKey, ""));
        SessionState.SetFloat(State + "volume", AudioListener.volume);
        SessionState.SetBool(State + "pending", true);
        // A remembered room must be presented for explicit confirmation rather
        // than connected on page open. Restore this test fixture on every exit.
        PlayerPrefs.SetString(RoomKey, "2468");
        PlayerPrefs.Save();
        EditorApplication.isPlaying = true;
    }

    private static void RestorePreference()
    {
        if (SessionState.GetBool(State + "roomExists", false)) PlayerPrefs.SetString(RoomKey, SessionState.GetString(State + "room", ""));
        else PlayerPrefs.DeleteKey(RoomKey);
        PlayerPrefs.Save();
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(State + "pending", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            nextStep = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update -= Begin;
            EditorApplication.update += Begin;
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && !finishing)
            Finish(new InvalidOperationException("Offline validation interrupted before completion."));
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestorePreference();
            AudioListener.volume = SessionState.GetFloat(State + "volume", 1f);
            SessionState.SetBool(State + "pending", false);
        }
    }

    private static void Begin()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        EditorApplication.update -= Begin;
        checks.Clear(); steps.Clear(); finishing = false; referencesSaved = false;
        try
        {
            shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
            navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
            Check(shell != null && shell.UsesLiveFeatures && navigator != null, "Live shell uses the original focused input path");
            Check(shell.assistantBridge != null && shell.assistantBridge.manager != null && shell.assistantBridge.serviceRoot != null, "Assistant points at the original service group");
            Check(shell.captionBridge != null && shell.captionBridge.manager != null, "Captions points at the existing translation manager");
            Check(shell.mapBridge != null && shell.mapBridge.search != null && shell.mapBridge.Navigation != null, "Maps points at the original search and walking navigation");
            actualAssistant = shell.assistantBridge.manager;
            actualAssistantRoot = shell.assistantBridge.serviceRoot;
            actualCaptions = shell.captionBridge.manager;
            actualMap = shell.mapBridge.search;
            actualDictation = actualMap.dictationExperience;
            actualCamera = shell.cameraBridge.cameraAccess;
            referencesSaved = true;
            Check(!actualAssistantRoot.activeInHierarchy && !actualMap.gameObject.activeInHierarchy, "Voice and Map legacy groups begin inactive");
            captionPage = new PageState(actualCaptions.gameObject, true);
            mapPage = new PageState(actualMap.gameObject, false);
            mapRect = new RectState(actualMap.walkingNavigation.mapView.transform as RectTransform);
            originalSourceLanguage = actualCaptions.SourceIndex;
            originalTargetLanguage = actualCaptions.TargetIndex;
            originalTranslationView = actualCaptions.ShowTranslation;
            originalReconnect = actualMap.phoneTether.AllowAutomaticReconnect;
            audioSources = All<AudioSource>().Select(a => a.GetInstanceID()).ToHashSet();
            cameraCount = All<Camera>().Length; listenerCount = All<AudioListener>().Length;
            // These references stay disconnected throughout the test. Opening
            // Assistant cannot activate real Voice SDK or the raw camera stream.
            shell.assistantBridge.manager = null;
            shell.assistantBridge.serviceRoot = null;
            shell.cameraBridge.cameraAccess = null;
            actualMap.dictationExperience = null;
            QueueAssistant(); QueueCaptions(); QueueMaps();
            steps.Enqueue(() => { shell.OpenApp("Settings"); });
            steps.Enqueue(() => { Capture("settings-ready"); shell.OpenApp("Music"); });
            steps.Enqueue(() =>
            {
                Check(shell.musicBridge.IsAcquired && !shell.musicBridge.IsPlaying, "Music remains prepared without autoplay during the visual review");
                Capture("music-ready");
                Use("back");
                shell.musicBridge.Release();
            });
            steps.Enqueue(() =>
            {
                Check(!actualAssistantRoot.activeInHierarchy, "Offline checks never activated the Assistant service group");
                Check(audioSources.SetEquals(All<AudioSource>().Select(a => a.GetInstanceID())), "Integrations reuse existing audio sources without duplicates");
                Check(All<Camera>().Length == cameraCount && All<AudioListener>().Length == listenerCount && All<EventSystem>().Length == 1, "Original camera, listener and EventSystem counts are preserved");
                Check(shell.cameraBridge.CapturedCount == 0, "Offline checks captured no camera images");
                Check(!actualMap.phoneTether.isSocketOpen && actualMap.phoneTether.State == PhoneTether.ConnectionState.Idle && !actualMap.walkingNavigation.provider.IsRequesting, "No relay or route request remains after closing Maps");
            });
            nextStep = EditorApplication.timeSinceStartup + .3;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static T[] All<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    private static RayBanFeatureScreen Features => shell.FeatureScreens;
    private static RayBanTile Control(string action) => Features.Controls.First(t => t != null && t.ActionId == action && t.gameObject.activeInHierarchy);
    private static TMP_Text Text(string name) => Features.GetComponentsInChildren<TMP_Text>().First(t => t.name == name && t.gameObject.activeInHierarchy);
    private static void Use(string action) { EventSystem.current.SetSelectedGameObject(Control(action).gameObject); navigator.ClickSelected(); }
    private static RayBanTile Selected => EventSystem.current.currentSelectedGameObject != null ? EventSystem.current.currentSelectedGameObject.GetComponent<RayBanTile>() : null;
    private static void Capture(string name) => RayBanDisplayPreviewTools.Capture(shell, Folder + "/" + name + ".png");

    private static void QueueAssistant()
    {
        string answer = "Offline layout test. This text was injected locally and is not an AI response. <b>Literal text</b> remains visible. " +
            string.Join(" ", Enumerable.Repeat("A complete answer should stay readable when it spans multiple display pages.", 12));
        steps.Enqueue(() => { shell.OpenApp("Assistant"); });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "assistant" && Text("Assistant status text").text.Contains("not connected"), "Missing Assistant service shows explicit unavailability");
            Check(!Control("comm:assistant-listen").IsInteractable() && !Control("comm:assistant-question").IsInteractable(), "Unavailable Assistant cannot start microphone or clear a nonexistent session");
            Check(!Text("Sample response").text.Contains("Illini") && Text("Sample response").text.Contains("response will appear"), "Live Assistant starts without an invented sample answer");
            Capture("assistant-unavailable");
            Use("back");
            Check(shell.CurrentDetail == "" && !shell.assistantBridge.IsAcquired, "Unavailable Assistant returns to the launcher cleanly");
            fakeAssistantRoot = new GameObject("Offline Assistant output fixture");
            fakeAssistantRoot.SetActive(false);
            var fakeManager = fakeAssistantRoot.AddComponent<AIManager>();
            var output = new GameObject("Offline response text", typeof(RectTransform));
            output.transform.SetParent(fakeAssistantRoot.transform, false);
            fakeAnswer = output.AddComponent<TextMeshProUGUI>();
            fakeAnswer.text = answer;
            fakeManager.aiResponseText = (TextMeshProUGUI)fakeAnswer;
            shell.assistantBridge.manager = fakeManager;
            shell.OpenApp("Assistant");
        });
        steps.Enqueue(() =>
        {
            var rendered = Text("Sample response");
            rendered.ForceMeshUpdate(true);
            Check(rendered.text == answer && !rendered.richText, "Live answer binding preserves complete provider text as plain text");
            Check(rendered.overflowMode == TextOverflowModes.Page && rendered.textInfo.pageCount > 1, "Long response uses actual TMP layout pages instead of truncation");
            Check(!shell.assistantBridge.IsAcquired && !shell.assistantBridge.CanListen, "Injected output remains isolated from recording and network services");
            navigator.MoveDown();
            Check(Selected != null && Selected.ActionId == "comm:assistant-question", "Focused navigation reaches paging and skips unavailable Listen");
            Use("comm:assistant-question");
            Check(rendered.pageToDisplay == 2, "Next page advances the actual laid-out response");
            Capture("assistant-layout-offline");
            fakeAnswer.text = "A new local fixture answer.";
        });
        steps.Enqueue(() =>
        {
            Check(Text("Sample response").text == fakeAnswer.text && Text("Sample response").pageToDisplay == 1, "A replacement response resets to its first page");
            Use("back");
            shell.assistantBridge.manager = null;
            UnityEngine.Object.Destroy(fakeAssistantRoot);
            fakeAssistantRoot = null;
            Check(!actualAssistantRoot.activeInHierarchy, "Reading and paging responses never enables the original voice group");
        });
    }

    private static void QueueCaptions()
    {
        int source = 0, target = 0;
        bool translated = false;
        steps.Enqueue(() => { shell.captionBridge.manager = null; shell.OpenApp("Captions"); });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "captions" && Text("Caption playback state").text.Contains("not connected"), "Missing caption service is reported");
            Check(!Control("comm:captions-pause").IsInteractable(), "Missing captions cannot start recognition");
            Check(Text("Transcript").text.Contains("Start captions") && !Text("Transcript").text.Contains("Union"), "Live captions do not display the former invented transcript");
            Capture("captions-unavailable");
            Use("back");
            shell.captionBridge.manager = actualCaptions;
            shell.OpenApp("Captions");
        });
        steps.Enqueue(() =>
        {
            Check(shell.captionBridge.IsAcquired && !actualCaptions.IsListening && !actualCaptions.IsBusy, "Opening real Captions prepares the manager without recording");
            Check(actualCaptions.History == "" && actualCaptions.CurrentText == "", "Fresh caption session contains no generated transcript");
            Check(Control("comm:captions-pause").Title == "Start captions", "Captions requires an explicit Start action");
            Check(actualCaptions.transform.Cast<Transform>().All(t => !t.gameObject.activeSelf), "Legacy caption controls remain hidden while backend is leased");
            source = actualCaptions.SourceIndex; target = actualCaptions.TargetIndex; translated = actualCaptions.ShowTranslation;
            navigator.MoveDown();
            Check(Selected != null && Selected.ActionId == "comm:captions-source", "Caption language controls use existing focus navigation");
            Use("comm:captions-source");
            Check(actualCaptions.SourceIndex == (source + 1) % 5 && actualCaptions.TargetIndex == target, "Spoken-language control updates the actual manager independently");
            Use("comm:captions-target");
            Check(actualCaptions.TargetIndex == (target + 1) % 5, "Target-language control updates the actual manager");
            Use("comm:captions-language");
            Check(actualCaptions.ShowTranslation != translated && !actualCaptions.IsListening, "Original/translated view changes without starting the microphone");
            Capture("captions-ready");
            actualCaptions.SetLanguages(originalSourceLanguage, originalTargetLanguage);
            actualCaptions.SetTranslationView(originalTranslationView);
            Use("back");
        });
        steps.Enqueue(() =>
        {
            Check(!shell.captionBridge.IsAcquired && !actualCaptions.IsListening && captionPage.Restored, "Leaving Captions stops the backend and restores legacy component/child states");
            shell.OpenApp("Captions");
        });
        steps.Enqueue(() =>
        {
            Check(shell.captionBridge.IsAcquired && !actualCaptions.IsListening, "Reopening Captions still requires an explicit Start");
            shell.Hide();
        });
        steps.Enqueue(() => { Check(!shell.captionBridge.IsAcquired && captionPage.Restored, "Close display releases Captions and preserves its original page"); });
    }

    private static void QueueMaps()
    {
        TMP_InputField field = null;
        steps.Enqueue(() => { shell.mapBridge.search = null; shell.OpenApp("Maps"); });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "maps" && Text("Phone connection state").text.Contains("not connected"), "Missing Map components show explicit unavailability");
            Check(!Control("maps.submit").IsInteractable() && !Control("maps.listen").IsInteractable(), "Unavailable Map cannot connect or start dictation");
            Check(!Features.GetComponentsInChildren<TMP_Text>().Any(t => t.text.Contains("Illini Union · 450")), "Live Maps removes illustrative destination and distance");
            Capture("maps-unavailable");
            Use("back");
            shell.mapBridge.search = actualMap;
            shell.OpenApp("Maps");
        });
        steps.Enqueue(() =>
        {
            var phone = actualMap.phoneTether;
            Check(shell.mapBridge.IsAcquired, "Maps leases the original search and walking controllers");
            Check(!phone.AllowAutomaticReconnect && phone.State == PhoneTether.ConnectionState.Idle && !phone.isSocketOpen, "Opening a remembered room does not automatically connect the relay");
            Check(shell.mapBridge.InputText == "2468" && Control("maps.submit").Title == "Connect", "Remembered room is shown for explicit Connect confirmation");
            Check(!shell.mapBridge.IsRouteShowing && !actualMap.walkingNavigation.HasRoute && !actualMap.walkingNavigation.IsLoading && !actualMap.walkingNavigation.provider.IsRequesting, "Opening Maps starts no invented route or provider request");
            Check(!Control("maps.listen").IsInteractable(), "Offline validation keeps real dictation disconnected");
            field = Control("maps.edit").GetComponentInChildren<TMP_InputField>();
            Check(field.text == "2468" && !field.richText, "Readout displays the saved room code as plain text");
            Check(field.readOnly && !field.enabled && !field.interactable && !Control("maps.edit").IsInteractable(), "Room readout and its container cannot enter text editing or button navigation");
            Check(Control("maps.edit").GetComponentsInChildren<Graphic>(true).All(g => !g.raycastTarget), "Readout does not intercept pointer or hand raycasts");
            EventSystem.current.SetSelectedGameObject(Control("back").gameObject);
            navigator.MoveDown();
            Check(Selected != null && Selected.ActionId == "maps.submit", "Swiping down skips the readout and unavailable Speak to reach Connect");
            navigator.MoveUp();
            Check(Selected != null && Selected.ActionId == "back", "Swiping up skips the readout and returns to Back");
            Capture("maps-ready");
            Features.Activate(Control("maps.edit"));
            Check(!field.isFocused && !DisplayTextInputFocus.IsEditing, "Submitting the readout cannot activate text editing");
            // Simulate stale EventSystem selection without starting speech or a connection.
            EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        });
        steps.Enqueue(() =>
        {
            Check(!field.isFocused && !DisplayTextInputFocus.IsEditing && Selected != null && Selected.ActionId == "back", "Stale readout focus recovers to Back when speech is unavailable");
            actualMap.dictationExperience = actualDictation; // Enable only navigation to Speak; never submit it.
            shell.mapBridge.SetInput("1357");
        });
        steps.Enqueue(() =>
        {
            Check(field.text == "1357", "Recognized/controller room code changes update the non-editable display");
            navigator.MoveDown();
            Check(Selected != null && Selected.ActionId == "maps.listen", "Swiping down reaches Speak directly when dictation is available");
            navigator.MoveRight();
            Check(Selected != null && Selected.ActionId == "maps.submit", "Speak and Connect remain horizontally navigable");
            navigator.MoveLeft(); navigator.MoveUp();
            Check(Selected != null && Selected.ActionId == "back", "Swiping up from Speak returns to Back without selecting the readout");
            EventSystem.current.SetSelectedGameObject(Control("maps.edit").gameObject);
        });
        steps.Enqueue(() =>
        {
            Check(Selected != null && Selected.ActionId == "maps.listen" && !field.isFocused, "Stale readout-container focus recovers to Speak");
            shell.mapBridge.SetInput("");
        });
        steps.Enqueue(() =>
        {
            Check(field.text == "" && field.placeholder.gameObject.activeSelf, "Clearing input shows the room-code placeholder");
            shell.mapBridge.SetInput("Illini Union");
        });
        steps.Enqueue(() =>
        {
            Check(field.text == "Illini Union" && !field.placeholder.gameObject.activeSelf, "Shared readout also displays recognized destination text");
            shell.mapBridge.SetInput("2468");
            actualMap.dictationExperience = null;
            Check(actualMap.phoneTether.State == PhoneTether.ConnectionState.Idle && !actualMap.phoneTether.isSocketOpen, "Map remains disconnected while waiting for user confirmation");
            Use("back");
        });
        steps.Enqueue(() =>
        {
            Check(!shell.mapBridge.IsAcquired && mapRect.Restored && mapPage.Restored, "Map release restores original renderer parent, geometry and page component states");
            Check(actualMap.phoneTether.AllowAutomaticReconnect == originalReconnect, "Map release restores the legacy reconnect policy");
            shell.OpenApp("Maps");
        });
        steps.Enqueue(() =>
        {
            Check(shell.mapBridge.IsAcquired && actualMap.phoneTether.State == PhoneTether.ConnectionState.Idle && !actualMap.phoneTether.isSocketOpen, "Reopening Maps still waits for explicit Connect");
            shell.Hide();
        });
        steps.Enqueue(() => { Check(!shell.mapBridge.IsAcquired && mapRect.Restored && mapPage.Restored, "Close display releases Maps and restores its original renderer"); });
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        try
        {
            if (steps.Count == 0) { Finish(null); return; }
            steps.Dequeue()();
            nextStep = EditorApplication.timeSinceStartup + .35;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks.Add("PASS: " + label);
    }

    private static void Finish(Exception failure)
    {
        if (finishing) return;
        finishing = true;
        EditorApplication.update -= Begin; EditorApplication.update -= Tick;
        try
        {
            if (shell != null && referencesSaved)
            {
                shell.assistantBridge.Release(); shell.captionBridge.Release(); shell.mapBridge.Release();
                if (shell.musicBridge != null) shell.musicBridge.Release();
                shell.cameraBridge.SetCameraVisible(false);
                shell.assistantBridge.manager = actualAssistant; shell.assistantBridge.serviceRoot = actualAssistantRoot;
                shell.captionBridge.manager = actualCaptions; shell.mapBridge.search = actualMap;
                shell.cameraBridge.cameraAccess = actualCamera;
                if (actualMap != null) actualMap.dictationExperience = actualDictation;
                if (actualCaptions != null)
                {
                    actualCaptions.SetLanguages(originalSourceLanguage, originalTargetLanguage);
                    actualCaptions.SetTranslationView(originalTranslationView);
                }
                if (shell.appSettings != null) shell.appSettings.Deactivate();
            }
            if (fakeAssistantRoot != null) UnityEngine.Object.Destroy(fakeAssistantRoot);
        }
        catch (Exception cleanup)
        {
            checks.Add("CLEANUP: " + cleanup);
            if (failure == null) failure = cleanup;
        }
        finally
        {
            RestorePreference();
            AudioListener.volume = SessionState.GetFloat(State + "volume", 1f);
        }
        int count = checks.Count(c => c.StartsWith("PASS:"));
        string report = "Ray-Ban Assistant/Captions/Maps offline integration / Unity Play Mode\n" + DateTime.Now.ToString("O") + "\n" +
            string.Join("\n", checks) + "\n" + (failure == null ? "RESULT: PASS (" + count + " checks)" : "RESULT: FAIL: " + failure) +
            "\nNo recording, relay Connect, destination search, route request, camera capture or AI response request was initiated.\n" +
            "Assistant layout text was injected into an inactive local fixture, not generated by an AI service.\n" +
            "Saved phone-room preference, service references, language selection and app audio gain restored.\n" +
            "Actual microphone, AI/TTS responses, translated captions, GPS, routing and physical Quest behavior remain device tests.\n";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Folder + "/offline-validation.txt", report);
            Debug.Log("[RayBanLiveFeaturesValidation] " + (failure == null ? "PASS " + count : "FAIL " + failure.Message));
        }
        finally { if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; }
    }
}
