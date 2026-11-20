using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Oculus.Voice.Dictation;

public class LiveCaptionManager : MonoBehaviour
{
    [Header("Meta Voice Dictation")]
    [Tooltip("Requires an AppDictationExperience component on this object or in the scene.")]
    public AppDictationExperience dictationExperience;

    [Header("UI References")]
    public Toggle micToggle;

    [Tooltip("Shows words as they are currently being spoken.")]
    public TextMeshProUGUI liveTranscriptionText;

    [Tooltip("The accumulated history of all spoken sentences.")]
    public TextMeshProUGUI captionHistoryText;

    [Tooltip("Drag the Scroll View here to auto-scroll to the bottom.")]
    public ScrollRect historyScrollView; // FIXED: Added this missing variable!

    private bool isIntentionallyListening = false;
    private string accumulatedHistory = "";

    private void Start()
    {
        // Reset UI
        liveTranscriptionText.text = "Waiting for speech...";
        captionHistoryText.text = "";

        // Hook up the UI Toggle
        if (micToggle != null)
        {
            micToggle.onValueChanged.AddListener(OnMicToggleValueChanged);
        }

        // Subscribe to Meta Dictation Events
        if (dictationExperience != null)
        {
            dictationExperience.DictationEvents.OnPartialTranscription.AddListener(OnPartialTranscription);
            dictationExperience.DictationEvents.OnFullTranscription.AddListener(OnFullTranscription);
            dictationExperience.DictationEvents.OnStoppedListening.AddListener(OnDictationStopped);
            dictationExperience.DictationEvents.OnError.AddListener(OnDictationError);
        }
    }

    private void OnDestroy()
    {
        if (micToggle != null) micToggle.onValueChanged.RemoveListener(OnMicToggleValueChanged);

        if (dictationExperience != null)
        {
            dictationExperience.DictationEvents.OnPartialTranscription.RemoveListener(OnPartialTranscription);
            dictationExperience.DictationEvents.OnFullTranscription.RemoveListener(OnFullTranscription);
            dictationExperience.DictationEvents.OnStoppedListening.RemoveListener(OnDictationStopped);
            dictationExperience.DictationEvents.OnError.RemoveListener(OnDictationError);
        }
    }

    private void OnMicToggleValueChanged(bool isOn)
    {
        isIntentionallyListening = isOn;

        if (isOn)
        {
            liveTranscriptionText.text = "Listening...";
            dictationExperience.Activate();
        }
        else
        {
            liveTranscriptionText.text = "Captions Paused.";
            dictationExperience.Deactivate();
        }
    }

    private void OnPartialTranscription(string text)
    {
        liveTranscriptionText.text = text;
    }

    private void OnFullTranscription(string text)
    {
        liveTranscriptionText.text = "";
        accumulatedHistory += text + "\n\n";
        captionHistoryText.text = accumulatedHistory;

        // SMART SCROLL: Only auto-scroll to the bottom if the user is already near the bottom (less than 5% scrolled up).
        if (gameObject.activeInHierarchy && historyScrollView != null)
        {
            if (historyScrollView.verticalNormalizedPosition < 0.05f)
            {
                StartCoroutine(ScrollToBottom());
            }
        }
    }

    private void OnDictationStopped()
    {
        // CRITICAL LOOP: If the toggle is still ON, force the mic to start listening again.
        if (isIntentionallyListening)
        {
            Invoke(nameof(RestartDictation), 0.1f);
        }
    }

    private void OnDictationError(string error, string message)
    {
        Debug.LogWarning($"Dictation Error: {error} - {message}");

        if (isIntentionallyListening)
        {
            Invoke(nameof(RestartDictation), 1.0f);
        }
    }

    private void RestartDictation()
    {
        if (isIntentionallyListening) dictationExperience.Activate();
    }

    public void ClearCaptions()
    {
        accumulatedHistory = "";
        captionHistoryText.text = "";
        liveTranscriptionText.text = "Cleared.";
    }

    // FIXED: Added the missing coroutine!
    private System.Collections.IEnumerator ScrollToBottom()
    {
        // Wait one frame to let Unity's UI update the new height of the text box
        yield return new WaitForEndOfFrame();

        // Push the scrollbar to the very bottom (0 is bottom, 1 is top)
        if (historyScrollView != null)
        {
            historyScrollView.verticalNormalizedPosition = 0f;
        }
    }
}