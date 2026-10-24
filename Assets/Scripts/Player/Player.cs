using Riptide;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public class Player : MonoBehaviour
{
    // Client players
    public static Dictionary<ushort, Player> List = new Dictionary<ushort, Player>();

    // Server players, separate because both sides live in one process
    public static Dictionary<ushort, Player> ServerList = new Dictionary<ushort, Player>();

    public ushort Id { get; private set; }
    public bool IsLocal { get; private set; }
    public bool IsServerSide { get; private set; }

    [Header("Components")]
    [Space(5)]
    [SerializeField] public PlayerMovement playerMovement;

    private void OnDestroy()
    {
        // Riptide reuses client ids, only remove it if it's still this player
        Dictionary<ushort, Player> list = IsServerSide ? ServerList : List;
        if (list.TryGetValue(Id, out Player listedPlayer) && listedPlayer == this) list.Remove(Id);
    }

    public static void SpawnServerPlayer(ushort id, Vector3 position)
    {
        if (ServerList.ContainsKey(id))
        {
            Debug.LogWarning($"[spawn] server player {id} was already spawned");
            return;
        }

        Player player = Instantiate(NetworkManager.Singleton.serverPlayerPrefab, position, Quaternion.identity).GetComponent<Player>();
        player.name = $"Server Player {id}";
        player.Id = id;
        player.IsServerSide = true;

        // Into the server's physics world, it never touches the client's colliders
        SceneManager.MoveGameObjectToScene(player.gameObject, NetworkManager.Singleton.serverScene);
        ServerList.Add(id, player);

        player.SendSpawned();
    }

    private static void SpawnLocalPlayer(ushort id, Vector3 position)
    {
        if (List.ContainsKey(id))
        {
            Debug.LogWarning($"[spawn] player {id} was already spawned");
            return;
        }

        Player player = Instantiate(NetworkManager.Singleton.localPlayerPrefab, position, Quaternion.identity).GetComponent<Player>();
        player.name = $"Local Player {id}";
        player.Id = id;
        player.IsLocal = true;

        List.Add(id, player);
    }

    #region ServerSenders
    private void SendSpawned()
    {
        Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.playerSpawned);
        message.AddUShort(Id);
        message.AddVector3(transform.position);
        NetworkManager.Singleton.SendToClient(message, Id);
    }
    #endregion

    #region ServerToClientHandlers
    [MessageHandler((ushort)ServerToClientId.playerSpawned)]
    private static void GetSpawned(Message message)
    {
        SpawnLocalPlayer(message.GetUShort(), message.GetVector3());
    }
    #endregion
}
