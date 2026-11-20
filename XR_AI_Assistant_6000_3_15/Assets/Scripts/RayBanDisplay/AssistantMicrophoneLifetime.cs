using Meta.Voice;
using Meta.WitAi.Data;
using Meta.WitAi.Lib;
using UnityEngine;

/// <summary>Releases the Voice SDK's idle hardware capture without replacing its shared input.</summary>
[DisallowMultipleComponent]
public sealed class AssistantMicrophoneLifetime : MonoBehaviour
{
    private AudioBuffer buffer;
    private Mic suspendedMic;
    private bool suspendWhenIdle;

    public static void PrepareForAssistant()
    {
        // Never instantiate the SDK buffer merely to inspect or release it.
        if (!AudioBuffer.HasInstance) return;
        var lifetime = AudioBuffer.Instance.GetComponent<AssistantMicrophoneLifetime>();
        if (lifetime == null) return;
        lifetime.enabled = true;
        lifetime.suspendWhenIdle = false;
        lifetime.ResumeInput();
    }

    public static void ReleaseIdleMicrophone()
    {
        if (!AudioBuffer.HasInstance) return;
        var existingBuffer = AudioBuffer.Instance;
        var lifetime = existingBuffer.GetComponent<AssistantMicrophoneLifetime>();
        if (lifetime == null) lifetime = existingBuffer.gameObject.AddComponent<AssistantMicrophoneLifetime>();
        lifetime.enabled = true;
        lifetime.suspendWhenIdle = true;
        lifetime.SuspendIdleInput();
    }

    private void OnEnable()
    {
        buffer = GetComponent<AudioBuffer>();
        if (buffer != null) buffer.Events.OnAudioStateChange += OnAudioStateChanged;
    }

    private void OnAudioStateChanged(VoiceAudioInputState state)
    {
        // AudioBuffer sends Activating before calling Mic.StartRecording. Restore
        // our input for any shared consumer, including Map dictation, at that point.
        if (state == VoiceAudioInputState.Activating) ResumeInput();
        else if (state == VoiceAudioInputState.Off) SuspendIdleInput();
    }

    private void ResumeInput()
    {
        var mic = suspendedMic;
        suspendedMic = null;
        if (mic != null && buffer != null && ReferenceEquals(buffer.MicInput, mic)) mic.enabled = true;
    }

    private void SuspendIdleInput()
    {
        if (!suspendWhenIdle || buffer == null || buffer.AudioState != VoiceAudioInputState.Off) return;
        var mic = buffer.MicInput as Mic;
        // A remaining buffer recorder or direct input consumer owns its capture.
        // Custom inputs and SDK components already disabled by others are untouched.
        if (mic == null || mic.IsRecording || !mic.enabled || !mic.ActivateOnEnable) return;
        suspendedMic = mic;
        // StopRecording alone retains the SDK's prewarmed hardware clip. Disabling
        // this component runs its own shutdown, including pending activation cleanup.
        mic.enabled = false;
    }

    private void OnDisable()
    {
        if (buffer != null) buffer.Events.OnAudioStateChange -= OnAudioStateChanged;
        SuspendIdleInput();
    }
}
