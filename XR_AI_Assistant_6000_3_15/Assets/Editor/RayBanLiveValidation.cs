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

/// <summary>
/// Exercises real app settings and the existing music player in Editor Play Mode.
/// Camera is deliberately disconnected for its failure-path check; no camera
/// images are captured, saved or sent, and no AI/network requests are made.
/// </summary>
[InitializeOnLoad]
public static class RayBanLiveValidation
{
    private const string Prefix = "RayBanDisplay.LiveValidation.";
    private const string ReportPath = "Reports/2026-10-18-RayBanLiveControls/playmode-validation.txt";
    private static readonly List<string> checks = new List<string>();
    private static readonly Queue<Action> steps = new Queue<Action>();
    private static RayBanDisplayShell shell;
    private static UINavigator navigator;
    private static PassthroughCameraAccess originalCamera;
    private static bool cameraReferenceSaved;
    private static double nextStep;
    private static HashSet<int> originalSources;
    private static int originalListeners;
    private static int originalCameras;
    private static bool finishing;

    static RayBanLiveValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    [MenuItem("Tools/Ray-Ban Display/Validate live controls in Play Mode")]
    public static void Start()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play Mode before running live-control validation.");
        if (SessionState.GetBool(Prefix + "pending", false)) RestorePreferences();
        SnapshotPreference(RayBanAppSettings.VolumeKey, "volume");
        SnapshotPreference(RayBanAppSettings.BrightnessKey, "brightness");
        SessionState.SetFloat(Prefix + "listenerVolume", AudioListener.volume);
        SessionState.SetBool(Prefix + "pending", true);
        EditorApplication.isPlaying = true;
    }

    private static void SnapshotPreference(string key, string suffix)
    {
        SessionState.SetBool(Prefix + suffix + ".exists", PlayerPrefs.HasKey(key));
        SessionState.SetFloat(Prefix + suffix + ".value", PlayerPrefs.GetFloat(key, 1f));
    }

    private static void RestorePreference(string key, string suffix)
    {
        if (SessionState.GetBool(Prefix + suffix + ".exists", false))
            PlayerPrefs.SetFloat(key, SessionState.GetFloat(Prefix + suffix + ".value", 1f));
        else PlayerPrefs.DeleteKey(key);
    }

    private static void RestorePreferences()
    {
        RestorePreference(RayBanAppSettings.VolumeKey, "volume");
        RestorePreference(RayBanAppSettings.BrightnessKey, "brightness");
        PlayerPrefs.Save();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Prefix + "pending", false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            nextStep = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update -= Begin;
            EditorApplication.update += Begin;
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && !finishing)
            Finish(new InvalidOperationException("Validation was interrupted before completion."));
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            // SessionState survives domain reload: restore even if compilation,
            // manual Stop or another Editor exception interrupted the test.
            RestorePreferences();
            AudioListener.volume = SessionState.GetFloat(Prefix + "listenerVolume", 1f);
            SessionState.SetBool(Prefix + "pending", false);
        }
    }

    private static void Begin()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        EditorApplication.update -= Begin;
        checks.Clear();
        steps.Clear();
        finishing = false;
        cameraReferenceSaved = false;
        try
        {
            shell = UnityEngine.Object.FindFirstObjectByType<RayBanDisplayShell>();
            navigator = UnityEngine.Object.FindFirstObjectByType<UINavigator>();
            Check(shell != null && shell.UsesLiveFeatures, "Saved scene enables live controls in the new shell");
            Check(shell.appSettings != null && shell.appSettings.IsActive, "App settings activate with the shell");
            Check(shell.cameraBridge != null && shell.cameraBridge.pictureTaker != null, "Camera bridge reuses the existing session photo store");
            Check(shell.musicBridge != null && shell.musicBridge.player != null, "Music bridge is bound to the existing player");
            Check(navigator != null && EventSystem.current != null, "Original focused navigation is available");
            originalSources = All<AudioSource>().Select(s => s.GetInstanceID()).ToHashSet();
            originalListeners = All<AudioListener>().Length;
            originalCameras = All<Camera>().Length;
            Check(All<EventSystem>().Length == 1, "One EventSystem is retained");
            originalCamera = shell.cameraBridge.cameraAccess;
            cameraReferenceSaved = true;
            shell.cameraBridge.SetCameraVisible(false);
            shell.cameraBridge.cameraAccess = null;
            Check(shell.cameraBridge.CapturedCount == 0, "Fresh session album starts empty");
            QueueSettings();
            QueueCamera();
            QueueMusic();
            steps.Enqueue(() =>
            {
                Check(originalSources.SetEquals(All<AudioSource>().Select(s => s.GetInstanceID())), "Controls reuse original AudioSources without duplicates");
                Check(All<AudioListener>().Length == originalListeners && All<Camera>().Length == originalCameras, "No extra AudioListener or scene camera is introduced");
                Check(All<EventSystem>().Length == 1, "Navigation retains its single EventSystem");
                Check(shell.servicesToSuspend.All(s => s == null || !s.activeInHierarchy), "AI and raw camera services remain suspended in this offline test");
                Check(shell.cameraBridge.CapturedCount == 0, "Validation creates no photos");
            });
            nextStep = EditorApplication.timeSinceStartup + .25;
            EditorApplication.update += Tick;
        }
        catch (Exception exception) { Finish(exception); }
    }

    private static T[] All<T>() where T : UnityEngine.Object =>
        UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    private static RayBanFeatureScreen Features => shell.FeatureScreens;
    private static RayBanTile Control(string action) => Features.Controls.First(c => c != null && c.ActionId == action && c.gameObject.activeInHierarchy);
    private static TMP_Text Text(string name) => Features.GetComponentsInChildren<TMP_Text>().First(t => t.name == name && t.gameObject.activeInHierarchy);
    private static RawImage Picture(string name) => Features.GetComponentsInChildren<RawImage>().First(t => t.name == name && t.gameObject.activeInHierarchy);
    private static void Use(string action)
    {
        EventSystem.current.SetSelectedGameObject(Control(action).gameObject);
        navigator.ClickSelected();
    }
    private static bool Near(float first, float second, float tolerance = .002f) => Mathf.Abs(first - second) <= tolerance;

    private static void QueueSettings()
    {
        Graphic probe = null;
        Color authored = default;
        Color undimmed = default;
        steps.Enqueue(() =>
        {
            shell.appSettings.SetVolume(.4f);
            shell.appSettings.SetBrightness(.7f);
            Check(Near(AudioListener.volume, .4f), "Volume changes actual app-wide Unity audio gain");
            shell.OpenApp("Volume");
        });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "volume" && Text("Level").text == "40%", "Volume opens its real saved adjustment");
            Use("volume.more");
            Check(Near(shell.appSettings.Volume, .5f) && Near(AudioListener.volume, .5f), "Focused Volume Increase changes real audio gain");
            Check(Near(shell.appSettings.Brightness, .7f), "Volume adjustment preserves separate brightness value");
            Use("volume.less");
            Check(Near(shell.appSettings.Volume, .4f), "Focused Volume Decrease works");
            shell.OpenApp("Brightness");
        });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "brightness" && Text("Level").text == "70%", "Brightness has an independent adjustment screen");
            Use("brightness.less");
            Check(Near(shell.appSettings.Brightness, .6f) && Near(AudioListener.volume, .4f), "Brightness changes independently of audio gain");
            Use("brightness.more");
            Check(Near(shell.appSettings.Brightness, .7f), "Focused Brightness Increase works");
            probe = Text("Level");
            authored = probe.color;
            shell.appSettings.SetBrightness(1f);
            Canvas.ForceUpdateCanvases();
            undimmed = probe.canvasRenderer.GetColor();
        });
        steps.Enqueue(() => { shell.appSettings.SetBrightness(.5f); Canvas.ForceUpdateCanvases(); });
        steps.Enqueue(() =>
        {
            Color dimmed = probe.canvasRenderer.GetColor();
            Check(Near(dimmed.r, undimmed.r * .5f) && Near(dimmed.g, undimmed.g * .5f) && Near(dimmed.b, undimmed.b * .5f), "Brightness changes rendered UI RGB");
            Check(Near(dimmed.a, undimmed.a) && probe.color == authored, "Brightness preserves alpha and authored Graphic color");
            shell.appSettings.SetVolume(.3f);
            shell.appSettings.SetBrightness(.8f);
            Check(Near(PlayerPrefs.GetFloat(RayBanAppSettings.VolumeKey), .3f) && Near(PlayerPrefs.GetFloat(RayBanAppSettings.BrightnessKey), .8f), "Both settings persist under separate preference keys");
            shell.appSettings.Deactivate();
            shell.appSettings.Activate(shell.VisualRoot);
            Check(Near(shell.appSettings.Volume, .3f) && Near(shell.appSettings.Brightness, .8f) && Near(AudioListener.volume, .3f), "Reactivation reloads and applies saved preferences");
            shell.appSettings.SetVolume(-10);
            shell.appSettings.SetBrightness(-10);
            Check(Near(shell.appSettings.Volume, 0) && Near(shell.appSettings.Brightness, RayBanAppSettings.MinimumBrightness), "Lower bounds allow mute while keeping UI readable");
            shell.appSettings.SetVolume(10);
            shell.appSettings.SetBrightness(10);
            Check(Near(shell.appSettings.Volume, 1) && Near(shell.appSettings.Brightness, 1), "Upper bounds cap both values at 100 percent");
            shell.appSettings.SetVolume(float.NaN);
            shell.appSettings.SetBrightness(float.PositiveInfinity);
            Check(float.IsFinite(AudioListener.volume) && float.IsFinite(shell.appSettings.Brightness), "Invalid numeric input cannot poison audio or renderer state");
            shell.OpenApp("Settings");
        });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "settings", "Settings summary opens");
            Use("settings.volume");
            Check(Features.CurrentView == "volume", "Settings summary links to Volume");
            Use("back");
            Check(Features.CurrentView == "settings", "Adjustment Back returns to Settings");
            Use("settings.brightness");
            Check(Features.CurrentView == "brightness", "Settings summary links to Brightness");
            Use("back");
            Use("back");
            Check(shell.CurrentDetail == "", "Settings Back returns to launcher");
            Check(RayBanAppSettings.BatteryLabel == "Quest battery · on headset", "Editor battery does not pretend the PC battery is the Quest");
            Check(RayBanAppSettings.FormatBattery(-1f, BatteryStatus.Unknown).Contains("unavailable"), "Unknown battery reading has explicit unavailable state");
            Check(RayBanAppSettings.FormatBattery(.73f, BatteryStatus.Charging).Contains("73%") && RayBanAppSettings.FormatBattery(.73f, BatteryStatus.Charging).Contains("charging"), "Device battery formatter shows the provided level and charging state");
            // Test playback silently; all preferences and the original global
            // volume are restored on normal completion, failure or manual Stop.
            shell.appSettings.SetVolume(0f);
        });
    }

    private static void QueueCamera()
    {
        steps.Enqueue(() => { shell.OpenApp("Camera"); });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "camera", "Camera opens its integrated screen");
            Check(!shell.cameraBridge.CanCapture && shell.cameraBridge.PreviewTexture == null, "Disconnected camera exposes no fresh frame");
            Check(Picture("Sample viewfinder").texture == null, "Live Camera never displays the bundled sample image as a feed");
            Check(!Text("Capture status").text.Contains("Sample"), "Camera shows real availability feedback");
            Check(!Control("camera.capture").IsInteractable(), "Unavailable camera disables the shutter");
            Use("camera.capture");
            shell.cameraBridge.Capture(new Rect(0, 0, 1, 1));
            Check(shell.cameraBridge.CapturedCount == 0 && !shell.cameraBridge.IsCapturing, "Capture without a camera does not manufacture a photo");
        });
        steps.Enqueue(() =>
        {
            Check(Text("Capture status").text.Contains("No fresh") || Text("Capture status").text.Contains("not connected"), "Unavailable capture is explained to the user");
            Use("camera.photos");
        });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "photos", "Camera Photos control opens the actual session album");
            Check(Picture("Current sample photo").texture == null && shell.cameraBridge.Photos.Count == 0, "Empty live gallery contains no sample photos");
            Check(!Control("photos.next").IsInteractable() && !Control("photos.previous").IsInteractable(), "Empty gallery disables unavailable photo navigation");
            Use("photos.next");
            Use("photos.previous");
            Check(shell.cameraBridge.Photos.Count == 0, "Empty gallery navigation is safe");
            Use("back");
            Check(shell.CurrentDetail == "", "Gallery returns to launcher");
            Check(shell.cameraBridge.PreviewTexture == null && !shell.cameraBridge.CanCapture, "Leaving Camera clears camera availability");
        });
    }

    private static void QueueMusic()
    {
        AudioSource source = null;
        AudioClip firstClip = null;
        int firstIndex = -1;
        float pausedAt = 0;
        steps.Enqueue(() =>
        {
            source = shell.musicBridge.player.audioSource;
            Check(source != null && originalSources.Contains(source.GetInstanceID()), "Music uses the existing AudioSource");
            Check(shell.musicBridge.player.playlist.Count == 3 && shell.musicBridge.player.playlist.All(t => t != null && t.clip != null), "All three existing local music clips are assigned");
            shell.OpenApp("Music");
        });
        steps.Enqueue(() =>
        {
            Check(Features.CurrentView == "music" && shell.musicBridge.IsAcquired, "Music screen acquires the original player");
            Check(!shell.musicBridge.IsPlaying && !source.isPlaying, "Opening Music does not autoplay");
            Check(source == shell.musicBridge.player.audioSource && shell.musicBridge.Duration > 0, "Original source loads a real clip and duration");
            Check(Text("Track title").text == shell.musicBridge.CurrentTrack.title, "Music shows actual playlist metadata");
            Check(!Text("Playback preview status").text.Contains("no audio"), "Music no longer claims simulated playback");
            firstClip = source.clip;
            firstIndex = shell.musicBridge.CurrentIndex;
            Use("music.toggle");
        });
        steps.Enqueue(() =>
        {
            Check(source.isPlaying && shell.musicBridge.IsPlaying, "Play starts the actual AudioSource");
            Check(shell.musicBridge.Elapsed > 0, "Real clip playback advances");
            Use("music.toggle");
            pausedAt = shell.musicBridge.Elapsed;
            Check(!source.isPlaying, "Pause stops audio advancement");
        });
        steps.Enqueue(() =>
        {
            Check(Near(shell.musicBridge.Elapsed, pausedAt, .05f), "Paused track retains its position");
            Use("music.toggle");
        });
        steps.Enqueue(() =>
        {
            Check(source.isPlaying && shell.musicBridge.Elapsed > pausedAt, "Resume continues the same clip beyond its paused position");
            Use("music.next");
        });
        steps.Enqueue(() =>
        {
            Check(source.clip != firstClip && shell.musicBridge.CurrentIndex != firstIndex && source.isPlaying, "Next plays another real local clip");
            Use("music.previous");
        });
        steps.Enqueue(() =>
        {
            Check(source.clip == firstClip && shell.musicBridge.CurrentIndex == firstIndex && source.isPlaying, "Previous returns to the original real clip");
            Use("back");
        });
        steps.Enqueue(() =>
        {
            Check(shell.CurrentDetail == "" && source.isPlaying, "Returning to launcher preserves music playback");
            shell.SetPage(0);
            var close = shell.GetComponentsInChildren<RayBanTile>().First(t => t.ActionId == "hide");
            EventSystem.current.SetSelectedGameObject(close.gameObject);
            navigator.ClickSelected();
        });
        steps.Enqueue(() =>
        {
            Check(!shell.IsVisible && source.isPlaying, "Close display hides the UI while music keeps playing");
            shell.ShowLauncher();
            shell.OpenApp("Music");
        });
        steps.Enqueue(() =>
        {
            Check(source.isPlaying && source.clip == firstClip, "Reopening Music keeps the same playback session");
            Use("music.toggle");
            Check(!source.isPlaying, "Playback can be paused again after reopening");
            shell.musicBridge.Release();
            Check(!shell.musicBridge.IsAcquired && !source.isPlaying, "Explicit bridge release stops and restores the legacy player");
        });
    }

    private static void Tick()
    {
        if (EditorApplication.timeSinceStartup < nextStep) return;
        try
        {
            if (steps.Count == 0) { Finish(null); return; }
            steps.Dequeue()();
            nextStep = EditorApplication.timeSinceStartup + .3;
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
        EditorApplication.update -= Begin;
        EditorApplication.update -= Tick;
        try
        {
            if (shell != null)
            {
                if (shell.cameraBridge != null)
                {
                    shell.cameraBridge.SetCameraVisible(false);
                    if (cameraReferenceSaved) shell.cameraBridge.cameraAccess = originalCamera;
                }
                if (shell.musicBridge != null) shell.musicBridge.Release();
                if (shell.appSettings != null) shell.appSettings.Deactivate();
            }
        }
        catch (Exception cleanupException)
        {
            checks.Add("CLEANUP: " + cleanupException);
            if (failure == null) failure = cleanupException;
        }
        finally
        {
            RestorePreferences();
            AudioListener.volume = SessionState.GetFloat(Prefix + "listenerVolume", 1f);
        }
        int count = checks.Count(c => c.StartsWith("PASS:"));
        string report = "Ray-Ban live controls / Unity Editor Play Mode\n" + DateTime.Now.ToString("O") + "\n" +
            string.Join("\n", checks) + "\n" + (failure == null ? "RESULT: PASS (" + count + " checks)" : "RESULT: FAIL: " + failure) +
            "\nPreferences and app audio gain restored. Playback tests were muted.\n" +
            "Camera was deliberately disconnected: only availability and no-fake-photo behavior were tested.\n" +
            "Fresh camera pixels, physical gestures, Quest battery, perceived audio and Android behavior require device validation.\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, report);
            Debug.Log("[RayBanLiveValidation] " + (failure == null ? "PASS " + count : "FAIL " + failure.Message));
        }
        finally
        {
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
    }
}
