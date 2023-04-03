using UnityEngine;
using Riptide;

public class PlayerMovement : MonoBehaviour
{
    public bool grounded { get; private set; }

    [Header("Components")]
    [SerializeField] public Rigidbody rb;

    [SerializeField] private LayerMask ground;
    [SerializeField] private Transform groundCheck;
    [SerializeField] private MovementSettings movementSettings;
    [SerializeField] private MovementInput movementInput;

    [SerializeField] private bool serverPlayer;

    // Jump Related
    public bool readyToJump = true;
    public float coyoteTimeCounter;
    public float jumpBufferCounter;

    // Movement Related
    private float horizontalInput;
    private float verticalInput;
    private Vector3 moveDirection;
    public Vector3 speed;
    public Vector3 angularSpeed;

    private ClientInputState lastReceivedInputs = new ClientInputState();

    private void Awake()
    {
        rb.freezeRotation = true;
    }
    
    private void Start()
    {
        // When not simulating an input the player RigidBody must be kinematic
        Physics.autoSimulation = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.isKinematic = true;
    }

    private void Update()
    {
        CheckIfGrounded();
    }

    public void SetInput(float horizontal, float vertical, bool jump)
    {
        // Forwards Sideways movement
        horizontalInput = horizontal;
        verticalInput = vertical;

        // Jumping
        if (jump) { jumpBufferCounter = movementSettings.jumpBufferTime; }
        else jumpBufferCounter -= Time.deltaTime;

        MovementTick();
    }

    private void MovementTick()
    {
        // When starting to simulate the player movement we set it to not kinematic
        // We also disable to autoSimulation to enable us to simulate it ourselfs
        Physics.autoSimulation = false;
        rb.isKinematic = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // We restore the previous saved player speed
        rb.velocity = speed;
        rb.angularVelocity = angularSpeed;
        SpeedCap();
        ApplyDrag();

        if (jumpBufferCounter > 0 && coyoteTimeCounter > 0 && readyToJump)
        {
            readyToJump = false;
            Jump();
            Invoke("ResetJump", movementSettings.jumpCooldown);
        }

        ApplyMovement();
        IncreaseFallGravity(movementSettings.gravity);

        // After applying the movement we simulate the physics using the fixedTimeStep
        Physics.Simulate(NetworkManager.Singleton.minTimeBetweenTicks);

        // We then save the player Speed
        speed = rb.velocity;
        angularSpeed = rb.angularVelocity;

        // After the player movement is processed we set him to kinematic again
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.isKinematic = true;
        Physics.autoSimulation = true;
    }

    private void CheckIfGrounded()
    {
        grounded = Physics.Raycast(groundCheck.position, Vector3.down, movementSettings.groundCheckHeight, ground);

        if (grounded) coyoteTimeCounter = movementSettings.coyoteTime;
        else coyoteTimeCounter -= Time.deltaTime;
    }

    private void HandleClientInput(ClientInputState[] inputs)
    {
        if (!serverPlayer || inputs.Length == 0) { print("Returned because inputsLen was 0"); return; }

        // Last input in the array is the newest one
        // Here we check to see if the inputs sent by the client are newer than the ones we already have received
        if (inputs[inputs.Length - 1].currentTick >= lastReceivedInputs.currentTick)
        {
            // Here we check for were to start processing the inputs
            // if the iputs we already have are newer than the first ones sent we start at their difference 
            // if not we start at the first one
            int start = lastReceivedInputs.currentTick > inputs[0].currentTick ? (lastReceivedInputs.currentTick - inputs[0].currentTick) : 0;

            // Now that we have when to start we can simply apply all relevant inputs to the player
            for (int i = start; i < inputs.Length - 1; i++)
            {
                SetInput(inputs[i].horizontal, inputs[i].vertical, inputs[i].jump);
                SendMovement(inputs[i].currentTick);
            }

            // Now we save the client newest input
            lastReceivedInputs = inputs[inputs.Length - 1];
        }
    }

    private void ApplyMovement()
    {
        moveDirection = transform.forward * verticalInput + transform.right * horizontalInput;

        if (grounded) rb.AddForce(moveDirection.normalized * movementSettings.moveSpeed * 10, ForceMode.Force);
        else rb.AddForce(moveDirection.normalized * movementSettings.airMultiplier * 10, ForceMode.Force);
    }

    private void ApplyDrag()
    {
        if (grounded) rb.drag = movementSettings.groundDrag;
        else rb.drag = movementSettings.airDrag;
    }

    private void Jump()
    {
        rb.velocity = new Vector3(rb.velocity.x, 0, rb.velocity.z);

        rb.AddForce(transform.up * movementSettings.jumpForce, ForceMode.Impulse);
        coyoteTimeCounter = 0;
    }

    private void ResetJump()
    {
        readyToJump = true;
    }

    private void SpeedCap()
    {
        Vector3 flatVel = new Vector3(rb.velocity.x, 0, rb.velocity.z);
        if (flatVel.magnitude > movementSettings.moveSpeed)
        {
            Vector3 limitedVel = flatVel.normalized * movementSettings.moveSpeed;
            rb.velocity = new Vector3(limitedVel.x, rb.velocity.y, limitedVel.z);
        }
    }

    private void IncreaseFallGravity(float force)
    {
        rb.AddForce(Vector3.down * force);
    }

    private void SendMovement(ushort clientTick)
    {
        Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.playerMovement);
        message.AddUShort(clientTick);
        message.AddVector3(speed);
        message.AddVector3(angularSpeed);
        message.AddVector3(rb.position);
        NetworkManager.Singleton.Server.SendToAll(message);
    }

    [MessageHandler((ushort)ClientToServerId.input)]
    private static void Input(ushort fromClientId, Message message)
    {
        byte inputsQuantity = message.GetByte();
        ClientInputState[] inputs = new ClientInputState[inputsQuantity];

        for (int i = 0; i < inputsQuantity; i++)
        {
            inputs[i] = new ClientInputState
            {
                horizontal = message.GetSByte(),
                vertical = message.GetSByte(),
                jump = message.GetBool(),
                currentTick = message.GetUShort()
            };
        }

        PlayerController.Instance.serverPlayermovement.HandleClientInput(inputs);
    }
}
