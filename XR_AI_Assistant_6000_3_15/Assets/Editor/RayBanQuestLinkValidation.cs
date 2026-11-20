using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Meta.XR;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Physical Link smoke checks. Reports metadata only; never requests speech or uploads images.</summary>
[InitializeOnLoad]
public static class RayBanQuestLinkValidation
{
    private const string State = "RayBanDisplay.QuestLinkValidation.";
    private const string RoomKey = "XR_AI_Assistant.PhoneTracker.LastSuccessfulRoom";
    private const string Report = "Reports/2026-10-18-QuestLink/live-smoke-validation.txt";
    private static readonly List<string> results = new List<string>();
    private static readonly Queue<Step> steps = new Queue<Step>();
    private static readonly Dictionary<string, int> diagnostics = new Dictionary<string, int>();
    private static RayBanDisplayShell shell;
    private static UINavigator navigator;
    private static PassthroughCameraAccess camera;
    private static OVRHand[] hands;
    private static Step current;
    private static double deadline, startTime;
    private static bool finishing, snapshots, sampleCamera, captureRequested, cameraInitiallyActive, cameraInitiallyEnabled;
    private static int failures, sampledFrames, freshFrames, previewFrames, trackedFrames, validHandFrames, lastFrame = -1;
    private static int initialPhotos, initialCameras, initialListeners, sourceLanguage, targetLanguage;
    private static int micBefore, micPrepared, micClosed, width, height;
    private static bool translated, reconnect;
    private static float volume, brightness, playbackTime, pausedTime;
    private static DateTime lastTimestamp;
    private static Transform mapParent;
    private static GameObject assistantRoot;
    private static bool assistantRootActive, aiEnabled, wakeEnabled, voiceEnabled, actionOnly, keyboard, autoListen;

    private sealed class Step
    {
        public string Name;
        public Action Start, End;
        public Func<bool> Ready;
        public double Seconds;
    }

    private sealed class ValidationFailure : Exception
    {
        public ValidationFailure(string message) : base(message) { }
    }

    static RayBanQuestLinkValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        // An assembly reload during an active run must never leave test preferences behind.
        if (SessionState.GetBool(State + "running", false)) EditorApplication.delayCall += RecoverInterrupted;
    }

    [MenuItem("Tools/Ray-Ban Display/Validate connected Quest Link")]
    public static void Start()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            throw new InvalidOperationException("Wait for Unity compilation and imports before testing.");
        if (SessionState.GetBool(State + "pending", false))
            throw new InvalidOperationException("A Link validation is already pending.");
        SavePreference(RayBanAppSettings.VolumeKey, "volume");
        SavePreference(RayBanAppSettings.BrightnessKey, "brightness");
        SessionState.SetBool(State + "roomExists", PlayerPrefs.HasKey(RoomKey));
        SessionState.SetString(State + "room", PlayerPrefs.GetString(RoomKey, ""));
        SessionState.SetFloat(State + "listener", AudioListener.volume);
        SessionState.SetBool(State + "ownsPlay", !EditorApplication.isPlaying || SessionState.GetBool("Codex.QuestLinkTest.StartedPlay", false));
        SessionState.SetBool(State + "pending", true);
        if (EditorApplication.isPlaying) Begin();
        else EditorApplication.isPlaying = true;
    }

    private static void SavePreference(string key, string suffix)
    {
        SessionState.SetBool(State + suffix + "Exists", PlayerPrefs.HasKey(key));
        SessionState.SetFloat(State + suffix, PlayerPrefs.GetFloat(key, 1f));
    }

    private static void RestorePreferences()
    {
        foreach (string suffix in new[] { "volume", "brightness" })
        {
            string key = suffix == "volume" ? RayBanAppSettings.VolumeKey : RayBanAppSettings.BrightnessKey;
            if (SessionState.GetBool(State + suffix + "Exists", false)) PlayerPrefs.SetFloat(key, SessionState.GetFloat(State + suffix, 1f));
            else PlayerPrefs.DeleteKey(key);
        }
        if (SessionState.GetBool(State + "roomExists", false)) PlayerPrefs.SetString(RoomKey, SessionState.GetString(State + "room", ""));
        else PlayerPrefs.DeleteKey(RoomKey);
        PlayerPrefs.Save();
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(State + "pending", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Begin;
        else if (state == PlayModeStateChange.ExitingPlayMode && !finishing) Finish("Play Mode interrupted");
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestorePreferences();
            AudioListener.volume = SessionState.GetFloat(State + "listener", 1f);
            SessionState.SetBool(State + "pending", false);
            SessionState.SetBool(State + "running", false);
            SessionState.SetBool("Codex.QuestLinkTest.StartedPlay", false);
        }
    }

    private static void RecoverInterrupted()
    {
        if (!SessionState.GetBool(State + "running", false) || shell != null) return;
        RestorePreferences();
        Directory.CreateDirectory(Path.GetDirectoryName(Report));
        File.AppendAllText(Report, "\nINTERRUPTED: assembly reload; preferences restored.\n");
        SessionState.SetBool(State + "running", false);
        if (EditorApplication.isPlaying && SessionState.GetBool(State + "ownsPlay", false)) EditorApplication.isPlaying = false;
        else SessionState.SetBool(State + "pending", false);
    }

    private static void Begin()
    {
        if (!EditorApplication.isPlaying || !SessionState.GetBool(State + "pending", false)) return;
        results.Clear(); steps.Clear(); diagnostics.Clear(); current = null;
        finishing = snapshots = sampleCamera = captureRequested = false;
        failures = sampledFrames = freshFrames = previewFrames = trackedFrames = validHandFrames = width = height = 0;
        lastFrame = -1; lastTimestamp = default; startTime = EditorApplication.timeSinceStartup;
        SessionState.SetBool(State + "running", true);
        Application.logMessageReceived += OnLog;
        results.Add("Unity " + Application.unityVersion + "; Editor Play Mode / Quest Link; UTC " + DateTime.UtcNow.ToString("O"));
        Add("Wait for live HMD", null, () =>
        {
            Require(OVRManager.isHmdPresent, "Live OVR HMD was not detected within 20 seconds; no feature actions were run.");
            results.Add("HMD: " + OVRPlugin.GetSystemHeadsetType());
            Setup();
        }, 20, () => OVRManager.isHmdPresent);
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
    }

    private static void Setup()
    {
        shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
        navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
        Require(shell != null && shell.UsesLiveFeatures && navigator != null && EventSystem.current != null, "Live shell / focused input is unavailable.");
        Require(shell.cameraBridge != null && shell.cameraBridge.cameraAccess != null && shell.cameraBridge.pictureTaker != null &&
            shell.assistantBridge != null && shell.assistantBridge.manager != null && shell.captionBridge != null && shell.captionBridge.manager != null &&
            shell.mapBridge != null && shell.mapBridge.search != null && shell.mapBridge.Navigation != null && shell.appSettings != null,
            "One or more live feature references are missing.");
        shell.Hide();
        camera = shell.cameraBridge.cameraAccess;
        cameraInitiallyActive = camera.gameObject.activeSelf; cameraInitiallyEnabled = camera.enabled;
        initialPhotos = shell.cameraBridge.CapturedCount;
        initialCameras = All<Camera>().Length; initialListeners = All<AudioListener>().Length;
        hands = All<OVRHand>();
        volume = shell.appSettings.Volume; brightness = shell.appSettings.Brightness;
        var captions = shell.captionBridge.manager;
        sourceLanguage = captions.SourceIndex; targetLanguage = captions.TargetIndex; translated = captions.ShowTranslation;
        reconnect = shell.mapBridge.search.phoneTether.AllowAutomaticReconnect;
        mapParent = shell.mapBridge.Navigation.mapView.transform.parent;
        var ai = shell.assistantBridge.manager; var wake = ai.wakeWordManager;
        Require(wake != null && wake.appVoice != null, "Assistant voice references are missing.");
        assistantRoot = shell.assistantBridge.serviceRoot;
        Require(assistantRoot != null, "Assistant service group is missing.");
        assistantRootActive = assistantRoot.activeSelf; aiEnabled = ai.enabled; wakeEnabled = wake.enabled; voiceEnabled = wake.appVoice.enabled;
        actionOnly = wake.onlyActivateFromAction; keyboard = wake.useKeyboardShortCutActivation; autoListen = ai.activateVoiceAfterResponse;
        snapshots = true;
        QueueCamera(); QueueAssistant(); QueueCaptionsAndMaps(); QueueSettingsAndMusic();
        Add("Final lifecycle checks", () => shell.Hide(), () =>
        {
            Check(All<Camera>().Length == initialCameras && All<AudioListener>().Length == initialListeners && All<EventSystem>().Length == 1,
                "Original camera / AudioListener / EventSystem counts preserved");
            Check(!shell.cameraBridge.IsCapturing && shell.cameraBridge.PreviewTexture == null, "No camera capture or preview remains after closing display");
            Check(camera.gameObject.activeSelf == cameraInitiallyActive && camera.enabled == cameraInitiallyEnabled, "Raw camera activation state restored");
        }, .5);
    }

    private static void QueueCamera()
    {
        Add("Camera warmup", () => shell.OpenApp("Camera"), null, 10, () => shell.cameraBridge.CanCapture);
        Add("Camera and hand sampling", () => { sampleCamera = true; lastTimestamp = camera.Timestamp; }, () =>
        {
            sampleCamera = false;
            results.Add($"Camera metadata over 5 seconds: sampled frames={sampledFrames}; advancing camera frames={freshFrames}; valid preview frames={previewFrames}; resolution={width}x{height}; playing={camera.IsPlaying}; canCapture={shell.cameraBridge.CanCapture}.");
            results.Add($"Optical-hand metadata: tracked frames={trackedFrames}; valid-data frames={validHandFrames}; hand components={hands.Length}. This does not verify gestures.");
            Check(freshFrames > 1 && previewFrames > 1 && width > 16 && height > 16, "Camera sensor timestamps advance and live preview is available");
            var image = Picture("Sample viewfinder");
            Check(image != null && image.enabled && image.texture == shell.cameraBridge.PreviewTexture && image.texture != null, "Viewfinder binds the live camera texture");
            if (shell.cameraBridge.CanCapture)
            {
                Rect oldCrop = image.uvRect;
                Use("camera.zoom");
                Check(image.uvRect.width < oldCrop.width && image.uvRect.height < oldCrop.height, "Focused camera zoom changes the centered UV crop");
                if (SessionState.GetBool(State + "ownsPlay", false))
                {
                    Use("camera.capture"); captureRequested = true;
                }
                else results.Add("SKIP: session capture requires validator-owned Play Mode for guaranteed texture cleanup.");
            }
        }, 5);
        Add("Fresh photo completion", null, () =>
        {
            if (captureRequested) Check(shell.cameraBridge.CapturedCount == initialPhotos + 1, "Focused shutter captures one fresh in-memory photo");
            shell.OpenApp("Photos");
        }, 4, () => !shell.cameraBridge.IsCapturing);
        Add("Photos binding", null, () =>
        {
            if (captureRequested && shell.cameraBridge.CapturedCount > initialPhotos)
            {
                var photo = shell.cameraBridge.Photos[initialPhotos];
                var image = Picture("Current sample photo");
                Check(image != null && image.texture == photo && photo.width > 0 && photo.height > 0, "Photos displays the captured session texture");
                results.Add($"Photo metadata only: {photo.width}x{photo.height}; no pixels read into report or saved to disk.");
            }
            Check(shell.cameraBridge.PreviewTexture == null && !shell.cameraBridge.CanCapture, "Leaving Camera clears the live view and capture availability");
            Check(camera.gameObject.activeSelf == cameraInitiallyActive && camera.enabled == cameraInitiallyEnabled, "Camera closes its raw sensor lease");
            shell.Hide();
        }, .5);
    }

    private static void QueueAssistant()
    {
        Add("Assistant prepare", () => { micBefore = RecordingMicrophones(); shell.OpenApp("Assistant"); }, () =>
        {
            var bridge = shell.assistantBridge; var ai = bridge.manager; var wake = ai.wakeWordManager;
            micPrepared = RecordingMicrophones();
            results.Add($"Assistant idle: initialized={ai.IsInitialized}; wakeInitialized={wake.IsInitialized}; configured={ai.IsConfigured}; canListen={bridge.CanListen}; listening={bridge.IsListening}; busy={bridge.IsBusy}; activeVoiceRequest={wake.appVoice.IsRequestActive}; physical Unity microphones recording={micPrepared} (before={micBefore}).");
            Check(bridge.IsAcquired && ai.IsInitialized && wake.IsInitialized, "Actual Assistant services initialize while the legacy page stays hidden");
            Check(!bridge.IsListening && !bridge.IsBusy && !wake.IsVoiceActivated && !wake.appVoice.IsRequestActive && !ai.IsResponding,
                "Prepared Assistant starts no recognition or answer request");
            Check(wake.onlyActivateFromAction && !wake.useKeyboardShortCutActivation && !ai.activateVoiceAfterResponse,
                "Assistant lease requires an explicit Listen action and disables automatic relistening");
            shell.Hide();
        }, 3);
        Add("Assistant release", null, () =>
        {
            var bridge = shell.assistantBridge; var ai = bridge.manager; var wake = ai.wakeWordManager;
            micClosed = RecordingMicrophones();
            results.Add($"Physical microphone recording count after Assistant close={micClosed} (baseline={micBefore}).");
            Check(micClosed == micBefore, "Assistant close releases physical microphone capture to its baseline");
            Check(!bridge.IsAcquired && !ai.IsResponding && !wake.IsVoiceActivated && !wake.appVoice.IsRequestActive, "Assistant close cancels requests and releases the adapter");
            Check(assistantRoot.activeSelf == assistantRootActive && ai.enabled == aiEnabled && wake.enabled == wakeEnabled && wake.appVoice.enabled == voiceEnabled &&
                wake.onlyActivateFromAction == actionOnly && wake.useKeyboardShortCutActivation == keyboard && ai.activateVoiceAfterResponse == autoListen,
                "Assistant legacy activation and behavior flags restore");
        }, 1);
    }

    private static void QueueCaptionsAndMaps()
    {
        Add("Captions idle", () => shell.OpenApp("Captions"), () =>
        {
            var caption = shell.captionBridge.manager;
            results.Add($"Caption readiness: configured={caption.IsConfigured}; listening={caption.IsListening}; busy={caption.IsBusy}.");
            Check(shell.captionBridge.IsAcquired && !caption.IsListening && !caption.IsBusy, "Captions opens idle without starting recognition");
            Use("comm:captions-source");
            Check(caption.SourceIndex == (sourceLanguage + 1) % 5 && caption.TargetIndex == targetLanguage, "Caption source language changes independently");
            Use("comm:captions-target");
            Check(caption.TargetIndex == (targetLanguage + 1) % 5, "Caption target language changes");
            caption.SetLanguages(sourceLanguage, targetLanguage); caption.SetTranslationView(translated);
            shell.Hide();
        }, 1);
        Add("Maps idle", () => shell.OpenApp("Maps"), () =>
        {
            var bridge = shell.mapBridge; var phone = bridge.search.phoneTether;
            Check(bridge.IsAcquired && !phone.AllowAutomaticReconnect && !phone.isSocketOpen && phone.State == PhoneTether.ConnectionState.Idle,
                "Maps opens without connecting a relay or starting GPS streaming");
            Check(!bridge.IsListening && !bridge.Navigation.IsLoading && !bridge.Navigation.provider.IsRequesting, "Maps starts no dictation or route request");
            shell.Hide();
        }, 1);
        Add("Map and caption cleanup", null, () =>
        {
            Check(!shell.captionBridge.IsAcquired && !shell.captionBridge.manager.IsListening, "Caption adapter releases with microphone stopped");
            Check(!shell.mapBridge.IsAcquired && shell.mapBridge.Navigation.mapView.transform.parent == mapParent &&
                shell.mapBridge.search.phoneTether.AllowAutomaticReconnect == reconnect, "Maps restores its legacy renderer parent and reconnect policy");
        }, .5);
    }

    private static void QueueSettingsAndMusic()
    {
        Add("Settings", () => shell.OpenApp("Brightness"), () =>
        {
            shell.appSettings.SetBrightness(.6f);
            Use("brightness.more");
            Check(Mathf.Abs(shell.appSettings.Brightness - .7f) < .005f, "Focused brightness control changes app UI brightness");
            shell.appSettings.SetBrightness(brightness);
            Check(Mathf.Abs(shell.appSettings.Brightness - brightness) < .005f, "UI brightness restores to prior value");
            shell.appSettings.SetVolume(Mathf.Min(volume, .2f));
            shell.OpenApp("Music");
        }, .4);
        Add("Music start", null, () =>
        {
            if (shell.musicBridge == null || !shell.musicBridge.IsAvailable) { results.Add("SKIP: local music is unavailable."); return; }
            Check(!shell.musicBridge.IsPlaying, "Music opens without autoplay");
            Use("music.toggle"); playbackTime = shell.musicBridge.Elapsed;
        }, .5);
        Add("Music progress", null, () =>
        {
            if (shell.musicBridge == null || !shell.musicBridge.IsAvailable) return;
            Check(shell.musicBridge.IsPlaying && shell.musicBridge.Elapsed > playbackTime + .1f, "Local music playback time advances");
            Use("music.toggle"); pausedTime = shell.musicBridge.Elapsed;
        }, 1.5);
        Add("Music pause", null, () =>
        {
            if (shell.musicBridge == null || !shell.musicBridge.IsAvailable) return;
            Check(!shell.musicBridge.IsPlaying && Mathf.Abs(shell.musicBridge.Elapsed - pausedTime) < .1f, "Paused music preserves playback position");
            Use("music.toggle");
        }, .7);
        Add("Music resume", null, () =>
        {
            if (shell.musicBridge != null && shell.musicBridge.IsAvailable)
            {
                Check(shell.musicBridge.IsPlaying && shell.musicBridge.Elapsed > pausedTime + .1f, "Music resumes from its paused position");
                shell.musicBridge.Release();
            }
            shell.appSettings.SetVolume(volume);
            results.Add("Music was capped to min(previous app volume, 20%); playback metadata does not establish perceived headset audio quality.");
        }, 1);
    }

    private static T[] All<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    private static RawImage Picture(string name) => shell.FeatureScreens.GetComponentsInChildren<RawImage>().FirstOrDefault(v => v.name == name && v.gameObject.activeInHierarchy);
    private static void Use(string action)
    {
        var tile = shell.FeatureScreens.Controls.First(t => t != null && t.ActionId == action && t.gameObject.activeInHierarchy);
        Require(tile.IsInteractable(), "Focused control unavailable: " + action);
        EventSystem.current.SetSelectedGameObject(tile.gameObject);
        navigator.ClickSelected();
    }
    private static int RecordingMicrophones() => Microphone.devices.Count(device => Microphone.IsRecording(device));
    private static void Check(bool condition, string description)
    {
        results.Add((condition ? "PASS: " : "FAIL: ") + description);
        if (!condition) failures++;
    }
    private static void Require(bool condition, string description) { if (!condition) throw new ValidationFailure(description); }
    private static void Add(string name, Action start, Action end, double seconds, Func<bool> ready = null) =>
        steps.Enqueue(new Step { Name = name, Start = start, End = end, Seconds = seconds, Ready = ready });

    private static void Tick()
    {
        try
        {
            if (!EditorApplication.isPlaying) { Finish("Play Mode ended"); return; }
            if (EditorApplication.timeSinceStartup - startTime > 100) { Finish("Global 100-second test timeout"); return; }
            if (sampleCamera && Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount; sampledFrames++;
                if (camera.IsPlaying && camera.IsUpdatedThisFrame && camera.Timestamp != lastTimestamp)
                { freshFrames++; lastTimestamp = camera.Timestamp; }
                var preview = shell.cameraBridge.PreviewTexture;
                if (preview != null) { previewFrames++; width = preview.width; height = preview.height; }
                if (hands.Any(hand => hand != null && hand.IsTracked)) trackedFrames++;
                if (hands.Any(hand => hand != null && hand.IsDataValid)) validHandFrames++;
            }
            if (current == null)
            {
                if (steps.Count == 0) { Finish(null); return; }
                current = steps.Dequeue(); deadline = EditorApplication.timeSinceStartup + current.Seconds;
                results.Add("PHASE: " + current.Name);
                current.Start?.Invoke();
                return;
            }
            if (EditorApplication.timeSinceStartup < deadline && (current.Ready == null || !current.Ready())) return;
            current.End?.Invoke(); current = null;
        }
        catch (Exception error)
        {
            // Messages are only emitted for our own assertions; provider exception text is excluded.
            string detail = error is ValidationFailure ? error.Message : error.GetType().Name;
            Finish("Phase " + (current?.Name ?? "setup") + ": " + detail);
        }
    }

    private static void OnLog(string message, string trace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        // Store category counts only. Provider messages, credentials, speech and camera content never enter this report.
        string category = trace != null && (trace.Contains("PassthroughCamera") || trace.Contains("MRUK")) ? "Meta camera/MRUK" :
            trace != null && (trace.Contains("Meta.WitAi") || trace.Contains("Oculus.Voice")) ? "Meta Voice SDK" :
            trace != null && (trace.Contains("RayBan") || trace.Contains("AIManager") || trace.Contains("LiveTranslation")) ? "Application feature" : "Other Unity/SDK";
        string key = category + " " + type;
        diagnostics[key] = diagnostics.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static void Finish(string error)
    {
        if (finishing) return;
        finishing = true; sampleCamera = false;
        EditorApplication.update -= Tick;
        try
        {
            if (shell != null && snapshots)
            {
                shell.Hide(); shell.cameraBridge.SetCameraVisible(false);
                shell.assistantBridge.Release(); shell.captionBridge.Release(); shell.mapBridge.Release();
                if (shell.musicBridge != null) shell.musicBridge.Release();
                shell.captionBridge.manager.SetLanguages(sourceLanguage, targetLanguage);
                shell.captionBridge.manager.SetTranslationView(translated);
                shell.appSettings.SetVolume(volume); shell.appSettings.SetBrightness(brightness);
            }
        }
        catch (Exception cleanup) { results.Add("Cleanup exception type: " + cleanup.GetType().Name); failures++; }
        finally
        {
            RestorePreferences();
            AudioListener.volume = SessionState.GetFloat(State + "listener", 1f);
            Application.logMessageReceived -= OnLog;
            if (error != null) { results.Add("STOPPED: " + error); failures++; }
            foreach (var diagnostic in diagnostics) results.Add("DIAGNOSTIC: " + diagnostic.Key + " count=" + diagnostic.Value);
            results.Add("RESULT: " + (failures == 0 ? "PASS" : "NEEDS ATTENTION") + $"; passed={results.Count(v => v.StartsWith("PASS:"))}; failed={failures}; critical diagnostic entries={diagnostics.Values.Sum()}.");
            results.Add("Limits: no recognition, AI/TTS response, caption translation, dictation, relay Connect, GPS route or cloud image request was initiated by this test. SDK initialization/telemetry and local microphone prewarming can occur. One camera image was held in session memory only when fresh capture succeeded; no image/audio files or bytes were saved or uploaded by the validator. Physical gesture usability, perceived visual/audio quality and standalone Android remain separate tests.");
            results.Add("App preferences, saved phone room and caption language selections restored. Validator-owned Play Mode is stopped to dispose session camera textures.");
            Directory.CreateDirectory(Path.GetDirectoryName(Report));
            File.WriteAllText(Report, string.Join("\n", results) + "\n");
            SessionState.SetBool(State + "running", false);
            if (EditorApplication.isPlaying && SessionState.GetBool(State + "ownsPlay", false)) EditorApplication.isPlaying = false;
            else SessionState.SetBool(State + "pending", false);
            Debug.Log("[RayBanQuestLinkValidation] " + (failures == 0 ? "PASS" : "NEEDS ATTENTION") + "; metadata report: " + Report);
        }
    }
}
