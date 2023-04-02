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
        timer += Time.deltaTime;
        while (timer >= NetworkManager.Singleton.minTimeBetweenTicks)
        {
            timer -= NetworkManager.Singleton.minTimeBetweenTicks;
            int cacheIndex = cSPTick % StateCacheSize;

            // Get the Inputs and store them in the cache
            inputStateCache[cacheIndex] = GetInput();

            // Stores the current SimState on a cache
            simulationStateCache[cacheIndex] = CurrentSimulationState();

            // Applies the movent
            playerMovement.SetInput(inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].jump);

            // Sends a message containing this player input to the server im not the host
            if (!NetworkManager.Singleton.Server.IsRunning) SendInput();

            cSPTick++;
        }
        if (NetworkManager.Singleton.Server.IsRunning) return; // Change

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

            readyToJump = playerMovement.readyToJump,
            coyoteTimeCounter = playerMovement.coyoteTimeCounter,
            jumpBufferCounter = playerMovement.jumpBufferCounter,

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
        print($"Last received tick was {lastCorrectedFrame} and the received one was {serverSimulationState.currentTick}");
        if (serverSimulationState.currentTick <= lastCorrectedFrame) { print($"Stop Recon"); return; }

        int cacheIndex = serverSimulationState.currentTick % StateCacheSize;

        ClientInputState cachedInputState = inputStateCache[cacheIndex];
        SimulationState cachedSimulationState = simulationStateCache[cacheIndex];

        // Find the difference between the Server Player Pos And the Client predicted Pos
        float posDif = Vector3.Distance(cachedSimulationState.position, serverSimulationState.position);
        float rotDif = 1f - Quaternion.Dot(serverSimulationState.rotation, cachedSimulationState.rotation);
        print($"PosDif is {posDif} [C={cachedSimulationState.position} | S {serverSimulationState.position}]");

        // A correction is necessary.
        if (posDif > 0.0001f || rotDif > 0.0001f)
        {
            // Set the player's position to match the server's state. 
            playerMovement.rb.position = serverSimulationState.position;
            playerMovement.speed = serverSimulationState.velocity;
            playerMovement.angularSpeed = serverSimulationState.angularVelocity;
            playerMovement.rb.rotation = serverSimulationState.rotation;

            // Declare the rewindFrame as we're about to resimulate our cached inputs. 
            ushort rewindTick = serverSimulationState.currentTick;

            // Loop through and apply cached inputs until we're 
            // caught up to our current simulation frame.
            print($"Rewinding {cSPTick - rewindTick} Ticks from {rewindTick} to {cSPTick}");

            while (rewindTick < cSPTick)
            {
                // Determine the cache index 
                int rewindCacheIndex = rewindTick % StateCacheSize;

                // Obtain the cached input and simulation states.
                ClientInputState rewindCachedInputState = inputStateCache[rewindCacheIndex];
                SimulationState rewindCachedSimulationState = simulationStateCache[rewindCacheIndex];

                // Replace the simulationStateCache index with the new value.
                print($"The position saved at {rewindCacheIndex} is {simulationStateCache[rewindCacheIndex].position}");

                SimulationState rewoundSimulationState = CurrentSimulationState();
                rewoundSimulationState.currentTick = rewindTick;
                simulationStateCache[rewindCacheIndex] = rewoundSimulationState;

                print($"The position saved at {rewindCacheIndex} is now{simulationStateCache[rewindCacheIndex].position} after sim");

                playerMovement.readyToJump = rewindCachedInputState.readyToJump;
                playerMovement.coyoteTimeCounter = rewindCachedInputState.coyoteTimeCounter;
                playerMovement.jumpBufferCounter = rewindCachedInputState.jumpBufferCounter;

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
        message.AddByte((byte)(cSPTick - serverSimulationState.currentTick));

        // Sends all the messages starting from the last received server tick until our current tick
        print($"Sending {(cSPTick - serverSimulationState.currentTick)}");

        for (int i = serverSimulationState.currentTick; i < cSPTick; i++)
        {
            message.AddSByte(inputStateCache[i % StateCacheSize].horizontal);
            message.AddSByte(inputStateCache[i % StateCacheSize].vertical);
            message.AddBool(inputStateCache[i % StateCacheSize].jump);
            message.AddUShort(inputStateCache[i % StateCacheSize].currentTick);
        }
        NetworkManager.Singleton.Client.Send(message);
    }
}
