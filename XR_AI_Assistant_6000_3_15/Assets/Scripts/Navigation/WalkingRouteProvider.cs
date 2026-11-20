using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Small, cancelable pedestrian-route client. It never requests geocoding or map tiles.</summary>
public sealed class WalkingRouteProvider : MonoBehaviour
{
    [Tooltip("HTTPS route endpoint returning OSRM-format pedestrian routes. Keep configurable for service changes.")]
    [SerializeField] private string routeEndpoint = "https://api.trailsplits.com/route/v1";

    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumGeometryPoints = 10000;
    private const string UserAgent = "XR-AI-Assistant/0.2 (+https://github.com/DJMikeLee/quad-tracker)";
    private static WalkingRouteProvider activeOwner;
    private static double nextRequestTime;
    private Coroutine requestRoutine;
    private UnityWebRequest activeRequest;
    private int generation;

    public bool IsRequesting => requestRoutine != null;
    private static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    public void RequestRoute(GeoCoordinate start, GeoCoordinate destination,
        Action<WalkingRoute> onSuccess, Action<string> onError)
    {
        Cancel();
        if (!isActiveAndEnabled) return;
        if (!start.IsValid || !destination.IsValid)
        {
            onError?.Invoke("The route needs valid start and destination coordinates.");
            return;
        }
        if (!TryEndpoint(routeEndpoint, out string endpoint))
        {
            onError?.Invoke("The walking route service is not configured with a valid HTTPS endpoint.");
            return;
        }
        // TrailSplits accepts the Valhalla pedestrian request contract. Older responses use
        // GeoJSON; its current router returns polyline6 even when GeoJSON is requested.
        var payload = new JObject {
            ["locations"] = new JArray(
                new JObject { ["lat"] = start.Latitude, ["lon"] = start.Longitude },
                new JObject { ["lat"] = destination.Latitude, ["lon"] = destination.Longitude }),
            ["costing"] = "pedestrian", ["format"] = "osrm", ["shape_format"] = "geojson",
            ["directions_options"] = new JObject { ["units"] = "kilometers", ["language"] = "en", ["format"] = "osrm" }
        };
        requestRoutine = StartCoroutine(FetchRoute(endpoint, payload.ToString(Formatting.None), start, destination, generation, onSuccess, onError));
    }

    public void Cancel()
    {
        generation++;
        Coroutine routine = requestRoutine;
        requestRoutine = null;
        if (routine != null) StopCoroutine(routine);
        ReleaseRequest(true);
    }

    private void OnDisable() => Cancel();
    private void OnDestroy() => Cancel();

    private IEnumerator FetchRoute(string url, string payload, GeoCoordinate start, GeoCoordinate destination,
        int version, Action<WalkingRoute> onSuccess, Action<string> onError)
    {
        // First yield ensures requestRoutine is assigned even when a request fails immediately.
        yield return null;
        while (activeOwner != null || Now < nextRequestTime)
        {
            if (!IsCurrent(version)) yield break;
            yield return null;
        }
        if (!IsCurrent(version)) yield break;
        activeOwner = this;
        nextRequestTime = Now + 2;
        UnityWebRequestAsyncOperation operation = BeginRequest(url, payload, out BoundedDownload download);
        if (operation == null)
        {
            ReleaseRequest(true);
            requestRoutine = null;
            if (IsCurrent(version)) onError?.Invoke("The walking route request could not start. Try again.");
            yield break;
        }
        double deadline = Now + 20;
        while (!operation.isDone && Now < deadline)
        {
            if (!IsCurrent(version)) yield break;
            yield return null;
        }
        if (!IsCurrent(version)) yield break;
        string error = null;
        WalkingRoute route = null;
        if (!operation.isDone) error = "The walking route service timed out. Try again.";
        else if (download.TooLarge) error = "This route is too large to display. Choose a closer destination.";
        else if (activeRequest.result != UnityWebRequest.Result.Success)
            error = activeRequest.responseCode == 429
                ? "The walking route service is busy. Wait a moment, then retry."
                : "The walking route service is unavailable. Check your connection and retry.";
        else TryParseRoute(download.ReadText(), out route, out error);
        if (route != null && !MatchesRequestedEndpoints(route, start, destination))
        {
            route = null;
            error = "The returned route does not match these locations. Choose Recalculate or a nearby entrance.";
        }
        ReleaseRequest(!operation.isDone);
        requestRoutine = null;
        if (!IsCurrent(version)) yield break;
        if (route != null) onSuccess?.Invoke(route);
        else onError?.Invoke(error ?? "No walking route was returned. Try another destination.");
    }

    private bool IsCurrent(int version) => version == generation && isActiveAndEnabled;

    public static bool MatchesRequestedEndpoints(WalkingRoute route, GeoCoordinate start, GeoCoordinate destination)
    {
        if (route == null || route.Geometry == null || route.Geometry.Count < 2 || !start.IsValid || !destination.IsValid)
            return false;
        var first = route.Geometry[0];
        var last = route.Geometry[route.Geometry.Count - 1];
        return first.IsValid && last.IsValid && WalkingRouteProgress.Distance(first, start) <= 100 &&
            WalkingRouteProgress.Distance(last, destination) <= 100;
    }

    private UnityWebRequestAsyncOperation BeginRequest(string url, string payload, out BoundedDownload download)
    {
        download = null;
        try
        {
            download = new BoundedDownload();
            activeRequest = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST, download,
                new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)));
            activeRequest.timeout = 20;
            activeRequest.redirectLimit = 0;
            activeRequest.SetRequestHeader("User-Agent", UserAgent);
            activeRequest.SetRequestHeader("Accept", "application/json");
            activeRequest.SetRequestHeader("Content-Type", "application/json");
            return activeRequest.SendWebRequest();
        }
        catch (Exception)
        {
            // Never expose provider URLs, returned text, coordinates or credentials in diagnostics.
            if (activeRequest == null) download?.Dispose();
            return null;
        }
    }

    private void ReleaseRequest(bool abort)
    {
        UnityWebRequest request = activeRequest;
        activeRequest = null;
        if (request != null)
        {
            if (abort) request.Abort();
            request.Dispose();
        }
        if (activeOwner == this) activeOwner = null;
    }

    private static bool TryEndpoint(string value, out string endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        string path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/route/v1", StringComparison.Ordinal) &&
            !path.EndsWith("/route", StringComparison.Ordinal)) return false;
        endpoint = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static bool TryParseRoute(string json, out WalkingRoute route, out string error)
    {
        route = null;
        error = "The service returned an invalid walking route. Try another destination.";
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes ||
            Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes) return false;
        try
        {
            JObject response;
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                response = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) return false;
            }
            if (response["code"]?.Type != JTokenType.String || response["code"].Value<string>() != "Ok")
            {
                string code = response["code"]?.Type == JTokenType.String ? response["code"].Value<string>() : "";
                if (code == "NoRoute" || code == "NoSegment") error = "No walking route connects these locations. Try a nearby public entrance.";
                return false;
            }
            JArray waypoints = response["waypoints"] as JArray;
            if (waypoints == null || waypoints.Count != 2) return false;
            foreach (JToken item in waypoints)
            {
                if (!(item is JObject waypoint) || !TryNumber(waypoint["distance"], out double snap) || snap < 0) return false;
                if (snap > 100) { error = "A location is too far from a mapped walking path. Choose a nearby public entrance."; return false; }
            }
            JArray routes = response["routes"] as JArray;
            JObject first = routes != null && routes.Count > 0 ? routes[0] as JObject : null;
            if (first == null || !TryNumber(first["distance"], out double distance) || distance <= 0 ||
                !TryNumber(first["duration"], out double duration) || duration <= 0) return false;
            // The costing label identifies the provider's weighting model, not a step's
            // transport mode. The current TrailSplits router uses its own label.
            JToken weight = first["weight_name"];
            if (weight != null && (weight.Type != JTokenType.String ||
                (weight.Value<string>() != "pedestrian" && weight.Value<string>() != "trailsplits")))
            { error = "The service returned an unsupported route format. Try again later."; return false; }
            var parsed = new WalkingRoute { DistanceMeters = distance, DurationSeconds = duration,
                Attribution = "Data © OpenStreetMap contributors (ODbL) · Routing: TrailSplits" };
            if (!TryReadGeometry(first["geometry"], parsed)) return false;
            double geometryLength = 0;
            for (int i = 1; i < parsed.Geometry.Count; i++)
                geometryLength += WalkingRouteProgress.Distance(parsed.Geometry[i - 1], parsed.Geometry[i]);
            if (geometryLength < 1) return false;
            JArray legs = first["legs"] as JArray;
            if (legs == null || legs.Count != 1) return false;
            int previousIndex = 0, count = 0;
            foreach (JToken legToken in legs)
            {
                JArray steps = (legToken as JObject)?["steps"] as JArray;
                if (steps == null || steps.Count < 2 || steps.Count > 2000) return false;
                foreach (JToken stepToken in steps)
                {
                    JObject step = stepToken as JObject;
                    if (step == null || step["mode"]?.Type != JTokenType.String || step["mode"].Value<string>() != "walking")
                    { error = "The service did not return a walking-only route. Try another destination."; return false; }
                    JObject maneuver = step["maneuver"] as JObject;
                    if (maneuver == null || !TryCoordinate(maneuver["location"], out GeoCoordinate location) ||
                        maneuver["type"]?.Type != JTokenType.String) return false;
                    int index = NearestForwardIndex(parsed, location, previousIndex);
                    if (index < 0) return false;
                    string type = maneuver["type"].Value<string>();
                    if (count == 0 && (type != "depart" || index != 0)) return false;
                    string modifier = SafeString(maneuver["modifier"], 24);
                    string street = SafeString(step["name"], 100);
                    string instruction = FormatInstruction(type, modifier, street, maneuver);
                    if (string.IsNullOrEmpty(instruction)) return false;
                    parsed.Maneuvers.Add(new RouteManeuver { GeometryIndex = index, Instruction = instruction,
                        Modifier = modifier, StreetName = street });
                    previousIndex = index;
                    count++;
                    if (count == steps.Count && (type != "arrive" || index != parsed.Geometry.Count - 1)) return false;
                }
            }
            route = parsed;
            error = null;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryReadGeometry(JToken geometry, WalkingRoute route)
    {
        if (geometry?.Type == JTokenType.String)
        {
            string encoded = geometry.Value<string>();
            // Each latitude/longitude delta needs at most six 5-bit groups. Bound both
            // encoded input and decoded output before processing untrusted route data.
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaximumGeometryPoints * 12) return false;
            int cursor = 0;
            long latitude = 0, longitude = 0;
            while (cursor < encoded.Length)
            {
                if (route.Geometry.Count >= MaximumGeometryPoints ||
                    !TryReadPolylineDelta(encoded, ref cursor, out long latDelta) ||
                    !TryReadPolylineDelta(encoded, ref cursor, out long lonDelta)) return false;
                latitude += latDelta;
                longitude += lonDelta;
                var point = new GeoCoordinate(latitude / 1000000.0, longitude / 1000000.0);
                if (!point.IsValid) return false;
                route.Geometry.Add(point);
            }
            return route.Geometry.Count >= 2;
        }
        if (!(geometry is JObject shape) || shape["type"]?.Type != JTokenType.String ||
            shape["type"].Value<string>() != "LineString" || !(shape["coordinates"] is JArray coordinates) ||
            coordinates.Count < 2 || coordinates.Count > MaximumGeometryPoints) return false;
        foreach (JToken point in coordinates)
        {
            if (!TryCoordinate(point, out GeoCoordinate coordinate)) return false;
            route.Geometry.Add(coordinate);
        }
        return true;
    }

    private static bool TryReadPolylineDelta(string encoded, ref int cursor, out long delta)
    {
        delta = 0;
        long value = 0;
        for (int shift = 0; shift < 30; shift += 5)
        {
            if (cursor >= encoded.Length) return false;
            int group = encoded[cursor++] - 63;
            if (group < 0 || group > 63) return false;
            value |= (long)(group & 31) << shift;
            if ((group & 32) != 0) continue;
            delta = (value & 1) == 0 ? value >> 1 : ~(value >> 1);
            return true;
        }
        return false;
    }

    private static int NearestForwardIndex(WalkingRoute route, GeoCoordinate location, int previous)
    {
        int best = -1;
        double nearest = double.PositiveInfinity;
        for (int i = previous; i < route.Geometry.Count; i++)
        {
            double distance = WalkingRouteProgress.Distance(location, route.Geometry[i]);
            if (distance < nearest - 0.001) { nearest = distance; best = i; }
        }
        return nearest <= 10 ? best : -1;
    }

    private static string FormatInstruction(string type, string modifier, string street, JObject maneuver)
    {
        string onto = street.Length == 0 ? "" : " onto " + street;
        string along = street.Length == 0 ? "" : " on " + street;
        if (type == "depart")
        {
            string direction = "";
            if (TryNumber(maneuver["bearing_after"], out double bearing) && bearing >= 0 && bearing < 360)
            {
                string[] names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
                direction = " " + names[(int)Math.Floor((bearing + 22.5) / 45) % 8];
            }
            return "Head" + direction + along;
        }
        if (type == "arrive") return "Arrive at your destination";
        if (type == "roundabout" || type == "rotary")
        {
            if (maneuver["exit"]?.Type == JTokenType.Integer && TryNumber(maneuver["exit"], out double exit) && exit >= 1 && exit <= 99)
                return "At the roundabout, take exit " + ((int)exit).ToString(CultureInfo.InvariantCulture) + onto;
            return "Enter the roundabout" + along;
        }
        if (type == "exit roundabout" || type == "exit rotary") return "Exit the roundabout" + onto;
        if (type == "new name" || type == "notification") return "Continue" + along;
        string turn;
        switch (modifier)
        {
            case "uturn": turn = "Turn around"; break;
            case "sharp right": turn = "Turn sharply right"; break;
            case "right": turn = "Turn right"; break;
            case "slight right": turn = "Bear right"; break;
            case "straight": turn = "Continue straight"; break;
            case "slight left": turn = "Bear left"; break;
            case "left": turn = "Turn left"; break;
            case "sharp left": turn = "Turn sharply left"; break;
            default: return null;
        }
        if (type == "fork") turn = modifier.Contains("left") ? "Keep left at the fork" : modifier.Contains("right") ? "Keep right at the fork" : "Continue at the fork";
        if (type == "roundabout turn") turn = "At the roundabout, " + char.ToLowerInvariant(turn[0]) + turn.Substring(1);
        return turn + onto;
    }

    private static bool TryCoordinate(JToken token, out GeoCoordinate coordinate)
    {
        coordinate = default(GeoCoordinate);
        if (!(token is JArray values) || values.Count != 2 || !TryNumber(values[0], out double longitude) ||
            !TryNumber(values[1], out double latitude)) return false;
        coordinate = new GeoCoordinate(latitude, longitude);
        return coordinate.IsValid;
    }

    private static bool TryNumber(JToken token, out double number)
    {
        number = 0;
        if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
        number = token.Value<double>();
        return WalkingRouteProgress.IsFinite(number);
    }

    private static string SafeString(JToken token, int limit)
    {
        if (token?.Type != JTokenType.String) return "";
        string value = token.Value<string>();
        var text = new StringBuilder(Math.Min(value.Length, limit));
        for (int i = 0; i < value.Length && text.Length < limit; i++)
            if (!char.IsControl(value[i])) text.Append(value[i]);
        return text.ToString().Trim();
    }

    private sealed class BoundedDownload : DownloadHandlerScript
    {
        private readonly MemoryStream buffer = new MemoryStream();
        public bool TooLarge { get; private set; }
        public BoundedDownload() : base(new byte[16384]) { }
        protected override bool ReceiveData(byte[] data, int length)
        {
            if (data == null || length <= 0) return true;
            if (buffer.Length + length > MaximumResponseBytes) { TooLarge = true; return false; }
            buffer.Write(data, 0, length);
            return true;
        }
        public string ReadText() => Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        public override void Dispose() { buffer.Dispose(); base.Dispose(); }
    }
}
