using Riptide;
using System;
using UnityEngine;
using System.Collections.Generic;

[Flags]
public enum InputButtons : byte
{
    None = 0,
    Forward = 1 << 0,
    Backward = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Jump = 1 << 4,
}

public struct PlayerInput
{
    public InputButtons buttons;
    public uint tick;

    public bool IsPressed(InputButtons button)
    {
        return (buttons & button) != 0;
    }
}

public struct SimulationState : IMessageSerializable
{
    public Vector3 position;
    public Vector3 velocity;
    public uint tick;

    public void Deserialize(Message message)
    {
        position = message.GetVector3();
        velocity = message.GetVector3();
        tick = message.GetUInt();
    }

    public void Serialize(Message message)
    {
        message.AddVector3(position);
        message.AddVector3(velocity);
        message.AddUInt(tick);
    }
}

public class PlayerMovement : MonoBehaviour
{
    public const uint BufferSize = 1024;        // ~16s of inputs/states at 64hz, way more than any ping we'd ever reconcile across
    private const uint MaxRedundantInputs = 64; // ~1s of unacknowledged inputs resent every tick
    private const int MaxInputBudget = 8;       // ticks of inputs the server can catch up in one go after a packet burst
    private const int MaxQueuedInputs = 8;      // ~125ms, more than that waiting on the server is a backlog, not jitter

    [Header("Components")]
    [Space(5)]
    [SerializeField] private Player player;
    [SerializeField] public Rigidbody rb;
    [SerializeField] private CapsuleCollider capsuleCollider;
    [SerializeField] private Transform visual;

    [Header("Checkers")]
    [Space(5)]
    [SerializeField] private LayerMask groundLayer;

    [Header("Scriptables")]
    [Space(5)]
    [SerializeField] private PlayerMovementSettings movementSettings;

    [Header("Debugging")]
    [Space(5)]
    [SerializeField] private bool wannaMoveDumb; // Random inputs instead of the keyboard

    // Simulation
    [SerializeField] private bool grounded;

    // Prediction (local player)
    [SerializeField] private uint currentTick;
    [SerializeField] private uint lastAckedTick;
    private bool hasNewServerState;
    private SimulationState latestServerState;
    private readonly PlayerInput[] inputBuffer = new PlayerInput[BufferSize];
    private readonly SimulationState[] stateBuffer = new SimulationState[BufferSize];
    public uint UnackedInputs { get { return currentTick - lastAckedTick; } }
    public int ReconciliationCount { get; private set; }

    // Authority (server side player)
    [SerializeField] private uint lastReceivedInputTick;
    [SerializeField] private uint lastProcessedInputTick;
    [SerializeField] private int inputBudget;
    private bool processedInputThisTick;
    private Vector3 frozenVelocity;
    private readonly Queue<PlayerInput> inputQueue = new Queue<PlayerInput>();

    // Visuals
    private Vector3 previousTickPosition;
    private Vector3 visualCorrectionOffset;

    // Debugging
    private InputButtons dumbInput;
    private float nextDumbInputTime;

    private void Awake()
    {
        previousTickPosition = transform.position;
        SetupRigidbody();
    }

    private void Update()
    {
        // Mesh is interpolated between the last two ticks, corrections ease out
        visualCorrectionOffset = Vector3.Lerp(visualCorrectionOffset, Vector3.zero, 1f - Mathf.Exp(-movementSettings.correctionSmoothing * Time.deltaTime));

        float tickFraction = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
        visual.position = Vector3.Lerp(previousTickPosition, transform.position, tickFraction) + visualCorrectionOffset;
    }

    private void SetupRigidbody()
    {
        // Gravity comes from the settings and the Visual does the interpolation
        rb.useGravity = false;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.None;

        // A sleeping body ignores small forces, the other side's copy might still be awake
        rb.sleepThreshold = 0f;
    }

    #region Ticks
    // Local player, called by the NetworkManager every tick
    public void ClientTick()
    {
        // Fix the past before predicting the new tick
        Reconcile();

        currentTick++;
        uint cacheIndex = currentTick % BufferSize;

        PlayerInput input = new PlayerInput { buttons = GetInput(), tick = currentTick };
        inputBuffer[cacheIndex] = input;

        previousTickPosition = rb.position;
        Simulate(input);

        // Saved AFTER simulating, it's what the server sends back for this tick
        stateBuffer[cacheIndex] = CurrentSimulationState(currentTick);

        SendClientInputs();
    }

    // Local player only, the server steps its scene once for every player in the NetworkManager
    private void Simulate(PlayerInput input)
    {
        ApplyInput(input);
        gameObject.scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
    }
    #endregion

    #region Moving
    // Both sides run this before a physics step, it can only read the input and what's in SimulationState
    private void ApplyInput(PlayerInput input)
    {
        grounded = IsGrounded();

        ApplyDrag();
        ApplyMovement(GetMoveDir(input));
        SpeedCap();
        ApplyGravity();

        if (grounded && input.IsPressed(InputButtons.Jump)) Jump();
    }

    private void ApplyDrag()
    {
        rb.drag = grounded ? movementSettings.groundDrag : movementSettings.airDrag;
    }

    private void ApplyMovement(Vector3 moveDir)
    {
        float acceleration = grounded ? movementSettings.acceleration : movementSettings.acceleration * movementSettings.airMultiplier;
        rb.AddForce(moveDir * acceleration, ForceMode.Acceleration);
    }

    private void SpeedCap()
    {
        Vector3 flatVel = new Vector3(rb.velocity.x, 0, rb.velocity.z);
        if (flatVel.magnitude <= movementSettings.moveSpeed) return;

        Vector3 limitedVel = flatVel.normalized * movementSettings.moveSpeed;
        rb.velocity = new Vector3(limitedVel.x, rb.velocity.y, limitedVel.z);
    }

    private void ApplyGravity()
    {
        rb.AddForce(Vector3.down * movementSettings.gravity, ForceMode.Acceleration);
    }

    private void Jump()
    {
        // Sets the velocity instead of adding a force, so holding it for 2 grounded ticks is still one jump
        rb.velocity = new Vector3(rb.velocity.x, Mathf.Sqrt(2f * movementSettings.jumpHeight * movementSettings.gravity), rb.velocity.z);
    }

    private bool IsGrounded()
    {
        // This player's own physics scene, Physics.Raycast would only see the client's
        float distance = capsuleCollider.height * 0.5f + movementSettings.groundCheckDistance;
        return gameObject.scene.GetPhysicsScene().Raycast(rb.position, Vector3.down, distance, groundLayer, QueryTriggerInteraction.Ignore);
    }

    private static Vector3 GetMoveDir(PlayerInput input)
    {
        Vector3 moveDir = Vector3.zero;
        if (input.IsPressed(InputButtons.Forward)) moveDir.z += 1;
        if (input.IsPressed(InputButtons.Backward)) moveDir.z -= 1;
        if (input.IsPressed(InputButtons.Right)) moveDir.x += 1;
        if (input.IsPressed(InputButtons.Left)) moveDir.x -= 1;

        // Diagonals would be ~1.41x faster otherwise
        return moveDir.normalized;
    }

    private InputButtons GetInput()
    {
        if (wannaMoveDumb) return GetDumbInput();

        InputButtons buttons = InputButtons.None;
        float vertical = Input.GetAxisRaw("Vertical");
        float horizontal = Input.GetAxisRaw("Horizontal");

        if (vertical > 0) buttons |= InputButtons.Forward;
        else if (vertical < 0) buttons |= InputButtons.Backward;
        if (horizontal > 0) buttons |= InputButtons.Right;
        else if (horizontal < 0) buttons |= InputButtons.Left;
        if (Input.GetKey(KeyCode.Space)) buttons |= InputButtons.Jump;

        return buttons;
    }

    private InputButtons GetDumbInput()
    {
        if (Time.time < nextDumbInputTime) return dumbInput;

        dumbInput = (InputButtons)UnityEngine.Random.Range(0, (int)InputButtons.Jump << 1);
        nextDumbInputTime = Time.time + UnityEngine.Random.Range(0.25f, 1.5f);
        return dumbInput;
    }
    #endregion

    #region Prediction
    public SimulationState CurrentSimulationState(uint tick)
    {
        return new SimulationState
        {
            position = rb.position,
            velocity = rb.isKinematic ? frozenVelocity : rb.velocity, // Frozen server players keep it aside
            tick = tick
        };
    }

    private void ApplySimulationState(SimulationState state)
    {
        // Moves the body right away, the transform catches up on the next physics step
        rb.position = state.position;
        rb.velocity = state.velocity;
    }

    private void HandleServerState(SimulationState serverState)
    {
        // Unreliable, only the newest state matters
        if (serverState.tick <= lastAckedTick) return;

        if (serverState.tick > currentTick)
        {
            Debug.LogWarning($"[prediction] server sent a state for tick {serverState.tick} but we are only on tick {currentTick}, ignoring");
            return;
        }

        latestServerState = serverState;
        lastAckedTick = serverState.tick;
        hasNewServerState = true;
    }

    private void Reconcile()
    {
        if (!hasNewServerState) return;
        hasNewServerState = false;

        uint serverTick = latestServerState.tick;

        // Older than our history, just take the server's state
        if (currentTick - serverTick >= BufferSize)
        {
            Debug.LogWarning($"[prediction] server state for tick {serverTick} is older than our history ({BufferSize} ticks), snapping to it");
            ApplySimulationState(latestServerState);
            return;
        }

        // What we predicted for that same tick
        uint cacheIndex = serverTick % BufferSize;
        SimulationState predictedState = stateBuffer[cacheIndex];

        float positionError = Vector3.Distance(predictedState.position, latestServerState.position);
        float velocityError = Vector3.Distance(predictedState.velocity, latestServerState.velocity);
        if (positionError <= movementSettings.reconciliationThreshold && velocityError <= movementSettings.reconciliationThreshold) return;

        ReconciliationCount++;
        Debug.LogWarning($"[prediction] mispredicted tick {serverTick} by {positionError:F3}m, replaying {currentTick - serverTick} inputs");

        Vector3 positionBeforeCorrection = rb.position;

        // Rewind to the server's state then replay every input since, each one is a full physics step
        ApplySimulationState(latestServerState);
        stateBuffer[cacheIndex] = latestServerState;

        for (uint tick = serverTick + 1; tick <= currentTick; tick++)
        {
            uint replayIndex = tick % BufferSize;
            Simulate(inputBuffer[replayIndex]);
            stateBuffer[replayIndex] = CurrentSimulationState(tick);
        }

        // Simulation snapped, the mesh eases into place in Update
        visualCorrectionOffset += positionBeforeCorrection - rb.position;
        if (visualCorrectionOffset.magnitude > movementSettings.maxSmoothedCorrection) visualCorrectionOffset = Vector3.zero;
    }
    #endregion

    #region Authority
    private void HandleClientInputs(uint firstTick, byte[] inputs)
    {
        for (int i = 0; i < inputs.Length; i++)
        {
            uint tick = firstTick + (uint)i;

            // Redundant copy of an input we already have
            if (tick <= lastReceivedInputTick) continue;

            // Lost more than the redundancy covers, the client will reconcile
            if (tick > lastReceivedInputTick + 1) Debug.LogWarning($"[prediction] client {player.Id} inputs {lastReceivedInputTick + 1}-{tick - 1} never arrived, skipping them");

            inputQueue.Enqueue(new PlayerInput { buttons = (InputButtons)inputs[i], tick = tick });
            lastReceivedInputTick = tick;
        }

        // A backlog never drains and becomes permanent input delay, drop the oldest
        if (inputQueue.Count <= MaxQueuedInputs) return;

        uint firstDroppedTick = inputQueue.Peek().tick;
        while (inputQueue.Count > MaxQueuedInputs) inputQueue.Dequeue();
        Debug.LogWarning($"[prediction] client {player.Id} input queue backed up, dropped inputs {firstDroppedTick}-{inputQueue.Peek().tick - 1}");
    }

    // Server players, called by the NetworkManager every tick before any physics step
    public void StartServerTick()
    {
        previousTickPosition = rb.position;
        processedInputThisTick = false;

        // One input per tick on average, no speedhack by flooding inputs
        inputBudget = Mathf.Min(inputBudget + 1, MaxInputBudget);
    }

    public bool HasInputToProcess()
    {
        return inputBudget > 0 && inputQueue.Count > 0;
    }

    // Called before every server physics step, one input per step or the player sits it out
    public void PrepareServerStep()
    {
        if (!HasInputToProcess())
        {
            Freeze();
            return;
        }

        Unfreeze();

        PlayerInput input = inputQueue.Dequeue();
        ApplyInput(input);
        lastProcessedInputTick = input.tick;
        inputBudget--;
        processedInputThisTick = true;
    }

    // Server players, called by the NetworkManager after the physics steps
    public void EndServerTick()
    {
        if (!processedInputThisTick) return;
        SendServerState(CurrentSimulationState(lastProcessedInputTick));
    }

    // Kinematic bodies don't move in a step, gravity would pull the player without an input otherwise
    private void Freeze()
    {
        if (rb.isKinematic) return;

        frozenVelocity = rb.velocity;
        rb.isKinematic = true;
    }

    private void Unfreeze()
    {
        if (!rb.isKinematic) return;

        rb.isKinematic = false;
        rb.velocity = frozenVelocity;
    }
    #endregion

    #region ServerSenders
    private void SendServerState(SimulationState simulationState)
    {
        Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.playerState);
        message.AddUShort(player.Id);
        message.AddSerializable(simulationState);
        NetworkManager.Singleton.SendToClient(message, player.Id);
    }
    #endregion

    #region ClientSenders
    private void SendClientInputs()
    {
        // Resends every unacked input, ticks are consecutive so only the first one is sent
        uint firstTick = currentTick - lastAckedTick > MaxRedundantInputs ? currentTick - MaxRedundantInputs + 1 : lastAckedTick + 1;

        byte[] inputs = new byte[currentTick - firstTick + 1];
        for (int i = 0; i < inputs.Length; i++) inputs[i] = (byte)inputBuffer[(firstTick + (uint)i) % BufferSize].buttons;

        Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.playerInputs);
        message.AddUInt(firstTick);
        message.AddBytes(inputs);
        NetworkManager.Singleton.SendToServer(message);
    }
    #endregion

    #region ServerToClientHandlers
    [MessageHandler((ushort)ServerToClientId.playerState)]
    private static void GetServerState(Message message)
    {
        if (Player.List.TryGetValue(message.GetUShort(), out Player player))
        {
            if (!player.IsLocal) return;
            player.playerMovement.HandleServerState(message.GetSerializable<SimulationState>());
        }
    }
    #endregion

    #region ClientToServerHandlers
    [MessageHandler((ushort)ClientToServerId.playerInputs)]
    private static void GetClientInputs(ushort fromClientId, Message message)
    {
        if (Player.ServerList.TryGetValue(fromClientId, out Player player))
        {
            player.playerMovement.HandleClientInputs(message.GetUInt(), message.GetBytes());
        }
    }
    #endregion
}
