using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using Oculus.Voice.Dictation;

public class MapSearch : MonoBehaviour
{
    [Header("Meta Voice Dictation")]
    public AppDictationExperience dictationExperience;

    [Header("UI References")]
    public TMP_InputField searchInput;
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI placeholderText;
    public TextMeshProUGUI instructionsText;
    public Toggle micToggle;

    [Header("Component Links")]
    public SpatialAirTag spatialAirTag;
    public PhoneTether phoneTether;
    public AIManager aiManager;

    private bool isIntentionallyListening = false;
    private bool finalTextReceivedThisSession = false;
    private bool isWaitingForRoomCode = true;
    private string geminiApiKey;

    private void Awake()
    {
        APIConfig config = Resources.Load<APIConfig>("API_Secrets");
        if (config != null)
        {
            geminiApiKey = config.geminiApiKey;
        }
        else
        {
            Debug.LogError("[MapSearch] API_Secrets file not found! Run ATLAS VR -> API Setup.");
        }
    }

    private void Start()
    {
        if (micToggle != null)
            micToggle.onValueChanged.AddListener(OnMicToggleValueChanged);

        SetRoomCodeMode();

        if (dictationExperience != null)
        {
            dictationExperience.DictationEvents.OnPartialTranscription.AddListener(OnPartialTranscription);
            dictationExperience.DictationEvents.OnFullTranscription.AddListener(OnFullTranscription);
            dictationExperience.DictationEvents.OnStoppedListening.AddListener(OnStoppedListening);
            dictationExperience.DictationEvents.OnError.AddListener(OnDictationError);
        }
    }

    private void Update()
    {
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (statusText == null || phoneTether == null) return;

        // Do not overwrite active speech/search states
        string current = statusText.text;

        if (current.Contains("Listening") ||
            current.Contains("Processing") ||
            current.Contains("Asking AI") ||
            current.Contains("Voice error") ||
            current.Contains("Invalid") ||
            current.Contains("Location not found") ||
            current.Contains("Locked on"))
        {
            return;
        }

        if (isWaitingForRoomCode)
        {
            statusText.text = "<color=white>Awaiting Phone Sync</color>";
            return;
        }

        if (phoneTether.isSignalLost)
        {
            statusText.text = "<color=yellow>Phone Signal Lost!</color>";
            return;
        }

        if (phoneTether.isReceivingData)
        {
            statusText.text = "<color=#00ff00>Tracking Live!</color>";
            return;
        }

        if (phoneTether.isSocketOpen)
        {
            statusText.text = "<color=white>Connected to server. Waiting for GPS...</color>";
            return;
        }

        if (!string.IsNullOrEmpty(phoneTether.lastNetworkMessage))
        {
            statusText.text = $"<color=white>{phoneTether.lastNetworkMessage}</color>";
        }
    }

    private void OnDestroy()
    {
        if (micToggle != null)
            micToggle.onValueChanged.RemoveListener(OnMicToggleValueChanged);

        if (dictationExperience != null)
        {
            dictationExperience.DictationEvents.OnPartialTranscription.RemoveListener(OnPartialTranscription);
            dictationExperience.DictationEvents.OnFullTranscription.RemoveListener(OnFullTranscription);
            dictationExperience.DictationEvents.OnStoppedListening.RemoveListener(OnStoppedListening);
            dictationExperience.DictationEvents.OnError.RemoveListener(OnDictationError);
        }
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(RestartDictation));
        isIntentionallyListening = false;
        finalTextReceivedThisSession = false;

        if (dictationExperience != null)
            dictationExperience.Deactivate();

        if (micToggle != null)
            micToggle.SetIsOnWithoutNotify(false);
    }

    private void ShowRoomCodeInstructions()
    {
        if (instructionsText != null)
        {
            instructionsText.text =
                "1. Go to djmikelee.github.io/quad-tracker/\n" +
                "2. Input room number code using speech-to-text";
        }
    }

    private void ShowDestinationInstructions()
    {
        if (instructionsText != null)
        {
            instructionsText.text =
                "1. Phone connected\n" +
                "2. Press mic and say destination";
        }
    }

    private void SetRoomCodeMode()
    {
        isWaitingForRoomCode = true;

        if (spatialAirTag != null)
            spatialAirTag.ClearTarget();

        if (searchInput != null)
            searchInput.SetTextWithoutNotify("");

        if (placeholderText != null)
            placeholderText.text = "Input Room number shown on phone...";

        ShowRoomCodeInstructions();

        if (statusText != null)
            statusText.text = "<color=white>Awaiting Phone Sync</color>";
    }

    private void SetNavigationMode()
    {
        isWaitingForRoomCode = false;

        if (spatialAirTag != null)
            spatialAirTag.ClearTarget();

        if (searchInput != null)
            searchInput.SetTextWithoutNotify("");

        if (placeholderText != null)
            placeholderText.text = "Search for a location...";

        ShowDestinationInstructions();

        if (statusText != null)
            statusText.text = "<color=white>Connecting to phone...</color>";
    }

    private void OnMicToggleValueChanged(bool isOn)
    {
        CancelInvoke(nameof(RestartDictation));
        isIntentionallyListening = isOn;
        finalTextReceivedThisSession = false;

        if (isOn)
        {
            if (searchInput != null)
                searchInput.SetTextWithoutNotify("");

            if (statusText != null)
                statusText.text = "<color=red>Listening...</color>";

            if (dictationExperience != null)
                dictationExperience.Activate();
        }
        else
        {
            if (statusText != null)
                statusText.text = "<color=yellow>Processing...</color>";

            if (dictationExperience != null)
                dictationExperience.Deactivate();
        }
    }

    private void OnPartialTranscription(string text)
    {
        if (!isIntentionallyListening || searchInput == null) return;
        searchInput.SetTextWithoutNotify(text);
    }

    private void OnFullTranscription(string text)
    {
        string finalText = text == null ? "" : text.Trim();

        Debug.Log($"[MapSearch] Full transcription = '{finalText}', isWaitingForRoomCode = {isWaitingForRoomCode}");

        if (searchInput != null)
            searchInput.SetTextWithoutNotify(finalText);

        finalTextReceivedThisSession = true;
        isIntentionallyListening = false;

        if (dictationExperience != null)
            dictationExperience.Deactivate();

        if (micToggle != null)
            micToggle.SetIsOnWithoutNotify(false);

        if (isWaitingForRoomCode)
            ProcessRoomCode(finalText);
        else
            PerformSearch();
    }

    private void ProcessRoomCode(string input)
    {
        string cleanCode = Regex.Replace(input, @"\D", "");

        if (cleanCode.Length == 4)
        {
            if (phoneTether == null)
            {
                if (statusText != null)
                    statusText.text = "<color=red>PhoneTether missing.</color>";
                return;
            }

            phoneTether.ConnectToRelay(cleanCode);
            SetNavigationMode();
        }
        else
        {
            if (statusText != null)
                statusText.text = "<color=red>Invalid. Need 4 digits.</color>";
        }
    }

    private void OnStoppedListening()
    {
        if (isIntentionallyListening && !finalTextReceivedThisSession)
            Invoke(nameof(RestartDictation), 0.1f);
    }

    private void OnDictationError(string error, string message)
    {
        if (statusText != null)
            statusText.text = "<color=red>Voice error. Try again.</color>";

        if (isIntentionallyListening && !finalTextReceivedThisSession)
            Invoke(nameof(RestartDictation), 1.0f);
    }

    private void RestartDictation()
    {
        if (isIntentionallyListening && dictationExperience != null)
            dictationExperience.Activate();
    }

    public void PerformSearch()
    {
        if (searchInput == null || spatialAirTag == null) return;

        string query = searchInput.text == null ? "" : searchInput.text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            if (statusText != null)
                statusText.text = "<color=red>Say a destination.</color>";
            return;
        }

        StartCoroutine(AskGeminiForCoordinates(query));
    }

    private IEnumerator AskGeminiForCoordinates(string query)
    {
        if (statusText != null)
            statusText.text = "<color=yellow>Asking AI...</color>";

        if (aiManager == null || string.IsNullOrEmpty(geminiApiKey))
        {
            if (statusText != null) statusText.text = "<color=red>AI Link missing!</color>";
            yield break;
        }

        string systemPrompt =
            "You are a geocoding API. I will give you a location or building name. " +
            "IMPORTANT CONTEXT: Prioritize University of Illinois Urbana-Champaign (UIUC) locations. " +
            "Respond ONLY in this exact format: LAT: [latitude], LON: [longitude].";

        string escapedQuery = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string escapedPrompt = systemPrompt.Replace("\\", "\\\\").Replace("\"", "\\\"");

        string jsonPayload = $@"
    {{
      ""system_instruction"": {{
        ""parts"": [{{ ""text"": ""{escapedPrompt}"" }}]
      }},
      ""contents"": [
        {{
          ""parts"": [{{ ""text"": ""Location: {escapedQuery}"" }}]
        }}
      ],
      ""tools"": [
        {{ ""google_search"": {{}} }}
      ]
    }}";

        bool requestSuccessful = false;

        // --- THE FIX: Loop through the fallback array if the server is busy ---
        for (int i = 0; i < aiManager.fallbackModels.Length; i++)
        {
            string targetModel = aiManager.fallbackModels[i];
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{targetModel}:generateContent?key={geminiApiKey}";

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    string responseJson = request.downloadHandler.text;
                    string aiText = ExtractJsonValue(responseJson, "\"text\": \"", "\"");
                    aiText = aiText.Replace("\\n", "").Trim();

                    Debug.Log("[Gemini Geocoder] " + aiText);

                    if (aiText.Contains("LAT:") && aiText.Contains("LON:"))
                    {
                        try
                        {
                            string latPart = aiText.Substring(aiText.IndexOf("LAT:") + 4);
                            latPart = latPart.Substring(0, latPart.IndexOf(",")).Trim();

                            string lonPart = aiText.Substring(aiText.IndexOf("LON:") + 4).Trim();

                            if (float.TryParse(latPart, NumberStyles.Float, CultureInfo.InvariantCulture, out float lat) &&
                                float.TryParse(lonPart, NumberStyles.Float, CultureInfo.InvariantCulture, out float lon))
                            {
                                spatialAirTag.targetLat = lat;
                                spatialAirTag.targetLon = lon;

                                if (statusText != null)
                                    statusText.text = $"<color=#00ff00>Locked on: {query}</color>";

                                requestSuccessful = true;
                                break; // Success! Exit the loop.
                            }
                            else
                            {
                                if (statusText != null) statusText.text = "<color=red>Coordinate parse error.</color>";
                                requestSuccessful = true;
                                break;
                            }
                        }
                        catch
                        {
                            if (statusText != null) statusText.text = "<color=red>Failed to read AI format.</color>";
                            requestSuccessful = true;
                            break;
                        }
                    }
                    else
                    {
                        if (statusText != null) statusText.text = "<color=red>Location not found.</color>";
                        requestSuccessful = true;
                        break;
                    }
                }
                else
                {
                    long code = request.responseCode;
                    // If Gemini is busy (503) or out of quota (429), log it and try the next model!
                    if (code == 429 || code == 404 || code == 503 || code == 500)
                    {
                        Debug.LogWarning($"[MapSearch] Model {targetModel} failed ({code}). Swapping to next fallback...");
                        continue; // Loop continues to the next model
                    }
                    else
                    {
                        // Fatal connection error
                        Debug.LogError("Gemini API Error: " + request.error);
                        break;
                    }
                }
            }
        }

        if (!requestSuccessful && statusText != null)
        {
            statusText.text = "<color=red>AI Network Error. All models failed.</color>";
        }
    }

    private string ExtractJsonValue(string json, string key, string endChar)
    {
        int startIndex = json.IndexOf(key);
        if (startIndex == -1) return "";

        startIndex += key.Length;
        int endIndex = json.IndexOf(endChar, startIndex);

        if (endIndex == -1) return "";
        return json.Substring(startIndex, endIndex - startIndex);
    }
}