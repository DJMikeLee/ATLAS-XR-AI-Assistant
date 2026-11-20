using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Oculus.Voice.Dictation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>Offline Play Mode checks using public provider fixtures; never starts Maps network or voice requests.</summary>
[InitializeOnLoad]
public static class MapDestinationSearchValidation
{
    private const string State = "MapDestinationSearchValidation.";
    private const string Folder = "Reports/2026-10-18-MapDestinationSearch";
    private const string Fixtures = "Tools/MapValidation/Fixtures/";
    private static readonly List<string> checks = new List<string>();
    private static readonly Queue<Action> steps = new Queue<Action>();
    private static RayBanDisplayShell shell;
    private static UINavigator navigator;
    private static MapSearch search;
    private static AppDictationExperience originalDictation;
    private static bool originalReconnect, savedReferences, finishing;
    private static double nextStep;
    private static DestinationPlace alma, armory, eceb;
    private static int canceledVersion;

    static MapDestinationSearchValidation() { EditorApplication.playModeStateChanged += OnPlayState; }

    [MenuItem("Tools/Ray-Ban Display/Validate destination matching UI offline")]
    public static void Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            throw new InvalidOperationException("Start from idle Edit Mode after compilation finishes.");
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("Save or resolve existing scene changes before this Play Mode test.");
        var owner = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
        if (owner == null || owner.mapBridge == null || owner.mapBridge.search == null ||
            owner.mapBridge.search.gameObject.activeInHierarchy || owner.gameObject.scene.path != "Assets/Scenes/UI Quest Display.unity")
            throw new InvalidOperationException("Open the saved display scene with its legacy Map page inactive.");
        SessionState.SetFloat(State + "volume", AudioListener.volume);
        SessionState.SetBool(State + "pending", true);
        EditorApplication.isPlaying = true;
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
            Finish(new InvalidOperationException("Offline destination test interrupted."));
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            AudioListener.volume = SessionState.GetFloat(State + "volume", 1);
            SessionState.SetBool(State + "pending", false);
        }
    }

    private static void Begin()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        EditorApplication.update -= Begin;
        checks.Clear(); steps.Clear(); finishing = false; savedReferences = false;
        try
        {
            shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
            navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
            Check(shell != null && shell.UsesLiveFeatures && navigator != null, "Saved scene uses the live shell and original focused navigation");
            search = shell.mapBridge.search;
            Check(search != null && search.phoneTether != null && search.walkingNavigation != null &&
                search.walkingNavigation.provider != null && !search.gameObject.activeInHierarchy, "Map backend begins inactive with its existing components");
            originalDictation = search.dictationExperience;
            originalReconnect = search.phoneTether.AllowAutomaticReconnect;
            savedReferences = true;
            search.dictationExperience = null;
            search.phoneTether.AllowAutomaticReconnect = false;
            alma = ReadPlace("photon-alma-mater-statue-urbana-2026-10-18.json", "Alma Mater");
            armory = ReadPlace("photon-armor-2026-10-18.json", "Armory");
            eceb = ReadPlace("photon-eceb-full-name-2026-10-18.json", "Electrical and Computer Engineering Building");

            steps.Enqueue(() => shell.OpenApp("Maps"));
            steps.Enqueue(() =>
            {
                Check(shell.mapBridge.IsAcquired && shell.FeatureScreens.CurrentView == "maps", "Maps opens through the real display bridge");
                CheckNoRequests();
                Inject("Armor", alma, armory);
            });
            steps.Enqueue(() =>
            {
                Check(search.HasPendingDestination && search.HasAlternativeDestinations, "Two provider identities remain pending for explicit review");
                Check(Instructions.text.Contains("Did you mean") && Instructions.text.Contains("Alma Mater"), "First alternative presents the corrected statue name");
                Check(Control("maps.change-destination").Title == "Next match", "Existing change-destination tile displays Next match");
                var readout = shell.FeatureScreens.GetComponentsInChildren<TMP_InputField>().Single();
                Check(readout.text == "Armor" && readout.readOnly && !readout.interactable && !readout.enabled,
                    "Original recognized Armor text stays visible in the noninteractive readout");
                Check(!search.walkingNavigation.HasDestination, "Reviewing an interpretation does not establish a route destination");
                CheckFits("Alma Mater alternatives");
                RayBanDisplayPreviewTools.Capture(shell, Folder + "/maps-alma-alternatives-offline.png");
                Use("maps.change-destination");
            });
            steps.Enqueue(() =>
            {
                Check(Instructions.text.Contains("Armory") && Get<DestinationPlace>("pendingPlace").OsmReference == armory.OsmReference,
                    "Selecting Next match through the real tile and UINavigator selects Armory");
                Check(!search.walkingNavigation.HasDestination, "Cycling alternatives still starts no route");
                Use("maps.change-destination");
            });
            steps.Enqueue(() =>
            {
                Check(Get<DestinationPlace>("pendingPlace").OsmReference == alma.OsmReference, "Next match cycles back to the statue");
                Check(!search.phoneTether.HasUsableLocation, "Offline test has no usable GPS fix");
                Use("maps.submit");
            });
            steps.Enqueue(() =>
            {
                Check(!search.walkingNavigation.HasDestination && !search.walkingNavigation.provider.IsRequesting,
                    "Use destination without GPS rejects confirmation without requesting a route");
                Check(Instructions.text.Contains("Restore phone GPS"), "Missing GPS is explained after attempted confirmation");
                search.ChangeDestination();
                Check(!search.HasPendingDestination && Options.Count == 0 && search.searchInput.text == "",
                    "Change destination clears interpreted candidates and the original query");
                Inject("ECEB", eceb);
            });
            steps.Enqueue(() =>
            {
                Check(Instructions.text.Contains(eceb.Name) && Instructions.text.Contains(eceb.Address),
                    "ECEB review shows the complete provider name and address");
                CheckFits("ECEB long name and address");
                RayBanDisplayPreviewTools.Capture(shell, Folder + "/maps-eceb-review-offline.png");
                canceledVersion = Get<int>("searchVersion");
                search.enabled = false;
                Check(!search.HasPendingDestination && Options.Count == 0, "Disabling search clears all injected pending state");
                InvokeFinish(canceledVersion);
                Check(!search.HasPendingDestination && Options.Count == 0, "A stale completion cannot restore candidates after disable");
                search.enabled = true;
                shell.Back();
            });
            steps.Enqueue(() =>
            {
                Check(!shell.mapBridge.IsAcquired && !search.gameObject.activeInHierarchy, "Back releases Maps and its inactive legacy page");
                shell.OpenApp("Maps");
            });
            steps.Enqueue(() =>
            {
                Check(shell.mapBridge.IsAcquired && !search.HasPendingDestination && Options.Count == 0,
                    "Reopening Maps retains no injected destination candidates");
                CheckNoRequests();
                shell.Back();
            });
            nextStep = EditorApplication.timeSinceStartup + .35;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static DestinationPlace ReadPlace(string file, string query)
    {
        if (!DestinationSearchData.TrySelectPlace(File.ReadAllText(Fixtures + file), query,
            new GeoCoordinate(40.11, -88.228), true, out DestinationPlace place, out string error))
            throw new InvalidOperationException("Public provider fixture failed: " + file + ": " + error);
        return place;
    }

    private static List<DestinationPlace> Options => Get<List<DestinationPlace>>("pendingOptions");
    private static TMP_Text Instructions => shell.FeatureScreens.GetComponentsInChildren<TMP_Text>()
        .Single(text => text.name == "Map instructions" && text.gameObject.activeInHierarchy);
    private static RayBanTile Control(string action) => shell.FeatureScreens.Controls
        .First(tile => tile != null && tile.ActionId == action && tile.gameObject.activeInHierarchy);
    private static void Use(string action)
    {
        var tile = Control(action);
        Check(tile.IsInteractable(), "Focused control is available: " + action);
        EventSystem.current.SetSelectedGameObject(tile.gameObject);
        navigator.ClickSelected();
    }

    private static void Inject(string originalQuery, params DestinationPlace[] places)
    {
        search.ChangeDestination();
        Set("isWaitingForRoomCode", false);
        Set("awaitingPhone", false);
        search.searchInput.SetTextWithoutNotify(originalQuery);
        Options.Clear(); Options.AddRange(places);
        Set("assistedResult", true);
        InvokeFinish(Get<int>("searchVersion"));
    }

    private static T Get<T>(string name) => (T)Field(name).GetValue(search);
    private static void Set(string name, object value) => Field(name).SetValue(search, value);
    private static FieldInfo Field(string name) => typeof(MapSearch).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing test injection field: " + name);
    private static void InvokeFinish(int version)
    {
        var method = typeof(MapSearch).GetMethod("FinishSearch", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new InvalidOperationException("Missing search completion method.");
        method.Invoke(search, new object[] { version, null });
    }

    private static void CheckFits(string scenario)
    {
        Canvas.ForceUpdateCanvases();
        Instructions.ForceMeshUpdate(true);
        Check(!Instructions.isTextOverflowing, scenario + " fits the actual compact display instructions");
    }

    private static void CheckNoRequests()
    {
        Check(!search.phoneTether.isSocketOpen && search.phoneTether.State == PhoneTether.ConnectionState.Idle &&
            !search.walkingNavigation.provider.IsRequesting && Get<UnityEngine.Networking.UnityWebRequest>("activeRequest") == null &&
            !Get<bool>("acceptsSpeech"), "No relay, route, destination, AI hint or dictation request is active");
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
            if (savedReferences && search != null)
            {
                if (shell != null) { shell.Back(); shell.mapBridge.Release(); }
                search.dictationExperience = originalDictation;
                search.phoneTether.AllowAutomaticReconnect = originalReconnect;
                if (shell != null && shell.appSettings != null) shell.appSettings.Deactivate();
            }
            AudioListener.volume = SessionState.GetFloat(State + "volume", 1);
        }
        catch (Exception cleanup) { checks.Add("CLEANUP: " + cleanup); if (failure == null) failure = cleanup; }
        string report = "Destination confirmation UI / offline Unity Play Mode\n" + DateTime.Now.ToString("O") + "\n" +
            string.Join("\n", checks) + "\n" + (failure == null ? "RESULT: PASS (" + checks.Count + " checks)" : "RESULT: FAIL: " + failure) +
            "\nPublic Photon fixture identities were injected into pending review state, not obtained from live search in this test.\n" +
            "No phone relay connection, location injection, recognition, AI request, destination lookup, route request or camera capture was initiated.\n" +
            "Screenshots render the actual Unity display in an isolated preview scene.\n" +
            "Dictation reference, reconnect policy and app audio restored; Play Mode stopped. Physical Quest behavior remains unverified by this harness.\n";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Folder + "/offline-map-ui-validation.txt", report);
            Debug.Log("[MapDestinationSearchValidation] " + (failure == null ? "PASS " + checks.Count : "FAIL " + failure.Message));
        }
        finally { if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; }
    }
}
