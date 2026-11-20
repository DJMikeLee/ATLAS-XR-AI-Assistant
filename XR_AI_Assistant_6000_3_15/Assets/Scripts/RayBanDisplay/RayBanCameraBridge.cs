using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using PassthroughCameraSamples;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Connects the compact Camera/Photos screens to the existing camera and session
/// album. Captures raw camera pixels only; it never saves or uploads image files.
/// </summary>
[DisallowMultipleComponent]
public sealed class RayBanCameraBridge : MonoBehaviour
{
    public PassthroughCameraPictureTaker pictureTaker;
    public PassthroughCameraAccess cameraAccess;

    private const float FreshFrameSeconds = 1f;
    private const float CaptureTimeoutSeconds = 2f;
    private const int MaximumCaptureDimension = 2048;
    private static readonly Texture2D[] EmptyPhotos = Array.Empty<Texture2D>();
    private Texture preview;
    private DateTime observedTimestamp;
    private float lastFrameTime = float.NegativeInfinity;
    private float openedAt;
    private float capturedAt = float.NegativeInfinity;
    private bool cameraVisible;
    private bool applicationPaused;
    private bool activatedCameraObject;
    private bool enabledCameraComponent;
    private Coroutine captureRoutine;
    private int captureGeneration;
    private bool capturing;
    private string captureFailure;
    private float captureDeadline;
    // The legacy camera page can remain inactive for the entire session, in
    // which case Unity does not dispatch its OnDestroy. Keep a cleanup reference
    // to bridge captures as well; no extra pixel copies are retained here.
    private readonly HashSet<Texture2D> bridgePhotos = new HashSet<Texture2D>();

    public IReadOnlyList<Texture2D> Photos => pictureTaker != null ? pictureTaker.CapturedPictures : EmptyPhotos;
    public int PhotoCapacity => PassthroughCameraPictureTaker.PhotoCapacity;
    public int CapturedCount => Photos.Count;
    public bool IsCapturing => capturing;
    public Texture PreviewTexture => HasFreshFrame ? preview : null;
    public bool CanCapture => HasFreshFrame && !capturing && pictureTaker != null && CapturedCount < PhotoCapacity;

    private bool HasFreshFrame => Application.isPlaying && isActiveAndEnabled && cameraVisible &&
        !applicationPaused && cameraAccess != null && cameraAccess.isActiveAndEnabled && cameraAccess.IsPlaying &&
        preview != null && Time.realtimeSinceStartup - lastFrameTime <= FreshFrameSeconds;

    public string Status
    {
        get
        {
            if (!Application.isPlaying) return "Enter Play Mode to use the camera";
            if (!cameraVisible || applicationPaused) return "Camera paused";
            if (cameraAccess == null || pictureTaker == null) return "Camera is not connected";
            if (capturing) return "Capturing…";
            if (!string.IsNullOrEmpty(captureFailure)) return captureFailure;
            if (CapturedCount >= PhotoCapacity) return "Session album full · " + PhotoCapacity + " photos";
            if (HasFreshFrame)
                return Time.realtimeSinceStartup - capturedAt < 2f ? "Captured · saved for this session" : "Live camera · session photos";
            return Time.realtimeSinceStartup - openedAt < CaptureTimeoutSeconds
                ? "Waiting for live camera…" : "Camera unavailable · check headset and permissions";
        }
    }

    public void SetCameraVisible(bool visible)
    {
        if (!Application.isPlaying) return;
        if (cameraVisible == visible) return;
        cameraVisible = visible;
        CancelCapture();
        ClearPreview();
        captureFailure = null;
        if (visible && !applicationPaused)
        {
            openedAt = Time.realtimeSinceStartup;
            AcquireCamera();
        }
        else ReleaseCamera();
    }

    private void AcquireCamera()
    {
        if (cameraAccess == null || !isActiveAndEnabled) return;
        // The shell suspends this existing raw-camera root in preview mode.
        // Never activate a parent rig or create another camera service.
        if (!cameraAccess.enabled)
        {
            enabledCameraComponent = true;
            cameraAccess.enabled = true;
        }
        if (!cameraAccess.gameObject.activeSelf)
        {
            activatedCameraObject = true;
            cameraAccess.gameObject.SetActive(true);
        }
    }

    private void ReleaseCamera()
    {
        if (cameraAccess != null)
        {
            if (enabledCameraComponent) cameraAccess.enabled = false;
            if (activatedCameraObject) cameraAccess.gameObject.SetActive(false);
        }
        enabledCameraComponent = activatedCameraObject = false;
    }

    private void Update()
    {
        if (capturing && Time.realtimeSinceStartup >= captureDeadline)
        {
            CancelCapture();
            captureFailure = "Capture timed out · try again";
        }
        if (!cameraVisible || applicationPaused || cameraAccess == null || !cameraAccess.isActiveAndEnabled || !cameraAccess.IsPlaying)
        {
            ClearPreview();
            return;
        }
        // A playing camera can retain its last texture after the headset sleeps.
        // Only advancing sensor timestamps extend freshness, not repeated reads.
        if (cameraAccess.IsUpdatedThisFrame && cameraAccess.Timestamp != observedTimestamp)
        {
            observedTimestamp = cameraAccess.Timestamp;
            Texture current = cameraAccess.GetTexture();
            if (current != null && current.width > 16 && current.height > 16)
            {
                preview = current;
                lastFrameTime = Time.realtimeSinceStartup;
            }
        }
        if (Time.realtimeSinceStartup - lastFrameTime > FreshFrameSeconds) preview = null;
    }

    private void ClearPreview()
    {
        preview = null;
        observedTimestamp = default;
        lastFrameTime = float.NegativeInfinity;
    }

    public void Capture(Rect uvCrop)
    {
        if (capturing) return;
        captureFailure = null;
        if (!CanCapture)
        {
            if (CapturedCount < PhotoCapacity) captureFailure = "No fresh camera frame · try again";
            return;
        }
        if (!TryClampCrop(uvCrop, out Rect crop))
        {
            captureFailure = "Camera framing is unavailable";
            return;
        }
        capturing = true;
        captureDeadline = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
        int generation = ++captureGeneration;
        captureRoutine = StartCoroutine(CaptureFreshFrame(crop, generation, cameraAccess.Timestamp));
    }

    private IEnumerator CaptureFreshFrame(Rect crop, int generation, DateTime requestedAfter)
    {
        // Require the sensor stream to advance after the shutter press. The SDK
        // updates its GPU texture on the render thread, so neither path claims
        // that the returned pixels have an exact shutter-time/frame identity.
        while (generation == captureGeneration && cameraVisible && !applicationPaused &&
               Time.realtimeSinceStartup < captureDeadline)
        {
            if (HasFreshFrame && cameraAccess.IsUpdatedThisFrame && cameraAccess.Timestamp != requestedAfter)
            {
                Texture source = cameraAccess.GetTexture();
                if (source != null)
                {
                    if (!SystemInfo.supportsAsyncGPUReadback)
                    {
                        // Let a render frame pass before the blocking fallback.
                        // This remains a fresh-feed photograph, not a screenshot
                        // of the composited headset display.
                        yield return null;
                        if (generation != captureGeneration || !HasFreshFrame ||
                            Time.realtimeSinceStartup >= captureDeadline) break;
                        source = cameraAccess.GetTexture();
                        if (source == null) break;
                    }
                    QueueReadback(source, crop, generation);
                    captureRoutine = null;
                    yield break;
                }
            }
            yield return null;
        }
        if (generation == captureGeneration)
        {
            capturing = false;
            captureFailure = "No fresh camera frame · try again";
        }
        captureRoutine = null;
    }

    private void QueueReadback(Texture source, Rect crop, int generation)
    {
        if (source.width > MaximumCaptureDimension || source.height > MaximumCaptureDimension)
        {
            capturing = false;
            captureFailure = "Camera resolution exceeds the capture limit";
            return;
        }
        int x = Mathf.Clamp(Mathf.FloorToInt(crop.xMin * source.width), 0, source.width - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(crop.yMin * source.height), 0, source.height - 1);
        int width = Mathf.Clamp(Mathf.CeilToInt(crop.xMax * source.width) - x, 1, source.width - x);
        int height = Mathf.Clamp(Mathf.CeilToInt(crop.yMax * source.height) - y, 1, source.height - y);
        try
        {
            if (SystemInfo.supportsAsyncGPUReadback)
                AsyncGPUReadback.Request(source, 0, x, width, y, height, 0, 1, TextureFormat.RGBA32,
                    request => CompleteReadback(request, width, height, generation));
            else
                CompleteSynchronousReadback(source, x, y, width, height, generation);
        }
        catch (Exception exception)
        {
            capturing = false;
            captureFailure = "Capture failed · try again";
            Debug.LogWarning("[RayBanCameraBridge] Camera readback failed (" + exception.GetType().Name + ").");
        }
    }

    private void CompleteReadback(AsyncGPUReadbackRequest request, int width, int height, int generation)
    {
        if (this == null || generation != captureGeneration || !Application.isPlaying || !isActiveAndEnabled ||
            !cameraVisible || applicationPaused) return;
        capturing = false;
        if (request.hasError || Time.realtimeSinceStartup >= captureDeadline || !HasFreshFrame)
        {
            captureFailure = "Capture failed · try again";
            return;
        }
        Texture2D photo = null;
        try
        {
            photo = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Camera photo " + DateTime.Now.ToString("HH:mm:ss"),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            photo.LoadRawTextureData(request.GetData<byte>());
            photo.Apply(false, true);
            StorePhoto(photo);
            photo = null; // Session store owns this texture until scene destruction.
        }
        catch (Exception exception)
        {
            if (photo != null) Destroy(photo);
            captureFailure = "Capture failed · try again";
            Debug.LogWarning("[RayBanCameraBridge] Could not store camera photo (" + exception.GetType().Name + ").");
        }
    }

    private void CompleteSynchronousReadback(Texture source, int x, int y, int width, int height, int generation)
    {
        Texture2D photo = null;
        RenderTexture previousTarget = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        RenderTexture temporary = null;
        try
        {
            // Some graphics backends do not support async readback. This copy
            // runs once per shutter press, uses at most 2048x2048 temporary pixels,
            // and retains no render target. It can briefly stall the render thread.
            temporary = RenderTexture.GetTemporary(source.width, source.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            photo = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Camera photo " + DateTime.Now.ToString("HH:mm:ss"),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            // ReadPixels and RawImage UVs both use bottom-left texture space.
            // Do not unconditionally flip based on the display graphics backend.
            photo.ReadPixels(new Rect(x, y, width, height), 0, 0, false);
            photo.Apply(false, true);
            if (generation != captureGeneration || !HasFreshFrame || Time.realtimeSinceStartup >= captureDeadline)
            {
                captureFailure = "Capture timed out · try again";
                return;
            }
            StorePhoto(photo);
            photo = null;
        }
        finally
        {
            capturing = false;
            RenderTexture.active = previousTarget;
            GL.sRGBWrite = previousSrgbWrite;
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            if (photo != null) Destroy(photo);
        }
    }

    private void StorePhoto(Texture2D photo)
    {
        if (pictureTaker == null || !pictureTaker.TryAddCapturedPicture(photo))
        {
            Destroy(photo);
            captureFailure = "Session album full";
            return;
        }
        bridgePhotos.RemoveWhere(texture => texture == null);
        bridgePhotos.Add(photo);
        capturedAt = Time.realtimeSinceStartup;
        captureFailure = null;
    }

    private static bool TryClampCrop(Rect requested, out Rect crop)
    {
        crop = default;
        if (!float.IsFinite(requested.x) || !float.IsFinite(requested.y) ||
            !float.IsFinite(requested.width) || !float.IsFinite(requested.height) ||
            requested.width <= 0 || requested.height <= 0) return false;
        float left = Mathf.Clamp01(requested.xMin), bottom = Mathf.Clamp01(requested.yMin);
        float right = Mathf.Clamp01(requested.xMax), top = Mathf.Clamp01(requested.yMax);
        if (right <= left || top <= bottom) return false;
        crop = Rect.MinMaxRect(left, bottom, right, top);
        return true;
    }

    private void CancelCapture()
    {
        captureGeneration++;
        if (captureRoutine != null) StopCoroutine(captureRoutine);
        captureRoutine = null;
        capturing = false;
    }

    private void OnApplicationPause(bool paused)
    {
        applicationPaused = paused;
        CancelCapture();
        ClearPreview();
        if (paused) ReleaseCamera();
        else if (cameraVisible)
        {
            openedAt = Time.realtimeSinceStartup;
            AcquireCamera();
        }
    }

    private void OnDisable()
    {
        cameraVisible = false;
        CancelCapture();
        ClearPreview();
        ReleaseCamera();
    }

    private void OnDestroy()
    {
        foreach (Texture2D photo in bridgePhotos)
            if (photo != null) Destroy(photo);
        bridgePhotos.Clear();
    }
}
