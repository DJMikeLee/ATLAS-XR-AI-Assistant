using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Meta.Voice;
using Meta.WitAi.Data;
using Meta.WitAi.Lib;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Quest Link microphone lifecycle checks. Captures metadata only and never submits speech.</summary>
[InitializeOnLoad]
public static class AssistantMicrophoneValidation
{
    private const string State = "RayBanDisplay.AssistantMicrophoneValidation.";
    private const string Report = "Reports/2026-10-18-AssistantMic/live-microphone-validation.txt";
    private const string RoomKey = "XR_AI_Assistant.PhoneTracker.LastSuccessfulRoom";
    private static readonly List<string> results = new List<string>();
    private static readonly Queue<Step> steps = new Queue<Step>();
    private static readonly Dictionary<string, int> diagnostics = new Dictionary<string, int>();
    private static RayBanDisplayShell shell;
    private static AudioBuffer buffer;
    private static Mic mic;
    private static GameObject recorderOwner;
    private static Step current;
    private static double deadline, startedAt;
    private static bool finishing, bridgeEnabled;
    private static int failures, baseline, initialPosition;

    private sealed class Step
    {
        public string Name;
        public Action Start, End;
        public Func<bool> Ready;
        public double Seconds;
    }

    private sealed class ValidationFailure : Exception
    {
        public ValidationFailure(string text) : base(text) { }
    }

    static AssistantMicrophoneValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        if (SessionState.GetBool(State + "running", false)) EditorApplication.delayCall += Recover;
    }

    [MenuItem("Tools/Ray-Ban Display/Validate Assistant microphone close")]
    public static void Start()
    {
        if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling || EditorApplication.isUpdating)
            throw new InvalidOperationException("Start from idle Edit Mode so the validator owns Play Mode and cleanup.");
        if (SceneManager.GetActiveScene().path != "Assets/Scenes/UI Quest Display.unity")
            throw new InvalidOperationException("Open the main UI Quest Display scene first.");
        if (SessionState.GetBool(State + "pending", false))
            throw new InvalidOperationException("A microphone validation is already pending.");
        SessionState.SetBool(State + "roomExists", PlayerPrefs.HasKey(RoomKey));
        SessionState.SetString(State + "room", PlayerPrefs.GetString(RoomKey, ""));
        SessionState.SetBool(State + "pending", true);
        SessionState.SetBool(State + "running", false);
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(State + "pending", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += Begin;
        else if (state == PlayModeStateChange.ExitingPlayMode && !finishing) Finish("Play Mode interrupted");
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestorePreferences();
            SessionState.SetBool(State + "pending", false);
            SessionState.SetBool(State + "running", false);
        }
    }

    private static void RestorePreferences()
    {
        if (SessionState.GetBool(State + "roomExists", false))
            PlayerPrefs.SetString(RoomKey, SessionState.GetString(State + "room", ""));
        else PlayerPrefs.DeleteKey(RoomKey);
        PlayerPrefs.Save();
    }

    private static void Recover()
    {
        if (!SessionState.GetBool(State + "running", false) || shell != null) return;
        RestorePreferences();
        Directory.CreateDirectory(Path.GetDirectoryName(Report));
        File.AppendAllText(Report, "\nINTERRUPTED: assembly reload; stopping validator-owned Play Mode.\n");
        SessionState.SetBool(State + "running", false);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else SessionState.SetBool(State + "pending", false);
    }

    private static void Begin()
    {
        if (!EditorApplication.isPlaying || !SessionState.GetBool(State + "pending", false)) return;
        results.Clear(); steps.Clear(); diagnostics.Clear(); current = null;
        shell = null; buffer = null; mic = null; recorderOwner = null;
        failures = 0; finishing = false; startedAt = EditorApplication.timeSinceStartup;
        SessionState.SetBool(State + "running", true);
        Application.logMessageReceived += OnLog;
        results.Add("Unity " + Application.unityVersion + "; Editor Play Mode / Quest Link; UTC " + DateTime.UtcNow.ToString("O"));
        results.Add("Scope: SDK-local microphone buffer lifecycle only. No AppVoice Activate, recognition, AI request, translation, dictation, camera readback, or audio/image file capture.");
        Add("Wait for live HMD", null, () =>
        {
            Require(OVRManager.isHmdPresent, "Live Link HMD not found within 20 seconds.");
            results.Add("HMD: " + OVRPlugin.GetSystemHeadsetType());
            Setup();
        }, 20, () => OVRManager.isHmdPresent);
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        SaveReport();
    }

    private static void Setup()
    {
        shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
        Require(shell != null && shell.UsesLiveFeatures && shell.assistantBridge != null && shell.assistantBridge.manager != null,
            "Live Assistant bridge is missing from the main scene.");
        Require(shell.captionBridge != null && shell.mapBridge != null, "Caption / Map bridge references are missing.");
        bridgeEnabled = shell.assistantBridge.enabled;
        Require(bridgeEnabled, "Assistant bridge must be enabled for this test.");
        shell.Hide();
        Add("Baseline", null, () =>
        {
            baseline = RecordingMicrophones();
            RecordState("Baseline");
            Require(baseline == 0, "A microphone is already recording before the Assistant test; other recording must remain undisturbed.");
            Check(true, "No physical microphone capture before opening Assistant");
        }, 1);
        OpenAssistant("Initial Assistant warmup", () =>
        {
            Check(RecordingMicrophones() > baseline, "Opening Assistant prewarms the installed SDK microphone");
            Check(NoRecognition(), "Assistant preparation starts no speech recognition or answer request");
        });
        Closed("Close warmed Assistant", () => shell.Hide());

        OpenAssistant("Reopen Assistant", () => Check(shell.assistantBridge.CanListen, "Reopening Assistant restores Listen readiness"));
        Add("Stop idle Assistant", () => shell.assistantBridge.Stop(), () =>
        {
            RecordState("Stop idle Assistant");
            Check(shell.assistantBridge.IsAcquired && shell.assistantBridge.CanListen && RecordingMicrophones() == baseline &&
                mic.ActivationState == VoiceAudioInputState.Off && buffer.AudioState == VoiceAudioInputState.Off,
                "Stop releases idle hardware while Assistant remains ready for another Listen action");
        }, 1);
        StartLocal("Reopened local SDK recording", () => shell.Hide());
        Closed("Close after local recording", () => { StopLocal(); shell.Hide(); });

        Closed("Rapid open then close", () => { shell.OpenApp("Assistant"); shell.Hide(); }, 2);
        OpenAssistant("Assistant before Captions", null);
        Closed("Assistant to idle Captions", () => shell.OpenApp("Captions"));
        Add("Captions stays idle", null, () =>
            Check(shell.captionBridge.IsAcquired && !shell.captionBridge.manager.IsListening && !shell.captionBridge.manager.IsBusy,
                "Captions remains idle without starting recognition"), .3);
        OpenAssistant("Assistant before Maps", null);
        Closed("Assistant to idle Maps", () => shell.OpenApp("Maps"), 2);
        Add("Maps stays idle", null, () => Check(NoRecognition(), "Maps transition starts no Assistant recognition"), .3);

        OpenAssistant("Assistant before bridge disable", null);
        Closed("Bridge disable cleanup", () => shell.assistantBridge.enabled = false);
        Add("Restore bridge enable", () => { shell.assistantBridge.enabled = bridgeEnabled; shell.Hide(); }, null, .2);

        OpenAssistant("Assistant with independent buffer recorder", null);
        StartLocal("Start independent SDK recorder", null);
        Add("Close preserves independent SDK recorder", () => shell.Hide(), () =>
        {
            RecordState("Shared owner after Assistant close");
            Check(!shell.assistantBridge.IsAcquired && LocalRecording() && RecordingMicrophones() > baseline,
                "Closing Assistant preserves another active AudioBuffer recording owner");
            Check(NoRecognition(), "Independent local recording does not create a voice recognition request");
        }, 1);
        Closed("Last independent recorder ends", StopLocal);

        StartLocal("Independent recorder starts while Assistant closed", null);
        Closed("Independent recorder releases again", StopLocal);
        Add("Final closed state", () => shell.Hide(), () =>
        {
            RecordState("Final");
            Check(RecordingMicrophones() == baseline && NoRecognition(), "Final state has zero physical capture and no active voice request");
        }, .5);
    }

    private static void OpenAssistant(string title, Action finished)
    {
        Add(title, () => shell.OpenApp("Assistant"), () =>
        {
            GetBuffer();
            Require(shell.assistantBridge.CanListen, "Assistant failed to initialize in " + title);
            RecordState(title);
            finished?.Invoke();
        }, 7, () => shell.assistantBridge.CanListen && ExistingMicOn());
    }

    private static bool ExistingMicOn()
    {
        var existing = UnityEngine.Object.FindFirstObjectByType<AudioBuffer>();
        return existing != null && existing.MicInput is Mic input && input.ActivationState == VoiceAudioInputState.On;
    }

    private static void GetBuffer()
    {
        buffer = UnityEngine.Object.FindFirstObjectByType<AudioBuffer>();
        Require(buffer != null && buffer.MicInput is Mic, "Existing SDK AudioBuffer / Unity Mic is unavailable.");
        mic = buffer.MicInput as Mic;
        // Read the Editor-exposed serialized flag, never enable PCM debug file output.
        var serialized = new SerializedObject(buffer);
        var debug = serialized.FindProperty("_debugOutput");
        Require(debug != null && !debug.boolValue, "SDK audio debug output must be disabled before local recording tests.");
        if (recorderOwner == null) recorderOwner = new GameObject("Assistant microphone validator local owner") { hideFlags = HideFlags.DontSave };
    }

    private static void StartLocal(string title, Action afterCheck)
    {
        Add(title, () =>
        {
            GetBuffer();
            Require(NoRecognition(), "Unexpected recognition activity before local-only recording test.");
            buffer.StartRecording(recorderOwner.transform);
        }, () =>
        {
            RecordState(title);
            Check(LocalRecording() && RecordingMicrophones() > baseline, title + " reaches live capture without a speech request");
            initialPosition = mic.ClipPosition;
        }, 7, LocalRecording);
        Add(title + " progression", null, () =>
        {
            Check(LocalRecording() && mic.ClipPosition != initialPosition, title + " advances physical microphone sample position");
            afterCheck?.Invoke();
        }, .5);
    }

    private static bool LocalRecording() => buffer != null && recorderOwner != null && mic != null &&
        buffer.IsRecording(recorderOwner.transform) && buffer.AudioState == VoiceAudioInputState.On &&
        mic.IsRecording && mic.ActivationState == VoiceAudioInputState.On;

    private static void StopLocal()
    {
        if (buffer != null && recorderOwner != null && buffer.IsRecording(recorderOwner.transform))
            buffer.StopRecording(recorderOwner.transform);
    }

    private static void Closed(string title, Action action, double seconds = 1)
    {
        Add(title, action, () =>
        {
            RecordState(title);
            Check(!shell.assistantBridge.IsAcquired && RecordingMicrophones() == baseline &&
                (buffer == null || buffer.AudioState == VoiceAudioInputState.Off) &&
                (mic == null || (!mic.IsRecording && mic.ActivationState == VoiceAudioInputState.Off)),
                title + " releases the physical microphone and SDK capture state");
        }, seconds);
    }

    private static bool NoRecognition()
    {
        if (shell == null || shell.assistantBridge == null) return false;
        var ai = shell.assistantBridge.manager;
        var wake = ai != null ? ai.wakeWordManager : null;
        return wake != null && wake.appVoice != null && !wake.appVoice.IsRequestActive && !wake.appVoice.MicActive &&
            !wake.IsVoiceActivated && !ai.IsResponding && !shell.assistantBridge.IsBusy;
    }

    private static int RecordingMicrophones() => Microphone.devices.Count(device => Microphone.IsRecording(device));
    private static void RecordState(string label) => results.Add($"STATE {label}: physical recording devices={RecordingMicrophones()}; " +
        $"AudioBuffer={(buffer != null ? buffer.AudioState.ToString() : "absent")}; Mic activation={(mic != null ? mic.ActivationState.ToString() : "absent")}; " +
        $"Mic recording={(mic != null && mic.IsRecording)}; Mic enabled={(mic != null && mic.enabled)}; local owner={(buffer != null && recorderOwner != null && buffer.IsRecording(recorderOwner.transform))}.");
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
            if (EditorApplication.timeSinceStartup - startedAt > 120) { Finish("Global 120-second timeout"); return; }
            if (current == null)
            {
                if (steps.Count == 0) { Finish(null); return; }
                current = steps.Dequeue(); deadline = EditorApplication.timeSinceStartup + current.Seconds;
                results.Add("PHASE: " + current.Name); current.Start?.Invoke(); SaveReport(); return;
            }
            if (EditorApplication.timeSinceStartup < deadline && (current.Ready == null || !current.Ready())) return;
            current.End?.Invoke(); current = null; SaveReport();
        }
        catch (Exception error)
        {
            Finish("Phase " + (current?.Name ?? "setup") + ": " + (error is ValidationFailure ? error.Message : error.GetType().Name));
        }
    }

    private static void OnLog(string message, string trace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        string category = trace != null && (trace.Contains("Meta.WitAi") || trace.Contains("Oculus.Voice")) ? "Meta Voice SDK" :
            trace != null && (trace.Contains("MRUK") || trace.Contains("PassthroughCamera")) ? "Meta MRUK/camera" :
            trace != null && (trace.Contains("RayBan") || trace.Contains("AIManager")) ? "Application" : "Other Unity/SDK";
        string key = category + " " + type;
        diagnostics[key] = diagnostics.TryGetValue(key, out int value) ? value + 1 : 1;
    }

    private static void SaveReport()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Report));
        File.WriteAllText(Report, string.Join("\n", results) + "\n");
    }

    private static void Finish(string error)
    {
        if (finishing) return;
        finishing = true; EditorApplication.update -= Tick;
        try
        {
            StopLocal();
            if (shell != null)
            {
                shell.Hide();
                if (shell.assistantBridge != null) shell.assistantBridge.enabled = bridgeEnabled;
            }
            if (recorderOwner != null) UnityEngine.Object.Destroy(recorderOwner);
        }
        catch (Exception cleanup) { results.Add("Cleanup exception type: " + cleanup.GetType().Name); failures++; }
        finally
        {
            Application.logMessageReceived -= OnLog;
            RestorePreferences();
            if (error != null) { results.Add("STOPPED: " + error); failures++; }
            foreach (var diagnostic in diagnostics) results.Add("DIAGNOSTIC: " + diagnostic.Key + " count=" + diagnostic.Value);
            results.Add("RESULT: " + (failures == 0 ? "PASS" : "NEEDS ATTENTION") + $"; passed={results.Count(line => line.StartsWith("PASS:"))}; failed={failures}.");
            results.Add("Cleanup: validator-owned Play Mode stopped; temporary recording owner disposed; saved room preference restored. No scene or SDK assets modified.");
            results.Add("Limits: microphone/device status and sample positions only, no audio contents saved or transmitted by the validator. Recognition, perceived speech quality and standalone Android require separate tests.");
            SaveReport();
            SessionState.SetBool(State + "running", false);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            else SessionState.SetBool(State + "pending", false);
            Debug.Log("[AssistantMicrophoneValidation] " + (failures == 0 ? "PASS" : "NEEDS ATTENTION") + "; metadata report: " + Report);
        }
    }
}
