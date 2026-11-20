using Meta.WitAi;
using Meta.WitAi.Json;
using UnityEngine;

/// <summary>Uses the original Assistant services while their legacy page stays hidden.</summary>
[DisallowMultipleComponent]
public sealed class RayBanAssistantBridge : MonoBehaviour
{
    public AIManager manager;
    public GameObject serviceRoot;

    private bool acquired;
    private bool rootWasActive;
    private bool managerWasEnabled;
    private bool wakeWasEnabled;
    private bool voiceWasEnabled;
    private bool previousAutoListen;
    private bool previousActionOnly;
    private bool previousKeyboard;
    private bool requested;
    private bool paused;
    private bool quitting;
    private bool hasPresented;
    private float requestedAt;
    private string question = "";
    private string failure;
    private WakeWordManager wake;

    public bool IsAcquired => acquired;
    public bool IsListening => acquired && wake != null && wake.appVoice != null && wake.appVoice.MicActive;
    public bool IsBusy => acquired && ((manager != null && manager.IsResponding) || requested);
    public bool CanListen => acquired && !paused && manager != null && manager.IsInitialized &&
        manager.IsConfigured && wake != null && wake.IsInitialized && wake.isActiveAndEnabled &&
        wake.appVoice != null && wake.appVoice.isActiveAndEnabled;
    public string Question => question;
    public string Response => manager != null && manager.aiResponseText != null ? manager.aiResponseText.text : "";
    public Texture Illustration => manager != null && manager.describePicture != null && manager.describePicture.enabled
        ? manager.describePicture.texture : null;

    public string Status
    {
        get
        {
            if (!HasReferences) return "Assistant is not connected";
            if (paused) return "Paused · select Listen to continue";
            if (!acquired) return "Assistant closed";
            if (!string.IsNullOrEmpty(failure)) return failure;
            if (!manager.IsInitialized || !wake.IsInitialized) return "Preparing Assistant…";
            if (!manager.IsConfigured) return "Assistant API configuration unavailable";
            if (IsListening) return "Listening…";
            if (requested) return "Processing speech…";
            if (manager.IsResponding) return "Preparing or speaking response…";
            return "Select Listen to ask a question";
        }
    }

    private bool HasReferences => manager != null && manager.wakeWordManager != null &&
        manager.wakeWordManager.appVoice != null && serviceRoot != null &&
        manager.transform.IsChildOf(serviceRoot.transform) &&
        manager.wakeWordManager.transform.IsChildOf(serviceRoot.transform);

    // Opening prepares existing components (including SDK microphone prewarming).
    // Recognition and web requests require the user's explicit Listen action.
    public bool Acquire()
    {
        if (!Application.isPlaying || quitting || !HasReferences) return false;
        if (acquired) return true;
        wake = manager.wakeWordManager;
        rootWasActive = serviceRoot.activeSelf;
        managerWasEnabled = manager.enabled;
        wakeWasEnabled = wake.enabled;
        voiceWasEnabled = wake.appVoice.enabled;
        previousAutoListen = manager.activateVoiceAfterResponse;
        previousActionOnly = wake.onlyActivateFromAction;
        previousKeyboard = wake.useKeyboardShortCutActivation;
        manager.activateVoiceAfterResponse = false;
        wake.onlyActivateFromAction = true;
        wake.useKeyboardShortCutActivation = false;
        manager.SetExternalPresentation(true, true);
        if (!hasPresented) { manager.ResetUI(); hasPresented = true; }
        manager.enabled = wake.enabled = wake.appVoice.enabled = true;
        acquired = true;
        requested = false;
        failure = null;
        wake.OnResponseDetected.AddListener(OnRecognized);
        wake.appVoice.VoiceEvents.OnPartialTranscription.AddListener(OnPartial);
        wake.appVoice.VoiceEvents.OnError.AddListener(OnVoiceError);
        wake.appVoice.VoiceEvents.OnRequestCompleted.AddListener(OnRecognitionComplete);
        serviceRoot.SetActive(true);
        AssistantMicrophoneLifetime.PrepareForAssistant();
        return true;
    }

    public void ToggleListening()
    {
        if (!acquired || paused) return;
        if (IsBusy || IsListening) { Stop(); return; }
        if (!CanListen) return;
        failure = null;
        question = "";
        manager.ResetUI();
        requested = true;
        requestedAt = Time.realtimeSinceStartup;
        try
        {
            AssistantMicrophoneLifetime.PrepareForAssistant();
            wake.Activate();
        }
        catch (System.Exception)
        {
            Stop();
            failure = "Microphone unavailable · try again";
        }
    }

    public void Stop()
    {
        requested = false;
        if (wake != null) wake.CancelListening();
        if (manager != null) manager.CancelResponse();
        if (acquired) AssistantMicrophoneLifetime.ReleaseIdleMicrophone();
    }

    public void Clear()
    {
        if (!acquired) return;
        Stop();
        manager.ResetUI();
        manager.ClearMemory();
        question = "";
        failure = null;
    }

    private void OnPartial(string text)
    {
        if (acquired && requested) question = text ?? "";
    }

    private void OnRecognized(WitResponseNode response)
    {
        if (!acquired || !requested) return;
        question = response != null ? response.GetTranscription() ?? "" : "";
        requested = false;
        if (string.IsNullOrWhiteSpace(question)) failure = "No speech recognized · try again";
    }

    private void OnVoiceError(string error, string message)
    {
        if (!acquired || !requested) return;
        Stop();
        // Provider messages can include sensitive data; show a bounded local message.
        failure = "Voice recognition unavailable · check microphone and connection";
    }

    private void OnRecognitionComplete()
    {
        // This SDK event follows all response/error callbacks. A completed
        // request without a recognized response must not remain busy forever.
        if (!acquired || !requested) return;
        requested = false;
        if (wake != null) wake.CancelListening();
        failure = "No speech recognized · try again";
    }

    private void Update()
    {
        if (acquired && requested && Time.realtimeSinceStartup - requestedAt > 45f)
        {
            Stop();
            failure = "Voice recognition timed out · try again";
        }
    }

    public void Release(bool restoreOriginalState = true)
    {
        if (!acquired) return;
        Stop();
        acquired = false;
        if (wake != null)
        {
            wake.OnResponseDetected.RemoveListener(OnRecognized);
            if (wake.appVoice != null)
            {
                wake.appVoice.VoiceEvents.OnPartialTranscription.RemoveListener(OnPartial);
                wake.appVoice.VoiceEvents.OnError.RemoveListener(OnVoiceError);
                wake.appVoice.VoiceEvents.OnRequestCompleted.RemoveListener(OnRecognitionComplete);
            }
        }
        // Restore component flags while the shared group is inactive. The legacy
        // UI and all its gesture adapters remain untouched throughout the lease.
        if (serviceRoot != null) serviceRoot.SetActive(false);
        AssistantMicrophoneLifetime.ReleaseIdleMicrophone();
        if (manager != null)
        {
            manager.SetExternalPresentation(false, false);
            manager.activateVoiceAfterResponse = previousAutoListen;
            manager.enabled = managerWasEnabled;
        }
        if (wake != null)
        {
            wake.onlyActivateFromAction = previousActionOnly;
            wake.useKeyboardShortCutActivation = previousKeyboard;
            wake.enabled = wakeWasEnabled;
            if (wake.appVoice != null) wake.appVoice.enabled = voiceWasEnabled;
        }
        if (serviceRoot != null && restoreOriginalState && !quitting) serviceRoot.SetActive(rootWasActive);
    }

    private void OnApplicationPause(bool value)
    {
        paused = value;
        if (value && acquired) Stop();
    }
    private void OnApplicationQuit() { quitting = true; Release(false); }
    private void OnDisable() => Release(!quitting);
    private void OnDestroy() => Release(!quitting);
}
