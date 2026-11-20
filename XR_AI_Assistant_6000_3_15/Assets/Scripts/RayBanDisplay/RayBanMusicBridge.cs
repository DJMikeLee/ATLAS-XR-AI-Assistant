using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lets the new display use the original music player without showing its old
/// Canvas or activating its old gesture handlers. The AudioSource stays in place.
/// </summary>
[DisallowMultipleComponent]
public sealed class RayBanMusicBridge : MonoBehaviour
{
    public SimpleAudioPlayer player;

    private readonly Dictionary<GameObject, bool> childStates = new Dictionary<GameObject, bool>();
    private readonly Dictionary<Behaviour, bool> componentStates = new Dictionary<Behaviour, bool>();
    private bool originalActive;
    private bool acquired;
    private bool quitting;

    public bool IsAcquired => acquired;
    public MusicTrack CurrentTrack => player != null ? player.CurrentTrack : null;
    public int CurrentIndex => player != null ? player.CurrentIndex : -1;
    public float Elapsed => player != null ? player.Elapsed : 0f;
    public float Duration => player != null ? player.Duration : 0f;
    public bool IsPlaying => acquired && player != null && player.IsPlaying;
    public float Volume => player != null && player.audioSource != null ? player.audioSource.volume : 0f;

    /// <summary>Music-only gain, matching the original slider; app/system volume is separate.</summary>
    public void SetVolume(float value)
    {
        if (!acquired || !IsAvailable || float.IsNaN(value) || float.IsInfinity(value)) return;
        value = Mathf.Clamp01(value);
        player.audioSource.volume = value;
        if (player.volumeSlider != null) player.volumeSlider.SetValueWithoutNotify(value);
    }

    public bool IsAvailable
    {
        get
        {
            if (player == null || player.playlist == null) return false;
            var source = player.audioSource != null ? player.audioSource : player.GetComponent<AudioSource>();
            // The current project keeps both components on the legacy page root.
            // Do not silently add a duplicate source for another configuration.
            if (source == null || source.gameObject != player.gameObject) return false;
            foreach (var track in player.playlist)
                if (track != null && track.clip != null) return true;
            return false;
        }
    }

    public string Status => !IsAvailable ? "No music available" : IsPlaying ? "Playing" : "Paused";

    /// <summary>Prepare the existing player, without starting audio.</summary>
    public bool Acquire()
    {
        if (!Application.isPlaying || quitting || !IsAvailable) return false;
        if (acquired) return true;

        originalActive = player.gameObject.activeSelf;
        foreach (Transform child in player.transform)
        {
            childStates[child.gameObject] = child.gameObject.activeSelf;
            child.gameObject.SetActive(false);
        }
        foreach (var component in player.GetComponents<Behaviour>())
        {
            componentStates[component] = component.enabled;
            if (component != player && component != player.audioSource) component.enabled = false;
        }

        if (!player.SetExternalControl(true))
        {
            RestorePresentation(true);
            return false;
        }

        player.enabled = true;
        player.audioSource.enabled = true;
        acquired = true;
        player.gameObject.SetActive(true);
        return true;
    }

    public void Toggle()
    {
        if (Acquire()) player.TogglePlayPause();
    }

    public void Next()
    {
        if (Acquire()) player.NextTrack();
    }

    public void Previous()
    {
        if (Acquire()) player.PreviousTrack();
    }

    /// <summary>
    /// Normal release restores the original presentation. On application quit,
    /// pass false so no legacy handlers are reactivated during teardown.
    /// </summary>
    public void Release(bool restoreOriginalState = true)
    {
        if (!acquired && componentStates.Count == 0) return;
        if (player != null)
        {
            player.Stop();
            player.gameObject.SetActive(false);
            player.SetExternalControl(false);
        }
        acquired = false;
        RestorePresentation(restoreOriginalState && !quitting);
    }

    private void RestorePresentation(bool restoreOriginalState)
    {
        // The root is inactive while restoring its children and UI/input flags.
        foreach (var pair in childStates)
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        foreach (var pair in componentStates)
            if (pair.Key != null) pair.Key.enabled = pair.Value;
        childStates.Clear();
        componentStates.Clear();
        if (player != null && restoreOriginalState) player.gameObject.SetActive(originalActive);
    }

    private void OnApplicationQuit()
    {
        quitting = true;
        Release(false);
    }

    private void OnDisable() => Release(!quitting);
}
