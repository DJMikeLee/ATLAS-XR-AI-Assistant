using System.Collections.Generic;
using UnityEngine;

/// <summary>Reuses the original Azure backend without activating its legacy input UI.</summary>
[DisallowMultipleComponent]
public sealed class RayBanCaptionBridge : MonoBehaviour
{
    public LiveTranslationManager manager;
    private readonly Dictionary<GameObject, bool> children = new Dictionary<GameObject, bool>();
    private readonly Dictionary<Behaviour, bool> components = new Dictionary<Behaviour, bool>();
    private bool acquired, originalActive, quitting;
    public bool IsAcquired => acquired;
    public bool IsListening => acquired && manager != null && manager.IsListening;
    public bool IsBusy => manager != null && manager.IsBusy;
    public string Status => manager == null ? "Caption service is not connected." : manager.Status;

    public bool SetVisible(bool visible)
    {
        if (!visible) { Release(); return false; }
        if (!Application.isPlaying || quitting || manager == null) return false;
        if (acquired) return true;
        originalActive = manager.gameObject.activeSelf;
        foreach (Transform child in manager.transform)
        { children[child.gameObject] = child.gameObject.activeSelf; child.gameObject.SetActive(false); }
        foreach (var component in manager.GetComponents<Behaviour>())
        { components[component] = component.enabled; if (component != manager) component.enabled = false; }
        acquired = true;
        manager.enabled = true;
        manager.gameObject.SetActive(true);
        manager.SetListening(false);
        return true;
    }

    public void Toggle()
    {
        if (SetVisible(true) && !manager.IsBusy) manager.SetListening(!manager.IsListening);
    }
    public void CycleSource()
    {
        if (!acquired || manager == null) return;
        manager.SetLanguages((manager.SourceIndex + 1) % 5, manager.TargetIndex);
    }
    public void CycleTarget()
    {
        if (!acquired || manager == null) return;
        manager.SetLanguages(manager.SourceIndex, (manager.TargetIndex + 1) % 5);
    }
    public void ToggleView() { if (acquired && manager != null) manager.SetTranslationView(!manager.ShowTranslation); }

    public void Release(bool restore = true)
    {
        if (!acquired && components.Count == 0) return;
        if (manager != null) { manager.SetListening(false); manager.gameObject.SetActive(false); }
        foreach (var state in children) if (state.Key != null) state.Key.SetActive(state.Value);
        foreach (var state in components) if (state.Key != null) state.Key.enabled = state.Value;
        children.Clear(); components.Clear(); acquired = false;
        if (manager != null && restore && !quitting) manager.gameObject.SetActive(originalActive);
    }
    private void OnDisable() => Release(!quitting);
    private void OnApplicationQuit() { quitting = true; Release(false); }
}
