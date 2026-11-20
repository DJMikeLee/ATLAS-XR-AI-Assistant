using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Meta.XR;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Exercises the real scene gesture events and media adapters without playback,
/// recognition, screenshots or saved camera pixels. Physical gestures are separate tests.
/// </summary>
[InitializeOnLoad]
public static class RayBanMediaTwistValidation
{
    private const string State = "RayBanDisplay.MediaTwistValidation.";
    private const string Report = "Reports/2026-10-18-MediaTwist/validation.txt";
    private static readonly List<string> results = new List<string>();
    private static readonly Queue<Action> steps = new Queue<Action>();
    private static RayBanDisplayShell shell;
    private static PinchAndTwistEventSource gesture;
    private static UINavigator navigator;
    private static PassthroughCameraAccess camera;
    private static AudioSource musicSource;
    private static Slider musicSlider;
    private static bool finishing, snapshots, sourceEnabled, cameraActive, cameraEnabled;
    private static float originalMusic, originalSlider, listenerDuringRun, cameraBeforeUnavailable;
    private static int photoCount, audioCount, cameraCount, listenerCount;
    private static double nextStep, cameraDeadline;
    private static RayBanFeatureScreen Features => shell.FeatureScreens;
    private static RayBanMusicBridge Music => shell.musicBridge;

    static RayBanMediaTwistValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        if (SessionState.GetBool(State + "running", false)) EditorApplication.delayCall += RecoverInterrupted;
    }

    [MenuItem("Tools/Ray-Ban Display/Validate media twist events")]
    public static void Start()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Run this validation from Edit Mode after compilation finishes.");
        if (SessionState.GetBool(State + "pending", false))
            throw new InvalidOperationException("A media twist validation is already pending.");
        SavePreference(RayBanAppSettings.VolumeKey, "volume");
        SavePreference(RayBanAppSettings.BrightnessKey, "brightness");
        SessionState.SetFloat(State + "listener", AudioListener.volume);
        SessionState.SetBool(State + "pending", true);
        EditorApplication.isPlaying = true;
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
        PlayerPrefs.Save();
        AudioListener.volume = SessionState.GetFloat(State + "listener", 1f);
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(State + "pending", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            nextStep = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update += Begin;
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && !finishing)
            Finish(new InvalidOperationException("Validation interrupted before completion."));
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestorePreferences();
            SessionState.SetBool(State + "pending", false);
            SessionState.SetBool(State + "running", false);
        }
    }

    private static void RecoverInterrupted()
    {
        if (!SessionState.GetBool(State + "running", false) || shell != null) return;
        RestorePreferences();
        Directory.CreateDirectory(Path.GetDirectoryName(Report));
        File.AppendAllText(Report, "\nINTERRUPTED: assembly reload; settings restored.\n");
        SessionState.SetBool(State + "running", false);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        else SessionState.SetBool(State + "pending", false);
    }

    private static T[] All<T>() where T : UnityEngine.Object => UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    private static TMP_Text Text(string name) => Features.GetComponentsInChildren<TMP_Text>().First(t => t.name == name);
    private static RawImage Viewfinder => Features.GetComponentsInChildren<RawImage>().First(t => t.name == "Sample viewfinder");
    private static float Zoom => float.Parse(Text("Zoom value").text.TrimEnd('x'), CultureInfo.InvariantCulture);
    private static void StartTwist() => gesture.OnStartPinchAndTwist.Invoke();
    private static void Twist(float value) => gesture.OnPinchAndTwist.Invoke(value);
    private static void EndTwist() => gesture.OnEndPinchAndTwist.Invoke();
    private static void Use(string action)
    {
        var tile = Features.Controls.First(t => t && t.ActionId == action && t.gameObject.activeInHierarchy);
        Check(tile.IsInteractable(), action + " is available through the normal focused button path");
        EventSystem.current.SetSelectedGameObject(tile.gameObject);
        navigator.ClickSelected();
    }
    private static bool Near(float value, float expected) => Mathf.Abs(value - expected) < .002f;
    private static void Volume(float expected, string label)
    {
        Check(Near(Music.Volume, expected) && Near(musicSource.volume, expected), label);
        Check(musicSlider == null || Near(musicSlider.value, expected), "Hidden legacy slider agrees with music gain");
        Check(!Music.IsPlaying && !musicSource.isPlaying, "Volume adjustment never starts music playback");
        Check(Near(AudioListener.volume, listenerDuringRun), "Music adjustment preserves global app gain");
    }

    private static void Begin()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        EditorApplication.update -= Begin;
        results.Clear(); steps.Clear(); finishing = snapshots = false;
        SessionState.SetBool(State + "running", true);
        try
        {
            shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
            navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
            Check(shell && shell.UsesLiveFeatures && navigator, "Existing live shell and focused navigator are present");
            var sources = All<PinchAndTwistEventSource>().Where(s => s.gameObject.scene == shell.gameObject.scene).ToArray();
            Check(sources.Length == 1 && sources[0].isActiveAndEnabled, "Exactly one existing active optical twist source is reused");
            gesture = sources[0]; sourceEnabled = gesture.enabled;
            Check(Music && Music.IsAvailable && shell.cameraBridge, "Original music and camera adapters are wired");
            musicSource = Music.player.audioSource != null ? Music.player.audioSource : Music.player.GetComponent<AudioSource>();
            musicSlider = Music.player.volumeSlider;
            Check(!musicSource.isPlaying, "Scene begins without active music playback");
            originalMusic = musicSource.volume; originalSlider = musicSlider != null ? musicSlider.value : 0;
            listenerDuringRun = AudioListener.volume;
            camera = shell.cameraBridge.cameraAccess;
            if (camera != null) { cameraActive = camera.gameObject.activeSelf; cameraEnabled = camera.enabled; }
            photoCount = shell.cameraBridge.CapturedCount;
            audioCount = All<AudioSource>().Length; cameraCount = All<Camera>().Length; listenerCount = All<AudioListener>().Length;
            snapshots = true;
            results.Add("CONTEXT: " + Application.unityVersion + "; " + Application.platform + "; HMD present=" + OVRManager.isHmdPresent);
            QueueMusic();
            steps.Enqueue(() => { shell.OpenApp("Camera"); cameraDeadline = EditorApplication.timeSinceStartup + 12; });
            steps.Enqueue(InspectCameraWhenReady);
            nextStep = EditorApplication.timeSinceStartup + .25;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void QueueMusic()
    {
        steps.Enqueue(() => shell.OpenApp("Music"));
        steps.Enqueue(() =>
        {
            Check(Music.IsAcquired && !Music.IsPlaying, "Music opens without autoplay");
            Check(Music.player.GetComponentsInChildren<PinchAndTwistCustomEvent>(true).All(c => !c.isActiveAndEnabled), "Legacy music twist forwarding remains inactive");
            Check(Music.player.GetComponentsInChildren<UpdateSliderOnPinchAndTwist>(true).All(c => !c.isActiveAndEnabled), "Legacy music slider gesture handlers remain inactive");
            Music.SetVolume(.4f);
            Twist(.2f); Volume(.4f, "An update without a gesture start cannot alter music");
            StartTwist(); Check(Features.IsAdjustingMediaWithTwist, "Active adjustment suppresses incidental optical navigation");
            Twist(.2f); Volume(.6f, "Positive optical event increases music gain");
            Twist(.2f); Volume(.6f, "Repeated absolute event does not compound gain");
            Twist(.25f); Volume(.65f, "Successive absolute events use incremental change");
            Check(Text("Playback preview status").text.Contains("65%"), "Music status shows the updated percentage");
            Twist(1f); Volume(1f, "Music gain clamps at full volume");
            Twist(.8f); Volume(.8f, "Reverse twist immediately moves away from the upper clamp");
            Twist(-1f); Volume(0f, "Music gain clamps at silence");
            Twist(-.8f); Volume(.2f, "Reverse twist immediately moves away from silence");
            EndTwist(); Twist(.5f); Volume(.2f, "A completed gesture cannot keep changing gain");
            StartTwist(); Twist(float.NaN); Twist(.5f); Volume(.2f, "Invalid samples cancel adjustment without poisoning gain");
            StartTwist(); Twist(float.PositiveInfinity); Volume(.2f, "Infinite samples leave gain finite"); EndTwist();
            for (int i = 0; i < 5; i++) shell.OpenApp("Music");
            Music.SetVolume(.4f); StartTwist(); Twist(.1f); Volume(.5f, "Repeated page openings do not duplicate event subscription");
            shell.OpenApp("Settings"); Twist(.3f); Volume(.5f, "Leaving Music cancels the held gesture");
            shell.OpenApp("Music"); Twist(.4f); Volume(.5f, "Returning to Music requires a fresh gesture start");
            StartTwist(); Twist(.1f); Volume(.6f, "A fresh gesture works after returning");
            shell.Hide(); Twist(.4f); Volume(.6f, "Hiding the display removes media event ownership");
            shell.OpenApp("Music"); Twist(.5f); Volume(.6f, "Reopening after hide cannot reuse the old pinch");
            StartTwist(); Features.enabled = false; Twist(.7f); Volume(.6f, "Disabling the feature screen cancels adjustment");
            Features.enabled = true;
        });
        steps.Enqueue(() =>
        {
            Music.SetVolume(.4f); Twist(.2f); Volume(.4f, "Re-enabled screen waits for a new gesture");
            StartTwist(); Twist(.1f); Volume(.5f, "Re-enabled screen binds exactly once");
            Features.SendMessage("OnApplicationPause", true, SendMessageOptions.RequireReceiver);
            Twist(.3f); Volume(.5f, "Application pause cancels a held adjustment");
            Features.SendMessage("OnApplicationPause", false, SendMessageOptions.RequireReceiver);
            Twist(.4f); Volume(.5f, "Resume does not resurrect the paused gesture");
            StartTwist(); Twist(.1f); Volume(.6f, "Fresh adjustment works after application resume");
            EndTwist();
        });
    }

    private static void InspectCameraWhenReady()
    {
        if (shell.cameraBridge.PreviewTexture == null && EditorApplication.timeSinceStartup < cameraDeadline)
        {
            steps.Enqueue(InspectCameraWhenReady);
            return;
        }
        if (shell.cameraBridge.PreviewTexture != null) TestLiveCamera();
        else results.Add("SKIP: fresh camera frame unavailable after 12 seconds; positive zoom/crop and zoom-button integration require a connected camera retest.");
        cameraBeforeUnavailable = Zoom;
        shell.cameraBridge.cameraAccess = null;
        steps.Enqueue(() =>
        {
            StartTwist(); Twist(.4f); EndTwist();
            Check(Near(Zoom, cameraBeforeUnavailable), "Unavailable camera cannot accept twist zoom");
            Check(!Viewfinder.enabled && Viewfinder.texture == null, "Unavailable camera shows no stale preview texture");
            shell.cameraBridge.cameraAccess = camera;
            shell.OpenApp("Music");
            Music.SetVolume(.4f);
            StartTwist(); shell.OpenApp("Camera"); Twist(.4f);
            Check(Near(Zoom, cameraBeforeUnavailable), "Held Music pinch is not retargeted to Camera");
            Volume(.4f, "Changing to Camera does not leak volume updates");
            shell.OpenApp("Photos"); StartTwist(); Twist(.5f); EndTwist();
            Volume(.4f, "Non-media detail screens ignore twist events");
            shell.ReturnToLauncher(); StartTwist(); Twist(.5f); EndTwist();
            Volume(.4f, "Launcher ignores media twist events");
            Check(All<AudioSource>().Length == audioCount && All<Camera>().Length == cameraCount && All<AudioListener>().Length == listenerCount,
                "Validation and integration add no cameras, audio sources or audio listeners");
            Check(shell.cameraBridge.CapturedCount == photoCount, "No photo was captured or saved during validation");
        });
    }

    private static void TestLiveCamera()
    {
        var frame = shell.cameraBridge.PreviewTexture;
        results.Add("CAMERA: fresh live texture " + frame.width + "x" + frame.height + "; no pixels copied or saved.");
        Check(shell.cameraBridge.pictureTaker.GetComponentsInChildren<PinchAndTwistCustomEvent>(true).All(c => !c.isActiveAndEnabled), "Legacy camera twist forwarding remains inactive");
        // Reset through the ordinary visible button, not private state injection.
        for (int i = 0; i < 3 && !Near(Zoom, 1f); i++) Use("camera.zoom");
        Check(Near(Zoom, 1f), "Camera starts the zoom test at 1x");
        Rect originalCrop = Viewfinder.uvRect;
        Twist(.25f); Check(Near(Zoom, 1f), "Camera ignores events without a gesture start");
        StartTwist(); Twist(.25f);
        Check(Near(Zoom, 1.5f), "Positive optical twist adjusts zoom continuously to 1.5x");
        Check(Near(Viewfinder.uvRect.width, originalCrop.width / 1.5f) && Near(Viewfinder.uvRect.height, originalCrop.height / 1.5f) &&
            Vector2.Distance(Viewfinder.uvRect.center, originalCrop.center) < .002f, "Zoom uses a centered live viewfinder crop");
        Twist(.25f); Check(Near(Zoom, 1.5f), "Repeated absolute camera event does not compound zoom");
        Twist(1f); Check(Near(Zoom, 3f), "Camera zoom clamps at 3x");
        Twist(.8f); Check(Near(Zoom, 2.6f), "Reversing from maximum zoom takes effect immediately");
        Twist(-1f); Check(Near(Zoom, 1f), "Camera zoom clamps at 1x");
        EndTwist(); Twist(.5f); Check(Near(Zoom, 1f), "Released gesture cannot keep changing camera zoom");
        Use("camera.zoom"); Check(Near(Zoom, 2f), "Zoom button still advances from 1x to 2x");
        Use("camera.zoom"); Check(Near(Zoom, 3f), "Zoom button still advances from 2x to 3x");
        Use("camera.zoom"); Check(Near(Zoom, 1f), "Zoom button still wraps from 3x to 1x");
        StartTwist(); shell.OpenApp("Music"); Music.SetVolume(.4f); Twist(.5f);
        Volume(.4f, "Held Camera pinch is not retargeted to Music");
        shell.OpenApp("Camera");
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        try
        {
            if (steps.Count == 0) { Finish(null); return; }
            steps.Dequeue()();
            nextStep = EditorApplication.timeSinceStartup + .25;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        results.Add("PASS: " + label);
    }

    private static void Finish(Exception failure)
    {
        if (finishing) return;
        finishing = true;
        EditorApplication.update -= Begin; EditorApplication.update -= Tick;
        try
        {
            if (shell != null && snapshots)
            {
                Features.enabled = true;
                Features.SendMessage("OnApplicationPause", false, SendMessageOptions.DontRequireReceiver);
                shell.cameraBridge.cameraAccess = camera;
                shell.Hide();
                Music.Release();
                shell.cameraBridge.SetCameraVisible(false);
                if (musicSource) musicSource.volume = originalMusic;
                if (musicSlider) musicSlider.SetValueWithoutNotify(originalSlider);
                if (gesture) gesture.enabled = sourceEnabled;
                if (camera) { camera.enabled = cameraEnabled; camera.gameObject.SetActive(cameraActive); }
            }
        }
        catch (Exception cleanup) { results.Add("CLEANUP: " + cleanup); if (failure == null) failure = cleanup; }
        finally { RestorePreferences(); }
        int count = results.Count(r => r.StartsWith("PASS:"));
        string report = "Ray-Ban media twist event integration / Unity Play Mode\n" + DateTimeOffset.Now.ToString("O") + "\n" +
            string.Join("\n", results) + "\n" + (failure == null ? "RESULT: PASS (" + count + " checks)" : "RESULT: FAIL: " + failure) +
            "\nTests invoke the existing optical source's UnityEvents; no physical hand motion was simulated or verified.\n" +
            "No music playback, microphone recognition, image capture, screenshot, image upload or network service action was requested.\n" +
            "Original audio gains, slider value, settings preferences and camera reference/state are restored.\n" +
            "Physical Quest gestures and a rebuilt standalone Android application remain separate validation.\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Report));
            File.WriteAllText(Report, report);
            Debug.Log("[RayBanMediaTwistValidation] " + (failure == null ? "PASS " + count : "FAIL " + failure.Message));
        }
        finally { if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; }
    }
}
