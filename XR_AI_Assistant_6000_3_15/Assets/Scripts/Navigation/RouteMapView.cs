using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>A small, bounded map inside the existing display canvas; no camera or extra canvas.</summary>
[RequireComponent(typeof(RectTransform))]
public sealed class RouteMapView : MonoBehaviour
{
    [Tooltip("HTTPS XYZ PNG tile URL (256/512 pixels). Leave blank in automated tests. Only visible tiles are requested.")]
    public string tileUrlTemplate = "";
    public string userAgent = "XR-AI-Assistant/0.2 (+https://github.com/DJMikeLee/quad-tracker)";
    [Range(3, 19)] public int initialZoom = 17;
    public bool headingUp = true;

    public bool UsingNorthUp => overview || !headingUp || !headingFresh;
    public bool IsOverview => overview;
    public int ZoomLevel => zoom;
    public string StatusMessage { get; private set; } = "Waiting for phone location.";
    public bool TilesUnavailable => tileFailure;
    public int ActiveRequestCount => requests.Count;
    public int CachedTextureCount => cache.Count;
    public int VisibleTileCount => visible.Count;

    private const int TilePixels = 256;
    private const int MaxVisibleTiles = 16;
    private const int MaxTextures = 32;
    private const int MaxConcurrentRequests = 2;
    private const int MaxRoutePoints = 4096;
    private const double MercatorLimit = 85.0511287798066;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private readonly Dictionary<string, TileEntry> cache = new Dictionary<string, TileEntry>();
    private readonly Dictionary<string, UnityWebRequest> requests = new Dictionary<string, UnityWebRequest>();
    private readonly Dictionary<string, float> retryAt = new Dictionary<string, float>();
    private readonly Dictionary<string, TileSpec> visible = new Dictionary<string, TileSpec>();
    private readonly List<TileSpec> candidates = new List<TileSpec>(25);
    private readonly List<string> workKeys = new List<string>(32);
    private readonly List<Vector2> projectedRoute = new List<Vector2>(256);
    private readonly List<RawImage> tiles = new List<RawImage>(MaxVisibleTiles);
    private readonly List<GeoCoordinate> geometry = new List<GeoCoordinate>();
    private RectTransform viewport;
    private RectTransform tileRoot;
    private RouteMapGraphic overlay;
    private Canvas parentCanvas;
    private CanvasGroup[] parentGroups;
    private GeoCoordinate position;
    private bool hasPosition, positionFresh, headingFresh, overview, dirty = true, suspended;
    private bool overviewFitPending = true;
    private float heading;
    private double centreX, centreY;
    private int zoom, lifetime;
    private long useSequence;
    private string configuredTemplate, cacheDirectory;
    private bool tileFailure;

    private sealed class TileEntry { public Texture2D Texture; public long Used; }
    private struct TileSpec { public string Key, Url; public int X, Y, Zoom; public Vector2 Offset; public float Distance; }

    private void Awake()
    {
        viewport = (RectTransform)transform;
        zoom = Mathf.Clamp(initialZoom, 3, 19);
        EnsureVisuals();
    }

    private void OnEnable()
    {
        parentCanvas = GetComponentInParent<Canvas>();
        parentGroups = GetComponentsInParent<CanvasGroup>();
        suspended = false; dirty = true;
    }

    private void OnDisable() { CancelRequests(); suspended = true; }

    private void OnDestroy()
    {
        CancelRequests();
        foreach (TileEntry entry in cache.Values) if (entry.Texture != null) Destroy(entry.Texture);
        cache.Clear();
    }

    private void OnRectTransformDimensionsChange() { overviewFitPending = true; dirty = true; }

    private void LateUpdate()
    {
        if (!IsVisible())
        {
            if (!suspended) { CancelRequests(); suspended = true; }
            return;
        }
        if (suspended) { suspended = false; dirty = true; }
        if (!string.Equals(configuredTemplate, tileUrlTemplate, StringComparison.Ordinal))
        {
            CancelRequests();
            configuredTemplate = tileUrlTemplate;
            cacheDirectory = Path.Combine(Application.persistentDataPath, "MapTileCache", Hash(configuredTemplate ?? ""));
            retryAt.Clear(); tileFailure = false; dirty = true;
        }
        if (dirty) { dirty = false; RefreshView(); }
        PumpRequests();
    }

    public void SetRoute(WalkingRoute route)
    {
        geometry.Clear();
        if (route != null && route.Geometry != null && route.Geometry.Count > 0)
        {
            int count = Math.Min(route.Geometry.Count, MaxRoutePoints);
            for (int i = 0; i < count; i++)
            {
                int source = count == 1 ? 0 : (int)((long)i * (route.Geometry.Count - 1) / (count - 1));
                GeoCoordinate point = route.Geometry[source];
                if (IsValid(point)) geometry.Add(point);
            }
        }
        overviewFitPending = true; tileFailure = false; dirty = true;
    }

    public void SetPosition(GeoCoordinate newPosition, bool fresh, float newHeading, bool freshHeading)
    {
        bool valid = fresh && IsValid(newPosition);
        bool compass = valid && freshHeading && !float.IsNaN(newHeading) && !float.IsInfinity(newHeading);
        if (valid != positionFresh || (valid && !hasPosition) || compass != headingFresh ||
            (valid && (position.Latitude != newPosition.Latitude || position.Longitude != newPosition.Longitude)) ||
            (compass && Mathf.Abs(Mathf.DeltaAngle(heading, newHeading)) >= 0.1f)) dirty = true;
        // PhoneTether clears its numeric fields to zero when disconnected. Preserve the
        // last real fix for a grey paused marker instead of moving the map to (0, 0).
        if (valid) { hasPosition = true; position = newPosition; }
        positionFresh = valid;
        headingFresh = compass; heading = compass ? Mathf.Repeat(newHeading, 360f) : 0f;
    }

    public void SetHeadingUp(bool enabled) { if (headingUp != enabled) { headingUp = enabled; dirty = true; } }
    public void SetOverview(bool enabled)
    {
        if (overview == enabled) return;
        overview = enabled;
        if (enabled) overviewFitPending = true;
        dirty = true;
    }
    public void ZoomIn() { ChangeZoom(1); }
    public void ZoomOut() { ChangeZoom(-1); }

    public void Clear()
    {
        geometry.Clear(); hasPosition = false; positionFresh = false; headingFresh = false;
        overview = false; overviewFitPending = true; tileFailure = false; retryAt.Clear(); visible.Clear();
        CancelRequests(); dirty = true;
        for (int i = 0; i < tiles.Count; i++) tiles[i].gameObject.SetActive(false);
        projectedRoute.Clear();
        if (overlay != null) overlay.SetGeometry(projectedRoute, Vector2.zero, false, false, 0f, Vector2.zero, false);
        StatusMessage = "Waiting for phone location.";
    }

    private void ChangeZoom(int delta)
    {
        // A preview remains a north-up preview when zoomed. Fit once before the
        // first zoom command, then keep its centre and user-selected zoom level.
        if (overview && overviewFitPending && geometry.Count > 0 && viewport != null)
        {
            FitOverview();
            overviewFitPending = false;
        }
        zoom = Mathf.Clamp(zoom + delta, 3, 19); dirty = true;
    }

    private bool IsVisible()
    {
        if (!isActiveAndEnabled || parentCanvas == null || !parentCanvas.isActiveAndEnabled ||
            !parentCanvas.rootCanvas.isActiveAndEnabled) return false;
        if (parentGroups != null)
            for (int i = 0; i < parentGroups.Length; i++)
                if (parentGroups[i] != null && parentGroups[i].alpha <= 0.001f) return false;
        return true;
    }

    private void EnsureVisuals()
    {
        if (GetComponent<RectMask2D>() == null) gameObject.AddComponent<RectMask2D>();
        GameObject background = new GameObject("Map background", typeof(RectTransform), typeof(Image));
        FillRect(background.GetComponent<RectTransform>(), transform);
        Image fill = background.GetComponent<Image>();
        fill.color = new Color32(27, 42, 50, 255); fill.raycastTarget = false;
        tileRoot = new GameObject("Visible street tiles", typeof(RectTransform)).GetComponent<RectTransform>();
        FillRect(tileRoot, transform);
        for (int i = 0; i < MaxVisibleTiles; i++)
        {
            RawImage tile = new GameObject("Tile " + i, typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
            tile.rectTransform.SetParent(tileRoot, false);
            tile.gameObject.layer = tileRoot.gameObject.layer;
            tile.rectTransform.sizeDelta = new Vector2(TilePixels, TilePixels);
            tile.raycastTarget = false; tile.gameObject.SetActive(false); tiles.Add(tile);
        }
        overlay = new GameObject("Route and position", typeof(RectTransform), typeof(RouteMapGraphic)).GetComponent<RouteMapGraphic>();
        FillRect(overlay.rectTransform, transform);
        overlay.raycastTarget = false;
    }

    private static void FillRect(RectTransform child, Transform parent)
    {
        child.SetParent(parent, false); child.anchorMin = Vector2.zero; child.anchorMax = Vector2.one;
        child.gameObject.layer = parent.gameObject.layer;
        child.offsetMin = Vector2.zero; child.offsetMax = Vector2.zero;
    }

    private void RefreshView()
    {
        if (viewport == null || overlay == null) return;
        visible.Clear(); candidates.Clear();
        if (!hasPosition && geometry.Count == 0) { Clear(); dirty = false; return; }
        if (overview && geometry.Count > 0)
        {
            if (overviewFitPending) { FitOverview(); overviewFitPending = false; }
        }
        else
        {
            GeoCoordinate centre = hasPosition ? position : geometry[0];
            WebMercator(centre, out centreX, out centreY);
        }
        float rotation = UsingNorthUp ? 0f : heading;
        tileRoot.localRotation = Quaternion.Euler(0f, 0f, rotation);
        projectedRoute.Clear();
        for (int i = 0; i < geometry.Count; i++)
            projectedRoute.Add(Project(geometry[i], centreX, centreY, zoom, rotation));
        Vector2 marker = hasPosition ? Project(position, centreX, centreY, zoom, rotation) : Vector2.zero;
        Vector2 destination = geometry.Count > 0 ? projectedRoute[projectedRoute.Count - 1] : Vector2.zero;
        overlay.SetGeometry(projectedRoute, marker, hasPosition, positionFresh,
            UsingNorthUp && headingFresh ? -heading : 0f, destination, geometry.Count > 0, headingFresh);

        bool configured = IsTileTemplateValid(tileUrlTemplate) && !string.IsNullOrWhiteSpace(userAgent);
        if (configured) BuildVisibleTiles(rotation);
        int index = 0;
        foreach (TileSpec tile in visible.Values)
        {
            RawImage image = tiles[index++];
            image.rectTransform.anchoredPosition = tile.Offset;
            if (cache.TryGetValue(tile.Key, out TileEntry entry))
            {
                image.texture = entry.Texture; image.color = Color.white; entry.Used = ++useSequence;
            }
            else { image.texture = null; image.color = new Color32(37, 53, 63, 255); }
            image.gameObject.SetActive(true);
        }
        for (; index < tiles.Count; index++) { tiles[index].texture = null; tiles[index].gameObject.SetActive(false); }
        workKeys.Clear();
        foreach (string key in requests.Keys) if (!visible.ContainsKey(key)) workKeys.Add(key);
        for (int i = 0; i < workKeys.Count; i++) requests[workKeys[i]].Abort();
        tileFailure = false;
        foreach (string key in visible.Keys)
            if (!cache.ContainsKey(key) && retryAt.ContainsKey(key)) { tileFailure = true; break; }
        UpdateStatus(configured);
    }

    private void FitOverview()
    {
        WebMercator(geometry[0], out double anchor, out double firstY);
        double minX = anchor, maxX = anchor, minY = firstY, maxY = firstY;
        for (int i = 1; i < geometry.Count; i++)
        {
            WebMercator(geometry[i], out double x, out double y); x = anchor + WrappedDelta(x - anchor);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        centreX = (minX + maxX) * 0.5; centreY = (minY + maxY) * 0.5;
        double availableWidth = Math.Max(64, viewport.rect.width - 64f);
        double availableHeight = Math.Max(64, viewport.rect.height - 64f);
        double scaleX = availableWidth / (TilePixels * Math.Max(0.00000001, maxX - minX));
        double scaleY = availableHeight / (TilePixels * Math.Max(0.00000001, maxY - minY));
        zoom = Mathf.Clamp((int)Math.Floor(Math.Log(Math.Min(scaleX, scaleY), 2.0)), 3, 19);
    }

    private void BuildVisibleTiles(float rotation)
    {
        int world = 1 << zoom;
        double tileX = centreX * world, tileY = centreY * world;
        float radians = rotation * Mathf.Deg2Rad, c = Mathf.Cos(radians), s = Mathf.Sin(radians);
        float halfWidth = viewport.rect.width * 0.5f, halfHeight = viewport.rect.height * 0.5f;
        float boundX = Mathf.Abs(c) * halfWidth + Mathf.Abs(s) * halfHeight;
        float boundY = Mathf.Abs(s) * halfWidth + Mathf.Abs(c) * halfHeight;
        int left = (int)Math.Floor(tileX - boundX / TilePixels), right = (int)Math.Floor(tileX + boundX / TilePixels);
        int top = Math.Max(0, (int)Math.Floor(tileY - boundY / TilePixels));
        int bottom = Math.Min(world - 1, (int)Math.Floor(tileY + boundY / TilePixels));
        // This component is a compact display, so a pathological external resize cannot enqueue a region.
        right = Math.Min(right, left + 8); bottom = Math.Min(bottom, top + 8);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                Vector2 offset = new Vector2((float)((x + 0.5 - tileX) * TilePixels), (float)((tileY - y - 0.5) * TilePixels));
                if (!TileIntersectsViewport(offset, c, s, halfWidth, halfHeight)) continue;
                int wrappedX = ((x % world) + world) % world;
                string coordinate = zoom + "_" + wrappedX + "_" + y;
                string key = cacheDirectory + "/" + coordinate;
                candidates.Add(new TileSpec { Key = key, X = wrappedX, Y = y, Zoom = zoom,
                    Offset = offset, Distance = offset.sqrMagnitude,
                    Url = tileUrlTemplate.Replace("{z}", zoom.ToString()).Replace("{x}", wrappedX.ToString()).Replace("{y}", y.ToString()) });
            }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        for (int i = 0; i < candidates.Count && i < MaxVisibleTiles; i++) visible[candidates[i].Key] = candidates[i];
    }

    private static bool TileIntersectsViewport(Vector2 centre, float c, float s, float hw, float hh)
    {
        const float tileHalf = TilePixels * 0.5f;
        Vector2 rotated = new Vector2(c * centre.x - s * centre.y, s * centre.x + c * centre.y);
        float extent = tileHalf * (Mathf.Abs(c) + Mathf.Abs(s));
        return Mathf.Abs(rotated.x) <= hw + extent && Mathf.Abs(rotated.y) <= hh + extent &&
               Mathf.Abs(centre.x) <= tileHalf + hw * Mathf.Abs(c) + hh * Mathf.Abs(s) &&
               Mathf.Abs(centre.y) <= tileHalf + hw * Mathf.Abs(s) + hh * Mathf.Abs(c);
    }

    private void PumpRequests()
    {
        if (!Application.isPlaying || suspended || string.IsNullOrWhiteSpace(userAgent)) return;
        foreach (TileSpec tile in visible.Values)
        {
            if (requests.Count >= MaxConcurrentRequests) break;
            if (cache.ContainsKey(tile.Key) || requests.ContainsKey(tile.Key)) continue;
            if (retryAt.TryGetValue(tile.Key, out float retry) && Time.unscaledTime < retry) continue;
            Texture2D cached = ReadDisk(tile.Key + ".png");
            if (cached != null) { AddTexture(tile.Key, cached); dirty = true; continue; }
            StartCoroutine(FetchTile(tile, lifetime));
        }
    }

    private IEnumerator FetchTile(TileSpec tile, int version)
    {
        var download = new TileDownload();
        UnityWebRequest request = new UnityWebRequest(tile.Url, UnityWebRequest.kHttpVerbGET, download, null);
        request.timeout = 15;
        request.SetRequestHeader("User-Agent", userAgent);
        requests[tile.Key] = request;
        yield return request.SendWebRequest();
        if (version != lifetime) yield break; // OnDisable owns disposal of the old generation.
        bool wanted = isActiveAndEnabled && visible.ContainsKey(tile.Key);
        byte[] bytes = request.result == UnityWebRequest.Result.Success ? download.ReadBytes() : null;
        requests.Remove(tile.Key); request.Dispose();
        if (!wanted) yield break;
        Texture2D texture = Decode(bytes);
        if (texture != null)
        {
            AddTexture(tile.Key, texture); WriteDisk(tile.Key + ".png", bytes); retryAt.Remove(tile.Key); tileFailure = false;
        }
        else { retryAt[tile.Key] = Time.unscaledTime + 30f; tileFailure = true; }
        dirty = true;
    }

    private void CancelRequests()
    {
        lifetime++;
        StopAllCoroutines();
        foreach (UnityWebRequest request in requests.Values) { request.Abort(); request.Dispose(); }
        requests.Clear();
    }

    private void AddTexture(string key, Texture2D texture)
    {
        if (cache.TryGetValue(key, out TileEntry replaced) && replaced.Texture != null) Destroy(replaced.Texture);
        cache[key] = new TileEntry { Texture = texture, Used = ++useSequence };
        while (cache.Count > MaxTextures)
        {
            string oldest = null; long age = long.MaxValue;
            foreach (KeyValuePair<string, TileEntry> entry in cache)
                if (!visible.ContainsKey(entry.Key) && entry.Value.Used < age) { oldest = entry.Key; age = entry.Value.Used; }
            if (oldest == null) break;
            Destroy(cache[oldest].Texture); cache.Remove(oldest);
        }
    }

    private static Texture2D Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return null;
        // Check PNG dimensions before Unity allocates the decoded image. A tiny compressed
        // but enormous untrusted image must not be allowed to exhaust headset memory.
        if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 ||
            bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82) return null;
        uint width = ((uint)bytes[16] << 24) | ((uint)bytes[17] << 16) | ((uint)bytes[18] << 8) | bytes[19];
        uint height = ((uint)bytes[20] << 24) | ((uint)bytes[21] << 16) | ((uint)bytes[22] << 8) | bytes[23];
        if (width == 0 || height == 0 || width > 512 || height > 512) return null;
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, bytes, true))
        { Destroy(texture); return null; }
        texture.wrapMode = TextureWrapMode.Clamp; texture.filterMode = FilterMode.Bilinear;
        return texture;
    }

    private sealed class TileDownload : DownloadHandlerScript
    {
        private readonly MemoryStream buffer = new MemoryStream();
        public TileDownload() : base(new byte[16384]) { }
        protected override bool ReceiveData(byte[] data, int length)
        {
            if (data == null || length <= 0) return true;
            if (buffer.Length + length > 2 * 1024 * 1024) return false;
            buffer.Write(data, 0, length);
            return true;
        }
        public byte[] ReadBytes() => buffer.ToArray();
        public override void Dispose() { buffer.Dispose(); base.Dispose(); }
    }

    private static Texture2D ReadDisk(string path)
    {
        try
        {
            if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CacheLifetime) return null;
            FileInfo info = new FileInfo(path);
            return info.Length <= 2 * 1024 * 1024 ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteDisk(string path, byte[] bytes)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void UpdateStatus(bool configured)
    {
        if (!positionFresh) StatusMessage = "Location paused — reconnect phone.";
        else if (!configured) StatusMessage = "Street map provider is not configured.";
        else if (tileFailure) StatusMessage = "Street tiles unavailable — route remains visible.";
        else if (overview) StatusMessage = "Route overview · north-up";
        else if (headingUp && !headingFresh) StatusMessage = "Compass unavailable · north-up";
        else StatusMessage = UsingNorthUp ? "North-up" : "Phone-relative";
    }

    public static bool IsTileTemplateValid(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{z}") || !template.Contains("{x}") || !template.Contains("{y}")) return false;
        string example = template.Replace("{z}", "0").Replace("{x}", "0").Replace("{y}", "0");
        return Uri.TryCreate(example, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool IsValid(GeoCoordinate coordinate)
    {
        return !double.IsNaN(coordinate.Latitude) && !double.IsInfinity(coordinate.Latitude) &&
               !double.IsNaN(coordinate.Longitude) && !double.IsInfinity(coordinate.Longitude) &&
               coordinate.Latitude >= -90 && coordinate.Latitude <= 90 && coordinate.Longitude >= -180 && coordinate.Longitude <= 180;
    }

    public static void WebMercator(GeoCoordinate coordinate, out double x, out double y)
    {
        double latitude = Math.Max(-MercatorLimit, Math.Min(MercatorLimit, coordinate.Latitude)) * Math.PI / 180.0;
        x = (coordinate.Longitude + 180.0) / 360.0;
        y = Math.Max(0.0, Math.Min(1.0, (1.0 - Math.Log(Math.Tan(latitude) + 1.0 / Math.Cos(latitude)) / Math.PI) * 0.5));
    }

    public static double WrappedDelta(double delta) { return delta - Math.Floor(delta + 0.5); }

    public static Vector2 Project(GeoCoordinate coordinate, double centreNormalizedX, double centreNormalizedY, int zoomLevel, float rotationDegrees)
    {
        WebMercator(coordinate, out double x, out double y);
        double scale = TilePixels * Math.Pow(2.0, Mathf.Clamp(zoomLevel, 0, 22));
        double dx = WrappedDelta(x - centreNormalizedX) * scale, dy = (centreNormalizedY - y) * scale;
        double angle = rotationDegrees * Math.PI / 180.0;
        return new Vector2((float)(dx * Math.Cos(angle) - dy * Math.Sin(angle)),
            (float)(dx * Math.Sin(angle) + dy * Math.Cos(angle)));
    }

    private static string Hash(string value)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
        }
    }
}
