using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Riptide;

public class SimulationState
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 velocity;
    public Vector3 angularVelocity;
    public ushort currentTick;
}

public class ClientInputState
{
    public sbyte horizontal;
    public sbyte vertical;
    public bool jump;

    public bool readyToJump = true;
    public float coyoteTimeCounter;
    public float jumpBufferCounter;

    public ushort currentTick;
}

public class MovementInput : MonoBehaviour
{
    public ushort cSPTick { get; private set; }
    public const int StateCacheSize = 1024;

    [Header("Components")]
    [SerializeField] private PlayerMovement playerMovement;

    [Header("Keybinds")]
    [SerializeField] private KeyCode forward;
    [SerializeField] private KeyCode backward;
    [SerializeField] private KeyCode left;
    [SerializeField] private KeyCode right;
    [SerializeField] private KeyCode jump;

    //----Client Side Prediction---
    private SimulationState[] simulationStateCache = new SimulationState[StateCacheSize];
    private ClientInputState[] inputStateCache = new ClientInputState[StateCacheSize];
    public SimulationState serverSimulationState = new SimulationState();
    private int lastCorrectedFrame;
    private float timer;

    private void Update()
    {
        // This creates a fixed timestep to keep the server and the client syncronized
        timer += Time.deltaTime;
        while (timer >= NetworkManager.Singleton.minTimeBetweenTicks)
        {
            timer -= NetworkManager.Singleton.minTimeBetweenTicks;

            // Gets the cache index 
            // This works like, if its tick 300 cache index is 300 but when it gets past the cachesize it resets
            // So when its tick 1025 cacheIndex is 1
            int cacheIndex = cSPTick % StateCacheSize;

            // Get the Inputs and store them in the cache
            inputStateCache[cacheIndex] = GetInput();

            // Stores the current SimState on a cache
            simulationStateCache[cacheIndex] = CurrentSimulationState();

            // Applies the movent
            playerMovement.SetInput(inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].jump);

            // Sends the inputs to the server
            // Here i doing a probabiity check to simulate packet loss and also im using invoke with a delay to simulate high ping
            // This should not be on your actual code, you can just call it directly
            if (NetworkManager.Singleton.packetLossChance < UnityEngine.Random.Range(0, 100))
            {
                Invoke("SendInput", NetworkManager.Singleton.inputMessageDelay / 100);
            }

            cSPTick++;
        }

        // If there's a ServerSimState available checks for reconciliation
        if (serverSimulationState != null) Reconciliate();
    }

    private ClientInputState GetInput()
    {
        return new ClientInputState
        {
            horizontal = (sbyte)Input.GetAxisRaw("Horizontal"),
            vertical = (sbyte)Input.GetAxisRaw("Vertical"),
            jump = Input.GetKey(jump),

            currentTick = cSPTick
        };
    }

    public SimulationState CurrentSimulationState()
    {
        return new SimulationState
        {
            position = playerMovement.rb.position,
            rotation = playerMovement.rb.rotation,
            velocity = playerMovement.rb.velocity,
            angularVelocity = playerMovement.rb.angularVelocity,
            currentTick = cSPTick
        };
    }

    private void Reconciliate()
    {
        // Makes sure that the ServerSimState is not outdated
        if (serverSimulationState.currentTick <= lastCorrectedFrame) return;

        int cacheIndex = serverSimulationState.currentTick % StateCacheSize;

        ClientInputState cachedInputState = inputStateCache[cacheIndex];
        SimulationState cachedSimulationState = simulationStateCache[cacheIndex];

        // Find the difference between the Server Player Pos And the Client predicted Pos
        float posDif = Vector3.Distance(cachedSimulationState.position, serverSimulationState.position);
        float rotDif = 1f - Quaternion.Dot(serverSimulationState.rotation, cachedSimulationState.rotation);

        // A correction is necessary.
        if (posDif > 0.0001f || rotDif > 0.0001f)
        {
            // Set the player's position to match the server's state. 
            playerMovement.rb.position = serverSimulationState.position;
            playerMovement.speed = serverSimulationState.velocity;
            playerMovement.angularSpeed = serverSimulationState.angularVelocity;
            playerMovement.rb.rotation = serverSimulationState.rotation.normalized;

            // Declare the rewindFrame as we're about to resimulate our cached inputs. 
            ushort rewindTick = serverSimulationState.currentTick;

            // Loop through and apply cached inputs until we're 
            // caught up to our current simulation frame.

            while (rewindTick < cSPTick)
            {
                // Determine the cache index 
                int rewindCacheIndex = rewindTick % StateCacheSize;

                // Obtain the cached input and simulation states.
                ClientInputState rewindCachedInputState = inputStateCache[rewindCacheIndex];
                SimulationState rewindCachedSimulationState = simulationStateCache[rewindCacheIndex];

                // Replace the simulationStateCache index with the new value.
                SimulationState rewoundSimulationState = CurrentSimulationState();
                rewoundSimulationState.currentTick = rewindTick;
                simulationStateCache[rewindCacheIndex] = rewoundSimulationState;

                playerMovement.CheckIfGrounded();

                // Process the cached inputs.
                playerMovement.SetInput(rewindCachedInputState.horizontal, rewindCachedInputState.vertical, rewindCachedInputState.jump);

                // Increase the amount of frames that we've rewound.
                ++rewindTick;
            }
        }
        lastCorrectedFrame = serverSimulationState.currentTick;
    }

    private void SendInput()
    {
        Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.input);


        // First let's send the size of the list of Redundant messages being sent to the server
        // As we send the inputs starting from the last received server tick until our current tick
        // The quantity of message is going to be currentTick - lastReceived tick
        message.AddByte((byte)(cSPTick - serverSimulationState.currentTick));

        // Sends all the messages starting from the last received server tick until our current tick
        for (int i = serverSimulationState.currentTick; i < cSPTick; i++)
        {
            message.AddSByte(inputStateCache[i % StateCacheSize].horizontal);
            message.AddSByte(inputStateCache[i % StateCacheSize].vertical);
            message.AddBool(inputStateCache[i % StateCacheSize].jump);
            message.AddUShort(inputStateCache[i % StateCacheSize].currentTick);
        }
        NetworkManager.Singleton.Client.Send(message);
    }

    [MessageHandler((ushort)ServerToClientId.playerMovement)]
    private static void GetPlayerMovement(Message message)
    {
        // When we receive the processed movement back from the server we save it
        // We have to also verify that the received movement is newer than the one we last received
        ushort tick = message.GetUShort();
        Vector3 speed = message.GetVector3();
        Vector3 angularSpeed = message.GetVector3();
        Vector3 position = message.GetVector3();

        if (tick > PlayerController.Instance.clientMovementInput.serverSimulationState.currentTick)
        {
            PlayerController.Instance.clientMovementInput.serverSimulationState.velocity = speed;
            PlayerController.Instance.clientMovementInput.serverSimulationState.angularVelocity = angularSpeed;
            PlayerController.Instance.clientMovementInput.serverSimulationState.position = position;
            PlayerController.Instance.clientMovementInput.serverSimulationState.currentTick = tick;
        }
    }
}
