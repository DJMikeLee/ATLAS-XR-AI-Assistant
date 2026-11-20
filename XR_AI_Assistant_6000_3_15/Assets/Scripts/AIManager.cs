using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.WitAi.Json;
using Meta.WitAi;
using PassthroughCameraSamples;
using TMPro;
using UnityEngine.UI;
using Meta.WitAi.TTS.Utilities;
using ZXing;

public class AIManager : MonoBehaviour
{
    public bool activateVoiceAfterResponse = true;

    [Header("Gemini Settings")]
    [Tooltip("Enter your Gemini API Key here")]
    private string geminiApiKey;

    [Tooltip("The AI will automatically rotate through these models if one hits a quota limit (429) or is retired (404).")]
    public string[] fallbackModels = new string[] { "gemini-2.5-flash", "gemini-3.0-flash", "gemini-2.5-flash-lite" };
    private int currentModelIndex = 0;

    [Tooltip("Check this to allow the AI to search the live internet for answers")]
    public bool enableGoogleSearch = true;

    [Tooltip("Paste your UIUC Persona and Rules here")]
    [TextArea(10, 20)]
    public string systemInstructions;

    public WakeWordManager wakeWordManager;
    public Meta.XR.PassthroughCameraAccess passthroughCamera;

    public Toggle micToggle;

    public TextMeshProUGUI aiResponseText;
    public RawImage describePicture;

    [Tooltip("Optional test image for Editor Play with XR disabled. Live camera capture takes priority; this image is never used as a Quest Link fallback.")]
    public Texture2D debugPicture;

    public TTSSpeaker ttsSpeaker;

    // Owned readback buffer. Never alias the imported debug image or SDK texture.
    private Texture2D cameraPicture;
    private Texture2D responsePicture;
    private CancellationTokenSource responseCancellation;
    private BufferedAssistantSpeech responseSpeech;
    private bool responseInProgress;
    private bool externalPresentation;
    private bool externalPresentationVisible;
    public bool IsResponding => responseInProgress;
    public bool IsInitialized { get; private set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(geminiApiKey);

    // The shared manager stays enabled when the AI page closes. Use the existing
    // page-local answer text to observe that lifecycle without changing the scene.
    private bool IsAssistantVisible => externalPresentation ? externalPresentationVisible :
        aiResponseText == null || aiResponseText.gameObject.activeInHierarchy;

    // The redesigned display reads the existing output targets without showing
    // or rewiring the legacy page. Its visibility still owns request lifetime.
    public void SetExternalPresentation(bool enabled, bool visible)
    {
        externalPresentation = enabled;
        externalPresentationVisible = enabled && visible;
        if (!IsAssistantVisible) CancelCurrentResponse();
    }

    public void CancelResponse() => CancelCurrentResponse();

    private const string CameraUnavailableNote = " [CAMERA STATUS: No current camera image is available for this request. Do not treat previous images or descriptions as the current view. If asked what you see, explain that the camera feed is unavailable.]";

    private sealed class RequestVision
    {
        public string base64Image = "";
        public string qrData;
        public string note = CameraUnavailableNote;
    }

    // --- The Memory Bank ---
    private List<string> conversationHistory = new List<string>();

    void Start()
    {
        // 1. Load the secrets vault
        APIConfig config = Resources.Load<APIConfig>("API_Secrets");
        if (config != null)
        {
            geminiApiKey = config.geminiApiKey;
        }
        else
        {
            Debug.LogError("API_Secrets file not found! Did you run ATLAS VR -> API Setup?");
        }
        // Request both permissions simultaneously using the array method so Android doesn't drop one!
        string[] permissions = {
            UnityEngine.Android.Permission.Camera,
            "horizonos.permission.HEADSET_CAMERA"
        };

        UnityEngine.Android.Permission.RequestUserPermissions(permissions);

        if (wakeWordManager != null)
        {
            wakeWordManager.OnResponseDetected.AddListener(HandleResponse);
            wakeWordManager.OnWakeWordDetected.AddListener(CancelCurrentResponse);
        }
        if (aiResponseText != null) aiResponseText.text = "";
        IsInitialized = true;
    }

    public async void HandleResponse(WitResponseNode response)
    {
        // A new deliberate mic activation cancels the older request via its
        // OnWakeWordDetected event. Duplicate completion callbacks cannot speak
        // the same answer over itself while the current request is still active.
        if (response == null || responseInProgress || !IsAssistantVisible) return;
        string input = response.GetTranscription();
        if (string.IsNullOrWhiteSpace(input)) return;
        CancellationTokenSource owner = BeginResponse();
        CancellationToken token = owner.Token;
        bool answerDisplayed = false;
        try
        {
            if (micToggle != null) micToggle.SetIsOnWithoutNotify(false);
            if (wakeWordManager != null) wakeWordManager.DeActivate();
            UpdateResultUI("loading...", null);

            string rawResult = await DescribeGemini(input, token);
            CheckResponseActive(token);
            string spokenText = rawResult;
            string imagePrompt = "";
            int imageTagIndex = rawResult.IndexOf("[IMAGE:", StringComparison.Ordinal);
            if (imageTagIndex != -1)
            {
                spokenText = rawResult.Substring(0, imageTagIndex).Trim();
                int endBracketIndex = rawResult.IndexOf("]", imageTagIndex, StringComparison.Ordinal);
                if (endBracketIndex != -1)
                    imagePrompt = rawResult.Substring(imageTagIndex + 7, endBracketIndex - imageTagIndex - 7).Trim();
            }

            // The readable answer survives image or speech service failures.
            UpdateResultUI(spokenText, null);
            answerDisplayed = true;
            if (!string.IsNullOrEmpty(imagePrompt))
            {
                Texture2D image = null;
                try
                {
                    image = await FetchImageFromWeb(imagePrompt, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    Debug.LogWarning("[AIManager] Answer illustration unavailable (" + error.GetType().Name + ").");
                }
                try
                {
                    CheckResponseActive(token);
                    if (responsePicture != null) Destroy(responsePicture);
                    responsePicture = image;
                    image = null; // Ownership transfers only while this request is current.
                    UpdateResultUI(spokenText, responsePicture);
                }
                finally
                {
                    if (image != null) Destroy(image);
                }
            }

            if (ttsSpeaker != null && !string.IsNullOrWhiteSpace(spokenText))
            {
                responseSpeech = new BufferedAssistantSpeech(ttsSpeaker);
                await responseSpeech.SpeakAsync(spokenText, () => CheckResponseActive(token));
            }
            CheckResponseActive(token);
            // Never listen to the Assistant's own audio. This is deliberately
            // after successful playback, not immediately after queuing speech.
            if (activateVoiceAfterResponse && wakeWordManager != null)
                wakeWordManager.Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && this != null && IsAssistantVisible)
            {
                if (aiResponseText != null)
                    aiResponseText.text = answerDisplayed
                        ? aiResponseText.text + "\n\nVoice playback is unavailable. You can read the response or ask again."
                        : "The response could not be completed. Please try again.";
                Debug.LogWarning("[AIManager] Assistant request stopped (" + error.GetType().Name + ").");
            }
        }
        finally
        {
            EndResponse(owner);
        }
    }

    private async Task<Texture2D> FetchImageFromWeb(string prompt, CancellationToken token)
    {
        string encodedPrompt = UnityWebRequest.EscapeURL(prompt);

        // FIX 1: Change 'piprop=original' to 'pithumbsize=1000' to force Wikipedia to convert SVGs to PNGs
        string searchUrl = $"https://en.wikipedia.org/w/api.php?action=query&generator=search&gsrsearch={encodedPrompt}&gsrlimit=1&prop=pageimages&pithumbsize=1000&format=json";

        string imageUrl = "";

        using (UnityWebRequest searchRequest = UnityWebRequest.Get(searchUrl))
        {
            searchRequest.timeout = 8;
            searchRequest.SetRequestHeader("User-Agent", "XR_AI_Assistant_Project/1.0");
            await SendRequest(searchRequest, token);

            if (searchRequest.result == UnityWebRequest.Result.Success)
            {
                string jsonResponse = searchRequest.downloadHandler.text;

                // FIX 2: Look for the 'thumbnail' tag instead of the 'original' tag
                int sourceIndex = jsonResponse.IndexOf("\"thumbnail\":{\"source\":\"");
                if (sourceIndex != -1)
                {
                    int startIndex = sourceIndex + 23; // Adjusted offset for the word 'thumbnail'
                    int endIndex = jsonResponse.IndexOf("\"", startIndex);
                    if (endIndex > startIndex)
                        imageUrl = jsonResponse.Substring(startIndex, endIndex - startIndex);
                }
            }
            else
            {
                Debug.LogError("Image search failed: " + searchRequest.error);
                return null;
            }
        }

        if (string.IsNullOrEmpty(imageUrl))
        {
            Debug.LogWarning("No related image found on the web for: " + prompt);
            return null;
        }

        using (UnityWebRequest imageRequest = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            imageRequest.timeout = 8;
            await SendRequest(imageRequest, token);

            if (imageRequest.result == UnityWebRequest.Result.Success)
            {
                Texture2D downloadedTex = DownloadHandlerTexture.GetContent(imageRequest);
                downloadedTex.filterMode = FilterMode.Bilinear;
                downloadedTex.anisoLevel = 8;
                downloadedTex.Apply();

                return downloadedTex;
            }
            else
            {
                Debug.LogError("Failed to download image from URL: " + imageRequest.error);
                return null;
            }
        }
    }

    // --- NEW METHOD: Scans raw pixels aggressively for a QR Code ---
    private string DecodeQRCode(Color32[] pixels, int width, int height)
    {
        try
        {
            var barcodeReader = new BarcodeReader
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
                }
            };

            var result = barcodeReader.Decode(pixels, width, height);

            if (result != null)
            {
                return result.Text;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("QR Scanning encountered an error: " + e.Message);
        }

        return null;
    }

    public void ResetUI()
    {
        CancelCurrentResponse();
        if (describePicture != null) describePicture.enabled = false;
        if (aiResponseText != null) aiResponseText.text = "";
        if (wakeWordManager != null && wakeWordManager.transcriptionText != null)
            wakeWordManager.transcriptionText.text = "";
    }

    public void ClearMemory()
    {
        CancelCurrentResponse();
        conversationHistory.Clear();
        Debug.Log("AI Memory Wiped.");
    }

    public void UpdateResultUI(string resultText, Texture2D resultTexture)
    {
        if (aiResponseText != null) aiResponseText.text = resultText;
        if (describePicture == null) return;

        if (resultTexture)
        {
            describePicture.enabled = true;
            describePicture.texture = resultTexture;

            AspectRatioFitter fitter = describePicture.GetComponent<AspectRatioFitter>();
            if (fitter != null) Destroy(fitter);

            RectTransform rectTransform = describePicture.GetComponent<RectTransform>();
            float imageAspect = (float)resultTexture.width / resultTexture.height;
            float uiAspect = rectTransform.rect.width / rectTransform.rect.height;

            if (imageAspect > uiAspect)
            {
                float cropWidth = uiAspect / imageAspect;
                describePicture.uvRect = new Rect((1f - cropWidth) / 2f, 0f, cropWidth, 1f);
            }
            else
            {
                float cropHeight = imageAspect / uiAspect;
                describePicture.uvRect = new Rect(0f, (1f - cropHeight) / 2f, 1f, cropHeight);
            }
        }
        else
        {
            describePicture.enabled = false;
        }
    }

    public async Task<string> AskGemini(string input)
    {
        return await DescribeGemini(input);
    }

    private async Task<bool> WaitForCameraFrame(CancellationToken token)
    {
        // A running component can retain its last texture after the stream stalls.
        // Require a new SDK frame, with a bounded wait for camera startup.
        float deadline = Time.realtimeSinceStartup + 2f;
        while (this != null && isActiveAndEnabled && Application.isPlaying &&
               passthroughCamera != null && passthroughCamera.isActiveAndEnabled)
        {
            CheckResponseActive(token);
            if (passthroughCamera.IsPlaying && passthroughCamera.IsUpdatedThisFrame)
            {
                return true;
            }
            if (Time.realtimeSinceStartup >= deadline) return false;
            await Task.Yield();
        }
        return false;
    }

    private Texture2D CopyCameraPicture(Texture cameraTexture)
    {
        if (cameraTexture == null || cameraTexture.width <= 0 || cameraTexture.height <= 0)
            return null;

        int width = cameraTexture.width;
        int height = cameraTexture.height;
        if (cameraPicture == null || cameraPicture.width != width || cameraPicture.height != height)
        {
            if (cameraPicture != null) Destroy(cameraPicture);
            cameraPicture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        RenderTexture previousRT = RenderTexture.active;
        RenderTexture tempRT = null;
        try
        {
            tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(cameraTexture, tempRT);
            RenderTexture.active = tempRT;
            cameraPicture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            cameraPicture.Apply();
            return cameraPicture;
        }
        finally
        {
            RenderTexture.active = previousRT;
            if (tempRT != null) RenderTexture.ReleaseTemporary(tempRT);
        }
    }

    private async Task<RequestVision> PrepareRequestVision(CancellationToken token)
    {
        var vision = new RequestVision();
        // Treat an initialized XR session as live even if the headset sleeps or
        // disconnects. A failed Link camera must not silently become a test image.
        bool requiresLiveCamera = !Application.isEditor || UnityEngine.XR.XRSettings.enabled ||
                                  UnityEngine.XR.XRSettings.isDeviceActive ||
                                  (passthroughCamera != null && passthroughCamera.IsPlaying);
        Texture2D requestPicture = null;
        try
        {
            if (requiresLiveCamera)
            {
                if (await WaitForCameraFrame(token))
                    requestPicture = CopyCameraPicture(passthroughCamera.GetTexture());
            }
            else
            {
                requestPicture = debugPicture;
            }

            if (requestPicture == null) return vision;

            // Complete readback/encoding before any further await. The request
            // keeps its own bytes and QR result, never a cached texture reference.
            Color32[] pixels = requestPicture.GetPixels32();
            byte[] imageBytes = requestPicture.EncodeToJPG();
            if (imageBytes == null || imageBytes.Length == 0) return vision;
            vision.base64Image = Convert.ToBase64String(imageBytes);
            vision.qrData = DecodeQRCode(pixels, requestPicture.width, requestPicture.height);
            vision.note = requiresLiveCamera ? "" : " [CAMERA STATUS: The attached image is a desktop test image, not a live view of the user's surroundings.]";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            // Keep failed readback/encoding from sending a partial or older image.
            vision = new RequestVision();
            Debug.LogWarning("[AIManager] Camera image preparation failed (" + e.GetType().Name + "). Sending text without an image.");
        }
        return vision;
    }

    private void OnDestroy()
    {
        CancelCurrentResponse();
        if (wakeWordManager != null)
        {
            wakeWordManager.OnResponseDetected.RemoveListener(HandleResponse);
            wakeWordManager.OnWakeWordDetected.RemoveListener(CancelCurrentResponse);
        }
        if (cameraPicture != null) Destroy(cameraPicture);
        if (responsePicture != null) Destroy(responsePicture);
    }

    public async Task<string> DescribeGemini(string input)
    {
        CancellationTokenSource owner = BeginResponse();
        try
        {
            return await DescribeGemini(input, owner.Token);
        }
        catch (OperationCanceledException)
        {
            return "Could not complete the request because the Assistant stopped.";
        }
        finally
        {
            EndResponse(owner);
        }
    }

    private async Task<string> DescribeGemini(string input, CancellationToken token)
    {
        CheckResponseActive(token);
        RequestVision vision = await PrepareRequestVision(token);
        CheckResponseActive(token);

        string base64Image = vision.base64Image;
        string cameraDiagnosticNote = vision.note;
        if (!string.IsNullOrEmpty(vision.qrData))
        {
            Debug.Log("QR Code Detected: " + vision.qrData);
            input += $" \n[IMPORTANT SYSTEM COMMAND: The user is looking at a QR code containing this exact URL/Data: '{vision.qrData}'. You MUST use your Google Search tool to look up this exact link, find out the specific title of the webpage or video, and tell the user what it is! Do NOT guess.]";
        }

        // --- STEP 4: Build JSON Payload Safely ---
        string safeInput = EscapeString(input + cameraDiagnosticNote);

        string systemInstructionJson = string.IsNullOrEmpty(systemInstructions) ? "" : $@"
        ""system_instruction"": {{
            ""parts"": [
                {{""text"": ""{EscapeString(systemInstructions)}""}}
            ]
        }},";

        string toolsJson = enableGoogleSearch ? @", ""tools"": [{""googleSearch"": {} }]" : "";

        // Construct the image part as a separate JSON object if it exists
        string imagePart = "";
        if (!string.IsNullOrEmpty(base64Image))
        {
            imagePart = $@", {{ ""inline_data"": {{ ""mime_type"": ""image/jpeg"", ""data"": ""{base64Image}"" }} }}";
        }

        // Build the current user message with text and (optionally) the image part
        string currentUserMsgWithImage = $@"{{
            ""role"": ""user"", 
            ""parts"": [
                {{ ""text"": ""User: {safeInput}"" }} {imagePart}
            ]
        }}";

        // Memory bank logic
        string memoryPrefix = string.IsNullOrEmpty(base64Image) ? "" : "[User sent an image] ";
        string currentUserMsgTextOnly = $@"{{""role"": ""user"", ""parts"": [{{""text"": ""{memoryPrefix}User: {safeInput}""}}]}}";

        string historyJson = string.Join(",", conversationHistory);
        if (!string.IsNullOrEmpty(historyJson)) historyJson += ",";

        string jsonPayload = $@"{{
            {systemInstructionJson}
            ""contents"": [
                {historyJson}
                {currentUserMsgWithImage}
            ]{toolsJson}
        }}";

        string responseText = await SendGeminiRequest(jsonPayload, token);
        CheckResponseActive(token);

        if (!responseText.StartsWith("Sorry") && !responseText.StartsWith("Could not") && !responseText.StartsWith("API Key"))
        {
            conversationHistory.Add(currentUserMsgTextOnly);
            conversationHistory.Add($@"{{""role"": ""model"", ""parts"": [{{""text"": ""{EscapeString(responseText)}""}}]}}");
        }

        return responseText;
    }

    private async Task<string> SendGeminiRequest(string jsonPayload, CancellationToken token, int retryCount = 0)
    {
        CheckResponseActive(token);
        if (string.IsNullOrEmpty(geminiApiKey))
        {
            Debug.LogError("Gemini API Key is missing! Please assign it in the inspector.");
            return "API Key is missing.";
        }

        if (fallbackModels == null || fallbackModels.Length == 0)
            return "Could not send the request because no Assistant model is configured.";
        currentModelIndex %= fallbackModels.Length;
        string currentModel = fallbackModels[currentModelIndex];
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:generateContent?key={geminiApiKey}";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            request.timeout = 30;
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            await SendRequest(request, token);

            if (request.result == UnityWebRequest.Result.Success)
            {
                GeminiResponseData response = JsonUtility.FromJson<GeminiResponseData>(request.downloadHandler.text);
                if (response?.candidates != null && response.candidates.Length > 0 &&
                    response.candidates[0]?.content?.parts != null)
                {
                    var answer = new StringBuilder();
                    foreach (GeminiPart part in response.candidates[0].content.parts)
                        if (!string.IsNullOrEmpty(part?.text)) answer.Append(part.text);
                    if (answer.Length > 0) return answer.ToString();
                }
                return "Could not parse the response from Gemini.";
            }
            else
            {
                if (request.responseCode == 429 || request.responseCode == 404 || request.responseCode == 503 || request.responseCode == 500)
                {
                    if (retryCount < fallbackModels.Length - 1)
                    {
                        string oldModel = fallbackModels[currentModelIndex];
                        currentModelIndex = (currentModelIndex + 1) % fallbackModels.Length;
                        Debug.LogWarning($"[AIManager] {oldModel} failed (Error {request.responseCode}). Automatically swapping to {fallbackModels[currentModelIndex]} and retrying...");
                        return await SendGeminiRequest(jsonPayload, token, retryCount + 1);
                    }
                    else
                    {
                        Debug.LogError("[AIManager] All fallback models have failed or exhausted their quotas!");
                        return "Sorry, I have run out of daily requests across all of my available models.";
                    }
                }

                // Provider bodies and request URLs may contain private request
                // data. Keep diagnostics to the status rather than echoing them.
                Debug.LogWarning($"[AIManager] Gemini request failed (HTTP {request.responseCode}).");
                return "Sorry, I encountered a network error.";
            }
        }
    }

    private CancellationTokenSource BeginResponse()
    {
        CancelCurrentResponse();
        responseCancellation = new CancellationTokenSource();
        responseInProgress = true;
        return responseCancellation;
    }

    private void EndResponse(CancellationTokenSource owner)
    {
        if (!ReferenceEquals(responseCancellation, owner)) return;
        responseSpeech?.Stop();
        responseSpeech = null;
        responseInProgress = false;
        responseCancellation = null;
        owner.Dispose();
    }

    private void CancelCurrentResponse()
    {
        CancellationTokenSource previous = responseCancellation;
        responseCancellation = null;
        responseInProgress = false;
        previous?.Cancel();
        responseSpeech?.Stop();
        responseSpeech = null;
        previous?.Dispose();
    }

    private void CheckResponseActive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (this == null || !isActiveAndEnabled || !Application.isPlaying || !IsAssistantVisible)
            throw new OperationCanceledException();
    }

    private async Task SendRequest(UnityWebRequest request, CancellationToken token)
    {
        CheckResponseActive(token);
        var operation = request.SendWebRequest();
        float deadline = Time.realtimeSinceStartup + request.timeout + 2f;
        try
        {
            while (!operation.isDone)
            {
                CheckResponseActive(token);
                if (Time.realtimeSinceStartup >= deadline)
                    throw new TimeoutException("Assistant web request timed out.");
                await Task.Yield();
            }
            CheckResponseActive(token);
        }
        catch
        {
            request.Abort();
            throw;
        }
    }

    private void Update()
    {
        if (responseInProgress && !IsAssistantVisible) CancelCurrentResponse();
    }

    private void OnDisable() => CancelCurrentResponse();

    private void OnApplicationPause(bool paused)
    {
        if (paused) CancelCurrentResponse();
    }

    private string EscapeString(string str)
    {
        if (string.IsNullOrEmpty(str)) return "";
        return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}

[Serializable]
public class GeminiResponseData
{
    public GeminiCandidate[] candidates;
}

[Serializable]
public class GeminiCandidate
{
    public GeminiContent content;
}

[Serializable]
public class GeminiContent
{
    public GeminiPart[] parts;
}

[Serializable]
public class GeminiPart
{
    public string text;
}
