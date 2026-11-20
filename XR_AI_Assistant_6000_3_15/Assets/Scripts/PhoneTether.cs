using System;
using System.Security.Authentication;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WebSocketSharp;

[Serializable]
public class PhoneData
{
    public float lat;
    public float lon;
    public float heading;
    public double timestamp = double.NaN;
    public double headingTimestamp = double.NaN;
    public float accuracy = float.NaN;
    public bool headingValid;
}

public class PhoneTether : MonoBehaviour
{
    // The new display asks for Connect before resuming the remembered room.
    public bool AllowAutomaticReconnect { get; set; } = true;
    public enum ConnectionState { Idle, Connecting, WaitingForLocation, Tracking, SignalLost, Reconnecting, Error }

    [Header("PieSocket Credentials")]
    private string apiKey;
    private string clusterId;

    [Header("Editor Debugging")]
    public string debugRoomCode = "0000";

    public float currentLat { get; private set; }
    public float currentLon { get; private set; }
    public float currentHeading { get; private set; }
    public float currentAccuracyMeters { get; private set; } = float.NaN;
    public uint locationRevision { get; private set; }
    public bool isSocketOpen { get; private set; }
    public bool isReceivingData => HasFreshLocation;
    public bool isSignalLost => State == ConnectionState.SignalLost || State == ConnectionState.Reconnecting;
    public string lastNetworkMessage { get; private set; } = "";
    public string currentRoomCode { get; private set; } = "";
    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public bool IsConnecting => State == ConnectionState.Connecting || State == ConnectionState.Reconnecting;
    public float SecondsSinceLastSample => hasLocation ? (float)Math.Max(0, Now - lastLocationTime) : float.PositiveInfinity;
    public bool HasFreshLocation => isActiveAndEnabled && isSocketOpen && hasLocation && SecondsSinceLastSample <= LocationTimeout;
    // Old companions omit accuracy. Retain compatibility, but do not route from a known poor fix.
    public bool HasUsableLocation => HasFreshLocation && IsAccuracyUsable(currentAccuracyMeters);
    public bool HasFreshHeading => HasFreshLocation && hasHeading && Now - lastHeadingTime <= HeadingTimeout;

    private const double LocationTimeout = 15;
    private const double HeadingTimeout = 5;
    private const double ConnectTimeout = 12;
    private const double FirstLocationTimeout = 15;
    private const double AllowedClockSkewMilliseconds = 30000;
    private const string SavedRoomPreference = "XR_AI_Assistant.PhoneTracker.LastSuccessfulRoom";
    private string rememberedRoom = "";
    private bool preferencesLoaded;

    private WebSocket ws;
    private readonly object threadLocker = new object();
    private int sessionGeneration;
    private bool callbackSocketOpen;
    private bool? pendingSocketOpen;
    private PhoneData pendingData;
    private double pendingReceiptTime;
    private double pendingReceiptEpoch;
    private bool connectionRequested;
    private bool applicationPaused;
    private bool hasLocation;
    private bool hasHeading;
    private bool usesLocationTimestamps;
    private bool usesHeadingTimestamps;
    private double lastLocationTimestamp = double.NegativeInfinity;
    private double lastHeadingTimestamp = double.NegativeInfinity;
    private double lastLocationTime;
    private double lastHeadingTime;
    private double connectionStarted;
    private double socketOpened;
    private double nextRetry = double.PositiveInfinity;
    private int retryAttempt;

    // A monotonic clock also works in socket callbacks without accessing Unity APIs.
    private static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    private void Awake()
    {
        rememberedRoom = PlayerPrefs.GetString(SavedRoomPreference, "");
        if (!IsValidRoomCode(rememberedRoom)) rememberedRoom = "";
        if (!IsValidRoomCode(currentRoomCode)) currentRoomCode = rememberedRoom;
        preferencesLoaded = true;
        APIConfig config = Resources.Load<APIConfig>("API_Secrets");
        if (config != null)
        {
            apiKey = config.pieSocketApiKey;
            clusterId = config.pieSocketClusterId;
        }
    }

    private void OnEnable()
    {
        ClearSamples();
        State = ConnectionState.Idle;
        lastNetworkMessage = "Enter your phone's four-digit room code.";
        if (AllowAutomaticReconnect && IsValidRoomCode(currentRoomCode)) RetryConnection();
    }

    private void OnDisable()
    {
        connectionRequested = false;
        nextRetry = double.PositiveInfinity;
        CancelSession();
        ClearSamples();
        State = ConnectionState.Idle;
    }

    private void OnDestroy() => CancelSession();

    private void OnApplicationPause(bool paused)
    {
        if (applicationPaused == paused) return;
        applicationPaused = paused;
        if (paused)
        {
            CancelSession();
            ClearSamples();
            if (connectionRequested)
            {
                State = ConnectionState.SignalLost;
                lastNetworkMessage = "Location paused while the app is suspended.";
            }
        }
        else if (connectionRequested && isActiveAndEnabled && IsValidRoomCode(currentRoomCode))
            StartConnection();
    }

    [ContextMenu("Force Debug Connection")]
    public void ForceDebugConnection() => ConnectToRelay(debugRoomCode);

    public static bool IsValidRoomCode(string roomCode)
    {
        if (roomCode == null || roomCode.Length != 4) return false;
        foreach (char character in roomCode)
            if (character < '0' || character > '9') return false;
        return true;
    }

    public static bool IsAccuracyUsable(float accuracyMeters)
    {
        return float.IsNaN(accuracyMeters) ||
            (!float.IsInfinity(accuracyMeters) && accuracyMeters >= 0 && accuracyMeters <= 50);
    }

    public void ConnectToRelay(string roomCode)
    {
        if (!IsValidRoomCode(roomCode))
        {
            lastNetworkMessage = "Enter exactly four digits (0-9).";
            return;
        }

        currentRoomCode = roomCode;
        retryAttempt = 0;
        connectionRequested = true;
        nextRetry = double.PositiveInfinity;
        if (isActiveAndEnabled) StartConnection();
    }

    public void RetryConnection()
    {
        if (IsValidRoomCode(currentRoomCode)) ConnectToRelay(currentRoomCode);
        else ResetConnection();
    }

    public void ResetConnection()
    {
        connectionRequested = false;
        nextRetry = double.PositiveInfinity;
        retryAttempt = 0;
        CancelSession();
        ClearSamples();
        currentRoomCode = "";
        if (preferencesLoaded)
        {
            rememberedRoom = "";
            PlayerPrefs.DeleteKey(SavedRoomPreference);
            PlayerPrefs.Save();
        }
        State = ConnectionState.Idle;
        lastNetworkMessage = "Enter your phone's four-digit room code.";
    }

    private void StartConnection()
    {
        CancelSession();
        ClearSamples();
        nextRetry = double.PositiveInfinity;
        if (!connectionRequested || !isActiveAndEnabled || applicationPaused) return;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(clusterId))
        {
            State = ConnectionState.Error;
            lastNetworkMessage = "Phone relay configuration is unavailable.";
            return;
        }

        State = ConnectionState.Connecting;
        lastNetworkMessage = "Connecting to phone relay...";
        connectionStarted = Now;
        int generation = sessionGeneration;
        try
        {
            string url = $"wss://{clusterId}/{currentRoomCode}?api_key={Uri.EscapeDataString(apiKey)}&notify_self=0";
            WebSocket socket = new WebSocket(url);
            ws = socket;
            // The library's default diagnostics can include the credential-bearing URL.
            socket.Log.Output = (entry, path) => { };
            socket.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;
            socket.OnOpen += (sender, args) =>
            {
                bool obsolete;
                lock (threadLocker)
                {
                    obsolete = generation != sessionGeneration;
                    if (!obsolete)
                    {
                        callbackSocketOpen = true;
                        pendingSocketOpen = true;
                    }
                }
                // A connection can finish after a page close/reset while ConnectAsync is running.
                if (obsolete) CloseSilently(socket);
            };
            socket.OnError += (sender, args) => QueueDisconnect(generation);
            socket.OnClose += (sender, args) => QueueDisconnect(generation);
            socket.OnMessage += (sender, args) =>
            {
                if (!args.IsText || !TryParsePhoneData(args.Data, out PhoneData data)) return;
                lock (threadLocker)
                {
                    if (generation != sessionGeneration || !callbackSocketOpen) return;
                    // Keep the newest timestamped fix if several messages arrive in one frame.
                    if (pendingData != null && !double.IsNaN(data.timestamp) &&
                        !double.IsNaN(pendingData.timestamp) && data.timestamp < pendingData.timestamp) return;
                    pendingData = data;
                    pendingReceiptTime = Now;
                    pendingReceiptEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            };
            socket.ConnectAsync();
        }
        catch (Exception)
        {
            ScheduleRetry();
        }
    }

    private void QueueDisconnect(int generation)
    {
        lock (threadLocker)
        {
            if (generation != sessionGeneration) return;
            callbackSocketOpen = false;
            pendingSocketOpen = false;
            pendingData = null;
        }
    }

    private void CancelSession()
    {
        WebSocket oldSocket = ws;
        ws = null;
        lock (threadLocker)
        {
            sessionGeneration++;
            callbackSocketOpen = false;
            pendingSocketOpen = null;
            pendingData = null;
        }
        isSocketOpen = false;
        // Close connecting sockets too, not only sockets whose IsAlive is already true.
        CloseSilently(oldSocket);
    }

    private static void CloseSilently(WebSocket socket)
    {
        if (socket == null) return;
        try { socket.CloseAsync(); }
        catch (Exception) { /* A superseded/closed socket cannot affect current UI state. */ }
    }

    private void ScheduleRetry()
    {
        CancelSession();
        ClearSamples();
        if (!connectionRequested || !isActiveAndEnabled) return;
        double delay = Math.Min(30, Math.Pow(2, Math.Min(retryAttempt + 1, 5)));
        retryAttempt++;
        nextRetry = Now + delay;
        State = ConnectionState.Reconnecting;
        lastNetworkMessage = "Connection interrupted. Retrying; you can also change the room.";
    }

    private void Update()
    {
        if (applicationPaused) return;
        bool? socketState;
        PhoneData data;
        double receiptTime;
        double receiptEpoch;
        lock (threadLocker)
        {
            socketState = pendingSocketOpen;
            data = pendingData;
            receiptTime = pendingReceiptTime;
            receiptEpoch = pendingReceiptEpoch;
            pendingSocketOpen = null;
            pendingData = null;
        }

        if (socketState == false) ScheduleRetry();
        else
        {
            if (socketState == true)
            {
                isSocketOpen = true;
                socketOpened = Now;
                State = ConnectionState.WaitingForLocation;
            }
            if (data != null && isSocketOpen) ApplySample(data, receiptTime, receiptEpoch);
        }

        if (!connectionRequested) return;
        if (State == ConnectionState.Connecting && Now - connectionStarted > ConnectTimeout)
            ScheduleRetry();
        if (State == ConnectionState.Reconnecting && Now >= nextRetry)
            StartConnection();
        if (!isSocketOpen) return;

        if (HasFreshLocation)
        {
            State = ConnectionState.Tracking;
            retryAttempt = 0;
            // Only remember a room once it actually receives a fresh phone fix. A mistyped
            // room or a relay-only connection must not replace the last working pairing.
            if (preferencesLoaded && rememberedRoom != currentRoomCode && IsValidRoomCode(currentRoomCode))
            {
                rememberedRoom = currentRoomCode;
                PlayerPrefs.SetString(SavedRoomPreference, rememberedRoom);
                PlayerPrefs.Save();
            }
            lastNetworkMessage = !HasUsableLocation ? "GPS accuracy is low. Wait in an open area for a better fix."
                : HasFreshHeading ? "Phone location and compass are live." : "Phone location is live. Waiting for a fresh compass reading.";
        }
        else if (hasLocation || Now - socketOpened > FirstLocationTimeout)
        {
            State = ConnectionState.SignalLost;
            lastNetworkMessage = "GPS paused. Unlock the phone and reopen its tracker. Retry if needed.";
        }
        else
        {
            State = ConnectionState.WaitingForLocation;
            lastNetworkMessage = "Relay connected. Open the phone tracker and share location in this room.";
        }
    }

    private void ClearSamples()
    {
        currentLat = currentLon = currentHeading = 0;
        currentAccuracyMeters = float.NaN;
        locationRevision++;
        hasLocation = hasHeading = false;
        usesLocationTimestamps = usesHeadingTimestamps = false;
        lastLocationTimestamp = lastHeadingTimestamp = double.NegativeInfinity;
        lastLocationTime = lastHeadingTime = 0;
    }

    private void ApplySample(PhoneData data, double receiptTime, double receiptEpoch)
    {
        bool timestamped = !double.IsNaN(data.timestamp);
        if ((!usesLocationTimestamps || timestamped) &&
            (!timestamped || data.timestamp > lastLocationTimestamp) &&
            TrySampleTime(data.timestamp, receiptTime, receiptEpoch, out double locationTime))
        {
            currentLat = data.lat;
            currentLon = data.lon;
            currentAccuracyMeters = data.accuracy;
            locationRevision++;
            hasLocation = true;
            lastLocationTime = locationTime;
            if (timestamped)
            {
                usesLocationTimestamps = true;
                lastLocationTimestamp = data.timestamp;
            }
        }

        if (!data.headingValid)
        {
            hasHeading = false;
            return;
        }
        bool headingTimestamped = !double.IsNaN(data.headingTimestamp);
        // An enhanced packet without a compass timestamp must not refresh an old heading.
        if ((usesHeadingTimestamps || timestamped) && !headingTimestamped) return;
        if (headingTimestamped && data.headingTimestamp <= lastHeadingTimestamp) return;
        if (!TrySampleTime(data.headingTimestamp, receiptTime, receiptEpoch, out double headingTime)) return;
        currentHeading = data.heading;
        hasHeading = true;
        lastHeadingTime = headingTime;
        if (headingTimestamped)
        {
            usesHeadingTimestamps = true;
            lastHeadingTimestamp = data.headingTimestamp;
        }
    }

    private static bool TrySampleTime(double timestamp, double receivedAt, double receivedEpoch, out double sampleTime)
    {
        sampleTime = receivedAt;
        if (double.IsNaN(timestamp)) return true; // Legacy phone payload: only receipt age is available.
        if (timestamp > receivedEpoch + AllowedClockSkewMilliseconds) return false;
        sampleTime -= Math.Max(0, receivedEpoch - timestamp) / 1000;
        return true;
    }

    // Pure parser: malformed/control messages never become an invented (0,0) fix.
    internal static bool TryParsePhoneData(string json, out PhoneData data)
    {
        data = null;
        if (string.IsNullOrEmpty(json) || json.Length > 4096) return false;
        try
        {
            JObject payload = JObject.Parse(json);
            if (!TryNumber(payload["lat"], out double lat) || lat < -90 || lat > 90 ||
                !TryNumber(payload["lon"], out double lon) || lon < -180 || lon > 180) return false;
            var parsed = new PhoneData { lat = (float)lat, lon = (float)lon };
            if (payload["timestamp"] != null)
            {
                if (!TryNumber(payload["timestamp"], out double timestamp) || timestamp <= 0) return false;
                parsed.timestamp = timestamp;
            }
            if (payload["accuracy"] != null)
            {
                if (!TryNumber(payload["accuracy"], out double accuracy) || accuracy < 0 || accuracy > float.MaxValue) return false;
                parsed.accuracy = (float)accuracy;
            }
            parsed.headingValid = TryNumber(payload["heading"], out double heading) && heading >= 0 && heading <= 360;
            JToken valid = payload["headingValid"];
            if (valid != null)
                parsed.headingValid &= valid.Type == JTokenType.Boolean && valid.Value<bool>();
            if (payload["headingTimestamp"] != null)
            {
                if (TryNumber(payload["headingTimestamp"], out double headingTimestamp) && headingTimestamp > 0)
                    parsed.headingTimestamp = headingTimestamp;
                else parsed.headingValid = false;
            }
            if (parsed.headingValid) parsed.heading = (float)(heading % 360);
            data = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryNumber(JToken token, out double value)
    {
        value = 0;
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
        value = token.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
