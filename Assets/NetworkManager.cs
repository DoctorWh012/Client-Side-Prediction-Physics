using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Riptide;

public enum ServerToClientId : ushort
{
    playerMovement = 1,

}

public enum ClientToServerId : ushort
{
    input = 1,

}

public class NetworkManager : MonoBehaviour
{
    private static NetworkManager _singleton;
    public static NetworkManager Singleton
    {
        get => _singleton;

        private set
        {
            if (_singleton == null) { _singleton = value; }
            else if (_singleton != value) { Destroy(value.gameObject); }
        }
    }

    public const float ServerTickRate = 64f;
    public Server Server { get; private set; }
    public Client Client { get; private set; }
    public float minTimeBetweenTicks { get; private set; }

    [Header("Settings")]
    [SerializeField] public float packetLossChance;
    [SerializeField] public float inputMessageDelay;

    private void Awake()
    {
        Singleton = this;
        minTimeBetweenTicks = 1f / ServerTickRate;
    }

    // Start is called before the first frame update
    void Start()
    {
        Server = new Server();
        Client = new Client();

        Server.Start(1717, 10);
        Client.Connect("127.0.0.1:1717");
    }

    // Update is called once per frame
    void FixedUpdate()
    {
        Server.Update();
        Client.Update();
    }
}
