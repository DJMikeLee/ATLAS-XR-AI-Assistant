using UnityEngine;
using WebSocketSharp;
using System.Security.Authentication;

[System.Serializable]
public class PhoneData
{
    public float lat;
    public float lon;
    public float heading;
}

public class PhoneTether : MonoBehaviour
{
    [Header("PieSocket Credentials")]
    private string apiKey;
    private string clusterId;

    [Header("Editor Debugging")]
    public string debugRoomCode = "0000";

    public float currentLat { get; private set; }
    public float currentLon { get; private set; }
    public float currentHeading { get; private set; }

    public bool isSocketOpen { get; private set; }
    public bool isReceivingData { get; private set; }
    public bool isSignalLost => isReceivingData && timeSinceLastUpdate > TIMEOUT_SECONDS;
    public string lastNetworkMessage { get; private set; } = "";
    public string currentRoomCode { get; private set; } = "";

    private WebSocket ws;
    private readonly object threadLocker = new object();
    private PhoneData latestData = null;
    private bool hasNewData = false;
    private float timeSinceLastUpdate = 0f;

    private const float TIMEOUT_SECONDS = 5.0f;

    private void Awake()
    {
        APIConfig config = Resources.Load<APIConfig>("API_Secrets");
        if (config != null)
        {
            apiKey = config.pieSocketApiKey;
            clusterId = config.pieSocketClusterId;
        }
        else
        {
            Debug.LogError("[PhoneTether] API_Secrets file not found! Run ATLAS VR -> API Setup.");
        }
    }

    private void OnEnable()
    {
        timeSinceLastUpdate = 0f;
        isReceivingData = false;
        isSocketOpen = false;
        lastNetworkMessage = "";
    }

    private void OnDisable()
    {
        if (ws != null && ws.IsAlive)
        {
            ws.Close();
            Debug.Log("Map Page closed. Server disconnected.");
        }

        isSocketOpen = false;
        isReceivingData = false;
    }

    [ContextMenu("Force Debug Connection")]
    public void ForceDebugConnection()
    {
        if (debugRoomCode.Length == 4)
        {
            ConnectToRelay(debugRoomCode);
        }
    }

    public void ConnectToRelay(string roomCode)
    {
        if (ws != null && ws.IsAlive)
            ws.Close();

        currentRoomCode = roomCode;
        timeSinceLastUpdate = 0f;
        isReceivingData = false;
        isSocketOpen = false;
        lastNetworkMessage = "Connecting to phone...";

        string wsUrl = $"wss://{clusterId}/{roomCode}?api_key={apiKey}&notify_self=0";
        ws = new WebSocket(wsUrl);
        ws.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;

        ws.OnMessage += (sender, e) =>
        {
            PhoneData data = JsonUtility.FromJson<PhoneData>(e.Data);
            lock (threadLocker)
            {
                latestData = data;
                hasNewData = true;
            }
        };

        ws.OnOpen += (sender, e) =>
        {
            lock (threadLocker)
            {
                isSocketOpen = true;
                lastNetworkMessage = "Connected to server. Waiting for GPS...";
            }
        };

        ws.OnError += (sender, e) =>
        {
            lock (threadLocker)
            {
                isSocketOpen = false;
                lastNetworkMessage = $"Connection Error: {e.Message}";
            }
        };

        ws.OnClose += (sender, e) =>
        {
            lock (threadLocker)
            {
                isSocketOpen = false;
                lastNetworkMessage = $"Disconnected: {e.Reason}";
            }
        };

        ws.ConnectAsync();
    }

    private void Update()
    {
        if (isReceivingData)
            timeSinceLastUpdate += Time.deltaTime;

        lock (threadLocker)
        {
            if (hasNewData && latestData != null)
            {
                currentLat = latestData.lat;
                currentLon = latestData.lon;
                currentHeading = latestData.heading;
                hasNewData = false;

                timeSinceLastUpdate = 0f;
                isReceivingData = true;
                lastNetworkMessage = "Tracking Live!";
            }
        }
    }
}