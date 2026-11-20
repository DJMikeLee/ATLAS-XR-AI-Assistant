using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    public Button submitButton, retryButton, changeRoomButton, changeDestinationButton;
    public TextMeshProUGUI submitButtonLabel;

    [Header("Component Links")]
    public SpatialAirTag spatialAirTag;
    public PhoneTether phoneTether;
    public AIManager aiManager;
    public WalkingNavigation walkingNavigation;

    [Header("Place Search")]
    [Tooltip("Photon-compatible HTTPS endpoint. Requests run only after Find destination is selected.")]
    [SerializeField] private string placeSearchEndpoint = "https://photon.komoot.io/api/";
    [Tooltip("When a direct place lookup fails, ask Gemini for spelling/acronym and address search hints. Coordinates always come from the map service.")]
    [SerializeField] private bool assistDestinationSearch = true;

    public bool IsWaitingForRoomCode => isWaitingForRoomCode;
    public bool HasPendingDestination => hasPendingDestination;
    public bool HasAlternativeDestinations => hasPendingDestination && pendingOptions.Count > 1;
    public string ChangeDestinationLabel => HasAlternativeDestinations ? "Next match" : "Change destination";
    private bool isWaitingForRoomCode = true;
    private bool awaitingPhone, initialized, acceptsSpeech, searching, hasPendingDestination;
    private float speechDeadline, pendingLat, pendingLon;
    private string pendingName = "", pendingAddress = "", confirmedName = "";
    private DestinationPlace pendingPlace;
    private readonly List<DestinationPlace> pendingOptions = new List<DestinationPlace>();
    private int pendingOptionIndex;
    private bool assistedResult;
    private int searchVersion;
    private Coroutine searchRoutine;
    private UnityWebRequest activeRequest;
    private static MapSearch activeSearchOwner;
    private static double nextSearchTime;
    private static readonly Dictionary<string, CachedSearch> searchCache = new Dictionary<string, CachedSearch>();
    private sealed class CachedSearch { public string Json; public double Expires; }
    private static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    private void OnEnable()
    {
        if (statusText != null) statusText.richText = false;
        if (instructionsText != null) instructionsText.richText = false;
        if (placeholderText != null) placeholderText.richText = false;
        if (searchInput != null)
        {
            searchInput.richText = false;
            searchInput.characterLimit = 200;
            searchInput.onValueChanged.AddListener(OnInputChanged);
        }
        if (micToggle != null) micToggle.onValueChanged.AddListener(OnMicToggleValueChanged);
        if (submitButton != null) submitButton.onClick.AddListener(SubmitInput);
        if (retryButton != null) retryButton.onClick.AddListener(RetryConnection);
        if (changeRoomButton != null) changeRoomButton.onClick.AddListener(ChangeRoom);
        if (changeDestinationButton != null) changeDestinationButton.onClick.AddListener(ChangeOrNextDestination);
        if (dictationExperience != null)
        {
            var events = dictationExperience.DictationEvents;
            events.OnPartialTranscription.AddListener(OnPartialTranscription);
            events.OnFullTranscription.AddListener(OnFullTranscription);
            events.OnStoppedListening.AddListener(OnStoppedListening);
            events.OnError.AddListener(OnDictationError);
        }
        if (!initialized)
        {
            initialized = true;
            awaitingPhone = phoneTether != null && PhoneTether.IsValidRoomCode(phoneTether.currentRoomCode);
            if (searchInput != null) searchInput.SetTextWithoutNotify(awaitingPhone ? phoneTether.currentRoomCode : "");
        }
        CancelSpeech();
        ShowDefaultInstructions();
        RefreshUI();
        FocusMic();
    }

    private void OnDisable()
    {
        CancelSpeech();
        CancelSearch();
        if (searchInput != null)
        {
            searchInput.onValueChanged.RemoveListener(OnInputChanged);
        }
        if (micToggle != null) micToggle.onValueChanged.RemoveListener(OnMicToggleValueChanged);
        if (submitButton != null) submitButton.onClick.RemoveListener(SubmitInput);
        if (retryButton != null) retryButton.onClick.RemoveListener(RetryConnection);
        if (changeRoomButton != null) changeRoomButton.onClick.RemoveListener(ChangeRoom);
        if (changeDestinationButton != null) changeDestinationButton.onClick.RemoveListener(ChangeOrNextDestination);
        if (dictationExperience != null)
        {
            var events = dictationExperience.DictationEvents;
            events.OnPartialTranscription.RemoveListener(OnPartialTranscription);
            events.OnFullTranscription.RemoveListener(OnFullTranscription);
            events.OnStoppedListening.RemoveListener(OnStoppedListening);
            events.OnError.RemoveListener(OnDictationError);
        }
    }

    private void Update()
    {
        // PhoneTether may restore a saved room after this component's first OnEnable.
        if (isWaitingForRoomCode && !awaitingPhone && phoneTether != null &&
            PhoneTether.IsValidRoomCode(phoneTether.currentRoomCode) && searchInput != null && string.IsNullOrWhiteSpace(searchInput.text))
        {
            awaitingPhone = true;
            searchInput.SetTextWithoutNotify(phoneTether.currentRoomCode);
            ShowDefaultInstructions();
        }
        if (isWaitingForRoomCode && awaitingPhone && phoneTether != null && phoneTether.HasFreshLocation)
        {
            CancelSpeech();
            isWaitingForRoomCode = false;
            awaitingPhone = false;
            if (searchInput != null) searchInput.SetTextWithoutNotify("");
            ShowDefaultInstructions();
            FocusMic();
        }
        if (acceptsSpeech && Time.realtimeSinceStartup >= speechDeadline)
        {
            CancelSpeech();
            SetInstructions("No final speech received. Choose Speak to try again.");
        }
        RefreshUI();
    }

    public void ChangeRoom()
    {
        if (!isActiveAndEnabled) return;
        CancelSpeech();
        ClearDestination();
        if (phoneTether != null) phoneTether.ResetConnection();
        isWaitingForRoomCode = true;
        awaitingPhone = false;
        if (searchInput != null) searchInput.SetTextWithoutNotify("");
        ShowDefaultInstructions();
        RefreshUI();
        FocusMic();
    }

    public void RetryConnection()
    {
        if (!isActiveAndEnabled) return;
        CancelSpeech();
        CancelSearch();
        if (phoneTether == null || !PhoneTether.IsValidRoomCode(phoneTether.currentRoomCode))
        {
            ChangeRoom();
            return;
        }
        awaitingPhone = isWaitingForRoomCode;
        phoneTether.RetryConnection();
        ShowDefaultInstructions();
        RefreshUI();
        FocusMic();
    }

    public void ChangeDestination()
    {
        if (!isActiveAndEnabled) return;
        CancelSpeech();
        ClearDestination();
        if (searchInput != null) searchInput.SetTextWithoutNotify("");
        ShowDefaultInstructions();
        RefreshUI();
        FocusMic();
    }

    public void ChangeOrNextDestination()
    {
        if (!isActiveAndEnabled) return;
        if (!HasAlternativeDestinations) { ChangeDestination(); return; }
        pendingOptionIndex = (pendingOptionIndex + 1) % pendingOptions.Count;
        SetPendingPlace(pendingOptions[pendingOptionIndex]);
        ShowDefaultInstructions();
        RefreshUI();
    }

    private void ClearDestination()
    {
        CancelSearch();
        hasPendingDestination = false;
        pendingName = confirmedName = "";
        if (spatialAirTag != null) spatialAirTag.ClearTarget();
        if (walkingNavigation != null) walkingNavigation.ClearDestination();
    }

    private void OnInputChanged(string text)
    {
        CancelSpeech();
        if (isWaitingForRoomCode)
        {
            // Do not let a late GPS sample accept a room while its code is being corrected.
            if (awaitingPhone && phoneTether != null) phoneTether.ResetConnection();
            awaitingPhone = false;
        }
        else ClearDestination();
        ShowDefaultInstructions();
        RefreshUI();
    }

    public void SubmitInput()
    {
        if (!isActiveAndEnabled || searchInput == null) return;
        CancelSpeech();
        if (isWaitingForRoomCode)
        {
            if (!TryNormalizeRoomCode(searchInput.text, out string roomCode))
            {
                SetInstructions("Choose Speak and say exactly four digits, then review them and choose Connect.");
                return;
            }
            if (phoneTether == null) { SetInstructions("Phone connection is unavailable."); return; }
            searchInput.SetTextWithoutNotify(roomCode);
            awaitingPhone = true;
            phoneTether.ConnectToRelay(roomCode);
            SetInstructions("Waiting for phone GPS. Wrong room? Choose Change room, then Speak to provide the correct code.");
        }
        else if (hasPendingDestination)
        {
            if (!HasUsableOrigin()) return;
            if (pendingPlace == null || WalkingRouteProgress.Distance(CurrentOrigin(), pendingPlace.Coordinate) > DestinationSearchData.MaximumDistanceMeters)
            { ClearDestination(); SetInstructions("Your location changed. Search for the destination again."); return; }
            if (spatialAirTag == null) { SetInstructions("Destination display is unavailable."); return; }
            spatialAirTag.SetTarget(pendingLat, pendingLon);
            confirmedName = pendingName;
            hasPendingDestination = false;
            if (walkingNavigation != null) walkingNavigation.PreviewDestination(pendingLat, pendingLon, confirmedName);
            ShowDefaultInstructions();
        }
        else if (walkingNavigation != null && walkingNavigation.HasDestination) walkingNavigation.Show();
        else PerformSearch();
        RefreshUI();
    }

    public void PerformSearch()
    {
        if (!isActiveAndEnabled || searchInput == null) return;
        if (isWaitingForRoomCode) { SubmitInput(); return; }
        CancelSpeech();
        string query = searchInput.text.Trim();
        ClearDestination();
        if (query.Length == 0) { SetInstructions("Choose Speak and say a destination, then choose Find destination."); return; }
        if (!HasUsableOrigin()) return;
        GeoCoordinate origin = CurrentOrigin();
        if (!DestinationSearchData.TryBuildRequest(placeSearchEndpoint, query, origin, phoneTether.HasUsableLocation,
            out string url, out string error)) { SetInstructions(error); return; }
        searching = true;
        SetInstructions("Finding nearby places. Choose Change destination to cancel.\n" + DestinationSearchData.Attribution);
        searchRoutine = StartCoroutine(SearchPlaces(url, query, origin, searchVersion));
        RefreshUI();
    }

    private void OnMicToggleValueChanged(bool isOn)
    {
        if (!isActiveAndEnabled) return;
        if (!isOn)
        {
            if (!acceptsSpeech) return;
            speechDeadline = Time.realtimeSinceStartup + 5f;
            SetInstructions("Finishing speech. Review the text before submitting.");
            if (dictationExperience != null) dictationExperience.Deactivate();
            return;
        }
        // New dictation is an edit and cancels a previous destination or pending room.
        OnInputChanged("");
        if (dictationExperience == null) { SetInstructions("Speech input is unavailable. Return to Maps and try again."); return; }
        if (searchInput != null) searchInput.SetTextWithoutNotify("");
        acceptsSpeech = true;
        speechDeadline = Time.realtimeSinceStartup + 20f;
        if (micToggle != null) micToggle.SetIsOnWithoutNotify(true);
        SetInstructions("Listening. Say the room digits or destination, then review the text.");
        dictationExperience.Activate();
    }

    private void OnPartialTranscription(string text)
    {
        if (isActiveAndEnabled && acceptsSpeech && searchInput != null)
            searchInput.SetTextWithoutNotify(LimitInput(text));
    }

    private void OnFullTranscription(string text)
    {
        if (!isActiveAndEnabled || !acceptsSpeech) return;
        CancelSpeech();
        string result = LimitInput(text).Trim();
        if (isWaitingForRoomCode && TryNormalizeRoomCode(result, out string code)) result = code;
        if (searchInput != null) searchInput.SetTextWithoutNotify(result);
        SetInstructions("Review the text, then choose " + (isWaitingForRoomCode ? "Connect." : "Find destination.") + " Choose Speak to try again.");
        RefreshUI();
        if (submitButton != null && submitButton.interactable) submitButton.Select();
    }

    private void OnStoppedListening()
    {
        if (!isActiveAndEnabled || !acceptsSpeech) return;
        speechDeadline = Time.realtimeSinceStartup + 5f;
        if (micToggle != null) micToggle.SetIsOnWithoutNotify(false);
        SetInstructions("Finishing speech. Review the text before submitting.");
    }

    private void OnDictationError(string error, string message)
    {
        if (!isActiveAndEnabled || !acceptsSpeech) return;
        CancelSpeech();
        SetInstructions("Speech could not finish. Choose Speak to try again.");
    }

    private void CancelSpeech()
    {
        bool hadSpeechSession = acceptsSpeech;
        acceptsSpeech = false;
        if (micToggle != null) micToggle.SetIsOnWithoutNotify(false);
        if (dictationExperience != null && (hadSpeechSession || dictationExperience.IsRequestActive))
            dictationExperience.Cancel();
    }

    private void CancelSearch()
    {
        searchVersion++;
        searching = false;
        hasPendingDestination = false;
        pendingName = "";
        pendingAddress = "";
        pendingPlace = null;
        pendingOptions.Clear();
        pendingOptionIndex = 0;
        assistedResult = false;
        UnityWebRequest request = activeRequest;
        activeRequest = null;
        if (request != null) request.Abort();
        if (searchRoutine != null) StopCoroutine(searchRoutine);
        searchRoutine = null;
        if (request != null) request.Dispose();
        if (activeSearchOwner == this) activeSearchOwner = null;
    }

    private GeoCoordinate CurrentOrigin() => new GeoCoordinate(phoneTether.currentLat, phoneTether.currentLon);

    private bool HasUsableOrigin()
    {
        if (phoneTether == null || !phoneTether.HasFreshLocation)
        { SetInstructions("Restore phone GPS. Keep the tracker visible and phone unlocked, then retry."); return false; }
        if (!phoneTether.HasUsableLocation)
        { SetInstructions("Phone GPS is too uncertain. Move to an open area and wait for a more accurate fix."); return false; }
        return true;
    }

    private sealed class SearchResponse { public string Json; public string Error; }

    private IEnumerator SearchPlaces(string url, string query, GeoCoordinate origin, int version)
    {
        yield return null; // Assign searchRoutine even for synchronous cache hits.
        var response = new SearchResponse();
        yield return FetchPlaces(url, version, response);
        if (!SearchIsCurrent(version)) yield break;
        if (!OriginStillUsable(origin)) { FinishSearch(version, "Restore fresh phone GPS and search again."); yield break; }
        if (response.Error != null) { FinishSearch(version, response.Error); yield break; }
        if (DestinationSearchData.TrySelectPlace(response.Json, query, origin, true, out DestinationPlace direct, out string error))
        {
            pendingOptions.Add(direct);
            FinishSearch(version, null);
            yield break;
        }
        if (!assistDestinationSearch) { FinishSearch(version, error); yield break; }

        SetInstructions("Checking the place name and address. Choose Change destination to cancel.");
        var suggestionResponse = new SearchResponse();
        yield return FetchQueryHints(query, origin, version, suggestionResponse);
        if (!SearchIsCurrent(version)) yield break;
        if (!OriginStillUsable(origin)) { FinishSearch(version, "Restore fresh phone GPS and search again."); yield break; }
        if (suggestionResponse.Error != null)
        { FinishSearch(version, suggestionResponse.Error + " Try saying the full place name and city."); yield break; }
        if (!DestinationQueryPlan.TryParseResponse(suggestionResponse.Json, out List<DestinationQueryHint> hints, out error))
        { FinishSearch(version, error); yield break; }

        SetInstructions("Checking suggested places on the map. You will review the result before routing.");
        string providerError = null;
        foreach (DestinationQueryHint hint in hints)
        {
            foreach (string lookup in DestinationQueryPlan.BuildQueries(hint))
            {
                if (!SearchIsCurrent(version)) yield break;
                if (!OriginStillUsable(origin)) { FinishSearch(version, "Restore fresh phone GPS and search again."); yield break; }
                if (!DestinationSearchData.TryBuildRequest(placeSearchEndpoint, lookup, origin, true, out string hintUrl, out error)) continue;
                var hintedResponse = new SearchResponse();
                yield return FetchPlaces(hintUrl, version, hintedResponse);
                if (!SearchIsCurrent(version)) yield break;
                if (hintedResponse.Error != null) { providerError = hintedResponse.Error; break; }
                if (!DestinationSearchData.TrySelectResolvedPlace(hintedResponse.Json, hint, query, origin, true, out DestinationPlace place, out error)) continue;
                if (!pendingOptions.Exists(item => item.OsmReference == place.OsmReference)) pendingOptions.Add(place);
                break;
            }
            if (providerError != null) break;
        }
        if (!OriginStillUsable(origin)) { FinishSearch(version, "Restore fresh phone GPS and search again."); yield break; }
        // Do not present a partial set as the only interpretation after a service failure.
        if (providerError != null) { pendingOptions.Clear(); FinishSearch(version, providerError); yield break; }
        assistedResult = true;
        FinishSearch(version, pendingOptions.Count == 0
            ? "No verified map match for that name. Try the full place name and city." : null);
    }

    private bool SearchIsCurrent(int version) => version == searchVersion && isActiveAndEnabled;
    private bool OriginStillUsable(GeoCoordinate origin) => phoneTether != null && phoneTether.HasUsableLocation &&
        WalkingRouteProgress.Distance(origin, CurrentOrigin()) <= 1000;

    private IEnumerator FetchPlaces(string url, int version, SearchResponse result)
    {
        if (searchCache.TryGetValue(url, out CachedSearch cached) && cached.Expires > Now)
        {
            result.Json = cached.Json;
            yield break;
        }
        while (activeSearchOwner != null || Now < nextSearchTime)
        {
            if (version != searchVersion || !isActiveAndEnabled) yield break;
            yield return null;
        }
        if (version != searchVersion || !isActiveAndEnabled) yield break;
        // A GPS sample may expire while another request is being completed.
        if (phoneTether == null || !phoneTether.HasUsableLocation)
        { result.Error = "Restore fresh, accurate phone GPS and search again."; yield break; }
        activeSearchOwner = this;
        nextSearchTime = Now + 2;
        UnityWebRequestAsyncOperation operation = BeginPlaceRequest(url, out PlaceDownload download);
        if (operation == null)
        { ReleasePlaceRequest(); result.Error = "Place search could not start. Try again."; yield break; }
        double deadline = Now + 20;
        while (!operation.isDone && Now < deadline)
        {
            if (version != searchVersion || !isActiveAndEnabled) yield break;
            yield return null;
        }
        string response = null;
        string error = null;
        if (!operation.isDone) error = "Place search timed out. Check your connection and try again.";
        else if (download.TooLarge) error = "The place response was too large. Try a more specific name.";
        else if (activeRequest.result != UnityWebRequest.Result.Success)
            error = activeRequest.responseCode == 429 ? "Place search is busy. Wait a moment before trying again." :
                "Place search is unavailable. Check your connection and try again.";
        else response = download.ReadText();
        ReleasePlaceRequest();
        if (response != null)
        {
            if (searchCache.Count >= 32) searchCache.Clear();
            searchCache[url] = new CachedSearch { Json = response, Expires = Now + 600 };
        }
        result.Json = response;
        result.Error = error;
    }

    private IEnumerator FetchQueryHints(string query, GeoCoordinate origin, int version, SearchResponse result)
    {
        APIConfig config = Resources.Load<APIConfig>("API_Secrets");
        if (config == null || string.IsNullOrWhiteSpace(config.geminiApiKey))
        { result.Error = "Name assistance is unavailable."; yield break; }
        string model = aiManager != null && aiManager.fallbackModels != null && aiManager.fallbackModels.Length > 0
            ? aiManager.fallbackModels[0] : "gemini-2.5-flash";
        if (string.IsNullOrWhiteSpace(model) || !Regex.IsMatch(model, @"^gemini-[a-zA-Z0-9.\-]{1,80}$"))
        { result.Error = "Name assistance is not configured."; yield break; }
        while (activeSearchOwner != null || Now < nextSearchTime)
        {
            if (!SearchIsCurrent(version)) yield break;
            yield return null;
        }
        if (!SearchIsCurrent(version)) yield break;
        if (!OriginStillUsable(origin))
        { result.Error = "Restore fresh phone GPS and search again."; yield break; }
        activeSearchOwner = this;
        nextSearchTime = Now + 2;
        UnityWebRequestAsyncOperation operation = BeginHintRequest(model, config.geminiApiKey, query, origin, out PlaceDownload download);
        if (operation == null)
        { ReleasePlaceRequest(); result.Error = "Name assistance could not start."; yield break; }
        double deadline = Now + 30;
        while (!operation.isDone && Now < deadline)
        {
            if (!SearchIsCurrent(version)) yield break;
            yield return null;
        }
        if (!operation.isDone) result.Error = "Name assistance timed out.";
        else if (download.TooLarge) result.Error = "Name assistance returned an unusable response.";
        else if (activeRequest.result != UnityWebRequest.Result.Success)
            result.Error = activeRequest.responseCode == 429 ? "Name assistance has reached its usage limit." : "Name assistance is unavailable.";
        else result.Json = download.ReadText();
        ReleasePlaceRequest();
    }

    private UnityWebRequestAsyncOperation BeginHintRequest(string model, string apiKey, string query, GeoCoordinate origin, out PlaceDownload download)
    {
        download = null;
        try
        {
            string body = DestinationQueryPlan.BuildRequest(query, origin);
            activeRequest = new UnityWebRequest("https://generativelanguage.googleapis.com/v1beta/models/" + model + ":generateContent", "POST");
            activeRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            download = new PlaceDownload(64 * 1024);
            activeRequest.downloadHandler = download;
            activeRequest.timeout = 30;
            activeRequest.redirectLimit = 0;
            activeRequest.SetRequestHeader("Content-Type", "application/json");
            activeRequest.SetRequestHeader("x-goog-api-key", apiKey);
            return activeRequest.SendWebRequest();
        }
        catch (Exception) { return null; } // Never print credentials, request data or provider bodies.
    }

    private UnityWebRequestAsyncOperation BeginPlaceRequest(string url, out PlaceDownload download)
    {
        download = null;
        try
        {
            activeRequest = new UnityWebRequest(url, "GET");
            download = new PlaceDownload();
            activeRequest.downloadHandler = download;
            activeRequest.timeout = 20;
            activeRequest.redirectLimit = 0;
            activeRequest.SetRequestHeader("User-Agent", "XR-AI-Assistant/0.2 (+https://github.com/DJMikeLee/quad-tracker)");
            activeRequest.SetRequestHeader("Accept", "application/json");
            return activeRequest.SendWebRequest();
        }
        catch (Exception) { return null; } // Do not log query strings or user location.
    }

    private void ReleasePlaceRequest()
    {
        if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
        if (activeSearchOwner == this) activeSearchOwner = null;
    }

    private void SetPendingPlace(DestinationPlace place)
    {
        pendingPlace = place;
        pendingName = place.Name;
        pendingAddress = place.Address;
        pendingLat = (float)place.Coordinate.Latitude;
        pendingLon = (float)place.Coordinate.Longitude;
        hasPendingDestination = true;
    }

    private void FinishSearch(int version, string error)
    {
        if (version != searchVersion || !isActiveAndEnabled) return;
        searching = false;
        searchRoutine = null;
        if (error == null && pendingOptions.Count > 0)
        {
            pendingOptionIndex = 0;
            SetPendingPlace(pendingOptions[0]);
            ShowDefaultInstructions();
        }
        else { pendingOptions.Clear(); SetInstructions(error + "\n" + DestinationSearchData.Attribution); }
        RefreshUI();
        if (hasPendingDestination && submitButton != null && submitButton.interactable) submitButton.Select();
    }

    private sealed class PlaceDownload : DownloadHandlerScript
    {
        private readonly MemoryStream buffer = new MemoryStream();
        private readonly int maximumBytes;
        public bool TooLarge { get; private set; }
        public PlaceDownload(int maximumBytes = DestinationSearchData.MaximumResponseBytes) : base(new byte[8192]) { this.maximumBytes = maximumBytes; }
        protected override bool ReceiveData(byte[] data, int length)
        {
            if (data == null || length <= 0) return true;
            if (buffer.Length + length > maximumBytes) { TooLarge = true; return false; }
            buffer.Write(data, 0, length);
            return true;
        }
        public string ReadText() => Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        public override void Dispose() { buffer.Dispose(); base.Dispose(); }
    }

    public static bool TryNormalizeRoomCode(string input, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > 200) return false;
        string[] words = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        string normalized = input.Trim().ToLowerInvariant();
        // Dictation commonly inserts commas and a sentence-ending period. Do not strip
        // arbitrary punctuation: an embedded decimal point or sign must still be rejected.
        if (normalized.EndsWith(".", StringComparison.Ordinal))
            normalized = normalized.Substring(0, normalized.Length - 1);
        normalized = normalized.Replace(',', ' ').Trim();
        var result = new StringBuilder(4);
        foreach (string token in Regex.Split(normalized, @"\s+"))
        {
            int wordDigit = token == "oh" ? 0 : Array.IndexOf(words, token);
            if (wordDigit >= 0) result.Append((char)('0' + wordDigit));
            else
                foreach (char c in token)
                {
                    if (c < '0' || c > '9') return false;
                    result.Append(c);
                    if (result.Length > 4) return false;
                }
            if (result.Length > 4) return false;
        }
        if (result.Length != 4) return false;
        code = result.ToString();
        return true;
    }

    private void RefreshUI()
    {
        string health = phoneTether == null ? "Phone connection is unavailable." : phoneTether.lastNetworkMessage;
        if (phoneTether != null && phoneTether.HasUsableLocation)
        {
            bool needsCompass = spatialAirTag != null && spatialAirTag.orientation == SpatialAirTag.BearingOrientation.PhoneHeading;
            health = !needsCompass ? "Phone location is live." : phoneTether.HasFreshHeading
                ? "Phone location and compass are live."
                : "Phone GPS live; waiting for compass. Allow it on your phone.";
        }
        SetLabel(statusText, health);
        SetLabel(placeholderText, isWaitingForRoomCode ? "Four-digit room code" : "Destination and city");
        SetLabel(submitButtonLabel, isWaitingForRoomCode ? "Connect" : hasPendingDestination ? "Use destination" :
            walkingNavigation != null && walkingNavigation.HasDestination ? "View route" : "Find destination");
        if (changeDestinationButton != null)
            SetLabel(changeDestinationButton.GetComponentInChildren<TextMeshProUGUI>(true), ChangeDestinationLabel);
        bool restoreFocus = SetInteractable(submitButton, !searching && !acceptsSpeech && searchInput != null && !string.IsNullOrWhiteSpace(searchInput.text));
        restoreFocus |= SetInteractable(retryButton, phoneTether != null && PhoneTether.IsValidRoomCode(phoneTether.currentRoomCode));
        restoreFocus |= SetInteractable(changeRoomButton, true);
        restoreFocus |= SetInteractable(changeDestinationButton, !isWaitingForRoomCode);
        if (restoreFocus) FocusMic();
    }

    private void ShowDefaultInstructions()
    {
        if (isWaitingForRoomCode) SetInstructions(awaitingPhone ? "Waiting for phone GPS. Check the room code; choose Retry or Change room." :
            "Open djmikelee.github.io/quad-tracker/ on your phone; keep it unlocked.\nBoth devices need internet. Choose Speak, say the room digits, review them and choose Connect.");
        else if (hasPendingDestination) SetInstructions((assistedResult ? "Did you mean" : "Found") +
            (HasAlternativeDestinations ? " (" + (pendingOptionIndex + 1) + "/" + pendingOptions.Count + ")" : "") + ": " + pendingName +
            "\n" + pendingAddress + "\nUse destination to confirm." + (HasAlternativeDestinations ? " Next match for alternatives." : "") +
            "\n" + DestinationSearchData.Attribution);
        else if (!string.IsNullOrEmpty(confirmedName))
        {
            if (walkingNavigation != null && walkingNavigation.HasDestination)
            {
                SetInstructions("Destination: " + confirmedName + "\nChoose View route for walking directions, or Change destination to replace it.");
                return;
            }
            bool phoneCompass = spatialAirTag != null && spatialAirTag.orientation == SpatialAirTag.BearingOrientation.PhoneHeading;
            SetInstructions("Destination: " + confirmedName + "\n" + (phoneCompass
                ? "Phone compass: hold the phone flat, top forward. Straight-line guidance."
                : "North-up: north stays at the top. Straight-line guidance."));
        }
        else SetInstructions("Choose Speak and say a destination and city, review it, then choose Find destination.");
    }

    private static bool SetInteractable(Selectable control, bool interactable)
    {
        if (control == null) return false;
        bool losesFocus = !interactable && control.interactable &&
            UnityEngine.EventSystems.EventSystem.current != null &&
            UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == control.gameObject;
        control.interactable = interactable;
        return losesFocus;
    }

    private void FocusMic()
    {
        if (micToggle != null && micToggle.isActiveAndEnabled && micToggle.IsInteractable()) micToggle.Select();
        else if (changeRoomButton != null && changeRoomButton.isActiveAndEnabled && changeRoomButton.IsInteractable()) changeRoomButton.Select();
    }
    private void SetInstructions(string text) => SetLabel(instructionsText, text);
    private static void SetLabel(TextMeshProUGUI label, string text) { if (label != null && label.text != text) label.text = text; }
    private static string LimitInput(string text) => string.IsNullOrEmpty(text) ? "" : text.Substring(0, Math.Min(200, text.Length));
}
