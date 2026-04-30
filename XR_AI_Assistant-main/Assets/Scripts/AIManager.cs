using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Text;
using System.Collections.Generic;
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

    public Texture2D debugPicture;

    public TTSSpeaker ttsSpeaker;

    private Texture2D picture;

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

        wakeWordManager.OnResponseDetected.AddListener(HandleResponse);
        aiResponseText.text = "";
    }

    public async void HandleResponse(WitResponseNode response)
    {
        micToggle.SetIsOnWithoutNotify(false);

        aiResponseText.text = "loading...";
        describePicture.enabled = false;

        // Get the raw response from Gemini
        string rawResult = await DescribeGemini(response.GetTranscription());

        // --- Parse the Response for the [IMAGE: ] tag ---
        string spokenText = rawResult;
        string imagePrompt = "";

        int imageTagIndex = rawResult.IndexOf("[IMAGE:");
        if (imageTagIndex != -1)
        {
            spokenText = rawResult.Substring(0, imageTagIndex).Trim();

            int endBracketIndex = rawResult.IndexOf("]", imageTagIndex);
            if (endBracketIndex != -1)
            {
                imagePrompt = rawResult.Substring(imageTagIndex + 7, endBracketIndex - imageTagIndex - 7).Trim();
            }
        }

        Debug.Log("GEMINI SPOKEN TEXT: " + spokenText);
        Debug.Log("GEMINI REQUESTED IMAGE: " + imagePrompt);

        // --- Fetch the relevant image from the web ---
        Texture2D dynamicImage = null;
        if (!string.IsNullOrEmpty(imagePrompt))
        {
            aiResponseText.text = "loading image...";
            dynamicImage = await FetchImageFromWeb(imagePrompt);
        }

        // Send the downloaded web image to the UI
        UpdateResultUI(spokenText, dynamicImage);

        // --- RESTORED: Tell the TTS to actually speak! ---
        if (ttsSpeaker != null)
        {
            ttsSpeaker.Speak(spokenText);
        }

        // --- RESTORED: Turn the mic back on automatically (if enabled) ---
        if (activateVoiceAfterResponse && wakeWordManager != null)
        {
            wakeWordManager.Activate();
        }

        // Send the downloaded web image to the UI (instead of the camera debug picture)
        UpdateResultUI(spokenText, dynamicImage);
    }

    private async Task<Texture2D> FetchImageFromWeb(string prompt)
    {
        string encodedPrompt = UnityWebRequest.EscapeURL(prompt);

        // FIX 1: Change 'piprop=original' to 'pithumbsize=1000' to force Wikipedia to convert SVGs to PNGs
        string searchUrl = $"https://en.wikipedia.org/w/api.php?action=query&generator=search&gsrsearch={encodedPrompt}&gsrlimit=1&prop=pageimages&pithumbsize=1000&format=json";

        string imageUrl = "";

        using (UnityWebRequest searchRequest = UnityWebRequest.Get(searchUrl))
        {
            searchRequest.SetRequestHeader("User-Agent", "XR_AI_Assistant_Project/1.0");

            var operation = searchRequest.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

            if (searchRequest.result == UnityWebRequest.Result.Success)
            {
                string jsonResponse = searchRequest.downloadHandler.text;

                // FIX 2: Look for the 'thumbnail' tag instead of the 'original' tag
                int sourceIndex = jsonResponse.IndexOf("\"thumbnail\":{\"source\":\"");
                if (sourceIndex != -1)
                {
                    int startIndex = sourceIndex + 23; // Adjusted offset for the word 'thumbnail'
                    int endIndex = jsonResponse.IndexOf("\"", startIndex);
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
            var operation = imageRequest.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

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
        describePicture.enabled = false;
        aiResponseText.text = "";
        wakeWordManager.transcriptionText.text = "";
    }

    public void ClearMemory()
    {
        conversationHistory.Clear();
        Debug.Log("AI Memory Wiped.");
    }

    public void UpdateResultUI(string resultText, Texture2D resultTexture)
    {
        aiResponseText.text = resultText;

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

    public async Task<string> DescribeGemini(string input)
    {
        Color32[] pixels = null;
        int imgWidth = 0;
        int imgHeight = 0;
        string cameraDiagnosticNote = "";

        if (Application.isEditor && debugPicture != null)
        {
            picture = debugPicture;
            imgWidth = picture.width;
            imgHeight = picture.height;

            try { pixels = picture.GetPixels32(); }
            catch (UnityException e) { Debug.LogError("Check 'Read/Write' in debugPicture! " + e.Message); }
        }
        // THE FIX: Using the correct modern Meta API methods
        else if (passthroughCamera != null && passthroughCamera.IsPlaying)
        {
            Texture camTex = passthroughCamera.GetTexture();
            if (camTex != null)
            {
                imgWidth = camTex.width;
                imgHeight = camTex.height;

                if (picture == null || picture.width != imgWidth || picture.height != imgHeight)
                {
                    picture = new Texture2D(imgWidth, imgHeight, TextureFormat.RGBA32, false);
                }

                // Safely extract the GPU texture to readable CPU pixels
                RenderTexture tempRT = RenderTexture.GetTemporary(imgWidth, imgHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(camTex, tempRT);
                RenderTexture previousRT = RenderTexture.active;
                RenderTexture.active = tempRT;

                picture.ReadPixels(new Rect(0, 0, imgWidth, imgHeight), 0, 0);
                picture.Apply();

                RenderTexture.active = previousRT;
                RenderTexture.ReleaseTemporary(tempRT);

                pixels = picture.GetPixels32();
            }
        }
        else
        {
            if (!Application.isEditor)
            {
                cameraDiagnosticNote = " [SYSTEM NOTE: The camera feed is currently unavailable. If the user asks what you see, explicitly mention that your camera seems to not be showing anything.]";
            }
        }

        if (pixels != null)
        {
            string qrData = DecodeQRCode(pixels, imgWidth, imgHeight);
            if (!string.IsNullOrEmpty(qrData))
            {
                Debug.Log("QR Code Detected: " + qrData);
                input += $" \n[IMPORTANT SYSTEM COMMAND: The user is looking at a QR code containing this exact URL/Data: '{qrData}'. You MUST use your Google Search tool to look up this exact link, find out the specific title of the webpage or video, and tell the user what it is! Do NOT guess.]";
            }
        }

        string base64Image = "";
        if (picture != null)
        {
            try
            {
                byte[] imageBytes = picture.EncodeToJPG();
                base64Image = Convert.ToBase64String(imageBytes);
            }
            catch (Exception e) { Debug.LogError("Failed to convert picture: " + e.Message); }
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

        string responseText = await SendGeminiRequest(jsonPayload);

        if (!responseText.StartsWith("Sorry") && !responseText.StartsWith("Could not") && !responseText.StartsWith("API Key"))
        {
            conversationHistory.Add(currentUserMsgTextOnly);
            conversationHistory.Add($@"{{""role"": ""model"", ""parts"": [{{""text"": ""{EscapeString(responseText)}""}}]}}");
        }

        return responseText;
    }

    private async Task<string> SendGeminiRequest(string jsonPayload, int retryCount = 0)
    {
        if (string.IsNullOrEmpty(geminiApiKey))
        {
            Debug.LogError("Gemini API Key is missing! Please assign it in the inspector.");
            return "API Key is missing.";
        }

        string currentModel = fallbackModels[currentModelIndex];
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:generateContent?key={geminiApiKey}";

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                GeminiResponseData response = JsonUtility.FromJson<GeminiResponseData>(request.downloadHandler.text);
                if (response != null && response.candidates != null && response.candidates.Length > 0)
                {
                    return response.candidates[0].content.parts[0].text;
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
                        return await SendGeminiRequest(jsonPayload, retryCount + 1);
                    }
                    else
                    {
                        Debug.LogError("[AIManager] All fallback models have failed or exhausted their quotas!");
                        return "Sorry, I have run out of daily requests across all of my available models.";
                    }
                }

                Debug.LogError($"Gemini API Error: {request.error}\nResponse: {request.downloadHandler.text}");
                return "Sorry, I encountered a network error.";
            }
        }
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