using Riptide;
using Riptide.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public enum ServerToClientId : ushort
{
    playerSpawned = 1,
    playerState,
}

public enum ClientToServerId : ushort
{
    playerInputs = 1,
}

public class NetworkManager : MonoBehaviour
{
    private static NetworkManager _singleton;
    public static NetworkManager Singleton
    {
        get { return _singleton; }
        set
        {
            if (_singleton == null)
            {
                _singleton = value;
            }

            else if (_singleton != value)
            {
                Debug.Log($"{nameof(NetworkManager)} instance already exists, destroying duplicate");
                Destroy(value.gameObject);
            }
        }
    }

    public Client Client { get; private set; }
    public Server Server { get; private set; }

    // The server's own physics world, server players live here
    public Scene serverScene { get; private set; }
    private PhysicsScene serverPhysicsScene;

    [Header("Prefabs")]
    [Space(5)]
    [SerializeField] public GameObject localPlayerPrefab;
    [SerializeField] public GameObject serverPlayerPrefab;

    [Header("Level")]
    [Space(5)]
    [SerializeField] private GameObject level; // Copied into the server scene on start

    [Header("Server")]
    [Space(5)]
    [SerializeField] private ushort port = 7777;
    [SerializeField] private int tickRate = 64; //64 ticks every 1000ms
    [SerializeField] private Vector3 spawnPosition = new Vector3(0, 1, 0);

    [Header("Network Simulation")]
    [Space(5)]
    [SerializeField] public float simulatedPing = 200; // Round trip ms, half each way
    [SerializeField] public float simulatedJitter = 20; // ± ms each way
    [Range(0, 100)][SerializeField] public float simulatedPacketLoss = 10; // % of unreliable messages dropped

    private struct DelayedMessage
    {
        public Message message;
        public float sendTime;
        public bool toServer;
        public ushort toClientId;
    }

    private readonly List<DelayedMessage> delayedMessages = new List<DelayedMessage>();

    private void Awake()
    {
        Singleton = this;
        if (Singleton != this) return;
        DontDestroyOnLoad(gameObject);

        Time.fixedDeltaTime = 1f / tickRate;

        // Physics only steps when we tell it to, replays need that
        Physics.simulationMode = SimulationMode.Script;
    }

    private void Start()
    {
        RiptideLogger.Initialize(Debug.Log, Debug.Log, Debug.LogWarning, Debug.LogError, false);

        CreateServerScene();

        Server = new Server();
        Client = new Client();

        Server.ClientConnected += ServerClientConnected;
        Server.ClientDisconnected += ServerClientDisconnected;
        Client.Disconnected += LocalPlayerDisconnected;

        // Server and client in the same process, they still only talk through messages
        Server.Start(port, 1);
        Client.Connect($"127.0.0.1:{port}");
    }

    private void FixedUpdate()
    {
        // Receive -> simulate -> send, always in this order
        if (Server.IsRunning)
        {
            Server.Update();
            ServerTick();
        }

        Client.Update();
        foreach (Player player in Player.List.Values) if (player.IsLocal) player.playerMovement.ClientTick();

        FlushDelayedMessages();
    }

    private void OnApplicationQuit()
    {
        Server.ClientConnected -= ServerClientConnected;
        Server.ClientDisconnected -= ServerClientDisconnected;
        Client.Disconnected -= LocalPlayerDisconnected;

        Client.Disconnect();
        if (Server.IsRunning) Server.Stop();

        foreach (DelayedMessage delayed in delayedMessages) delayed.message.Release();
        delayedMessages.Clear();
    }

    #region Physics
    // Same process, so the server needs its own physics world or both sides would collide and step together
    private void CreateServerScene()
    {
        serverScene = SceneManager.CreateScene("Server", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        serverPhysicsScene = serverScene.GetPhysicsScene();

        GameObject serverLevel = Instantiate(level);
        serverLevel.name = "Server Level";
        SceneManager.MoveGameObjectToScene(serverLevel, serverScene);

        // Only its colliders matter, the client's copy is the one we see
        foreach (Renderer renderer in serverLevel.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
    }

    // Every step moves the whole server scene, players without an input to process sit it out
    private void ServerTick()
    {
        foreach (Player player in Player.ServerList.Values) player.playerMovement.StartServerTick();

        while (ServerPlayersHaveInputs())
        {
            foreach (Player player in Player.ServerList.Values) player.playerMovement.PrepareServerStep();
            serverPhysicsScene.Simulate(Time.fixedDeltaTime);
        }

        foreach (Player player in Player.ServerList.Values) player.playerMovement.EndServerTick();
    }

    private bool ServerPlayersHaveInputs()
    {
        foreach (Player player in Player.ServerList.Values) if (player.playerMovement.HasInputToProcess()) return true;
        return false;
    }
    #endregion

    #region Network Simulation
    // Fakes ping, jitter and packet loss, a real game calls Client.Send / Server.Send directly
    public void SendToServer(Message message)
    {
        DelayMessage(message, true, 0);
    }

    public void SendToClient(Message message, ushort toClientId)
    {
        DelayMessage(message, false, toClientId);
    }

    private void DelayMessage(Message message, bool toServer, ushort toClientId)
    {
        if (message.SendMode == MessageSendMode.Unreliable && UnityEngine.Random.value * 100f < simulatedPacketLoss)
        {
            message.Release();
            return;
        }

        float oneWayDelay = (simulatedPing * 0.5f + UnityEngine.Random.Range(-simulatedJitter, simulatedJitter)) / 1000f;
        delayedMessages.Add(new DelayedMessage
        {
            message = message,
            sendTime = Time.time + Mathf.Max(0f, oneWayDelay),
            toServer = toServer,
            toClientId = toClientId
        });
    }

    private void FlushDelayedMessages()
    {
        for (int i = 0; i < delayedMessages.Count; i++)
        {
            DelayedMessage delayed = delayedMessages[i];
            if (delayed.sendTime > Time.time) continue;

            delayedMessages.RemoveAt(i);
            i--;

            if (delayed.toServer)
            {
                if (Client.IsConnected) Client.Send(delayed.message);
                else delayed.message.Release();
            }
            else
            {
                if (Server.IsRunning && Server.TryGetClient(delayed.toClientId, out Connection _)) Server.Send(delayed.message, delayed.toClientId);
                else delayed.message.Release();
            }
        }
    }
    #endregion

    #region Server/Client Event Handlers
    private void ServerClientConnected(object sender, ServerConnectedEventArgs e)
    {
        Player.SpawnServerPlayer(e.Client.Id, spawnPosition);
    }

    private void ServerClientDisconnected(object sender, ServerDisconnectedEventArgs e)
    {
        Debug.LogWarning($"[disconnect] server dropped client {e.Client.Id} - reason: {e.Reason}");
        if (Player.ServerList.TryGetValue(e.Client.Id, out Player player)) Destroy(player.gameObject);
    }

    private void LocalPlayerDisconnected(object sender, DisconnectedEventArgs e)
    {
        Debug.LogWarning($"[disconnect] local player disconnected from the server - reason: {e.Reason}");
        foreach (Player player in Player.List.Values) Destroy(player.gameObject);
    }
    #endregion
}
