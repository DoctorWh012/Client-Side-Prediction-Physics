# Client-Side-Prediction-Physics
Step by step guide on how to do Client Side Prediction With a RigidBody/PhysicsBased movement system

# This is an adaptation of my [Previous Guide](https://github.com/DoctorWh012/Client-Side-Prediction) made to work on a RigidBody movement system.

# What is client side prediction?
Client side prediction is allowing the client to predict it's own movement in an authoritative server enviroment.
It is used for hiding the latency from the server receiving the input and sending it back to the player.

# Server reconciliation 
Sometimes the client position may drift from the position on the server. As our enviroment is server authoritative we always trust the server
This means snaping the client to the position received from the server and processing the inputs again, resulting in a prediction of what the correct position is.

# Requirements
In this demo i am using [Riptide Networking](https://riptide.tomweiland.net/manual/overview/about-riptide.html) but you can use any networking implementation.

This implementation is made to work with a RigidBody/PhysicsBased movement system. In [This Guide](https://github.com/DoctorWh012/Client-Side-Prediction), I show you how to implement Client Side Prediction with a non PhysicsBased movement system.

# How to
Now let's go step by step on how to implement client side prediction

## 1) Setting a fixed timestep
Instead of using fixed update we are going to set a fixed timestep, the reason for this can be read in this [Article](https://gafferongames.com/post/fix_your_timestep/)

In the NetworkManager we assign our desired TickRate  
```cs
public float ServerTickRate = 64f;
```  

The TickRate is then used on the MovementInput to create a Fixed TimeStep
> Note that you have to create two `float` variables `timer` and `minTimeBetweenTicks`

```cs
private void Start()
{
    minTimeBetweenTicks = 1f / NetworkManager.Singleton.ServerTickRate;
}
```

```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
    }
}
```

## 2) Creating the caches
Now that we have a fixed TimeStep we can create our caches, they will be used to save previous player inputs and positions so we can 
compare to the ones received from the server in the same tick.  
  
We will need a `const int` called StateCacheSize, this will determine the maximum amount of previous Inputs/Positions saved  
```cs
public const int StateCacheSize = 1024;
```  
  
Now we create two classes one for the `InputState` and another one for `SimulationState`
>You can use `struct` if you want, but you will need to change some of the code
```cs
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

    public ushort currentTick;;
}
```
As you can see each of these classes contain a `currentTick` variable, this is so we can tell from when that cachedState is.  

Now we create two arrays to hold both states
```cs
private SimulationState[] simulationStateCache = new SimulationState[StateCacheSize];
private ClientInputState[] inputStateCache = new ClientInputState[StateCacheSize];
```
> As you can see the size of these arrays is defined by the cacheSize  

We also need a way to tell our current Tick, so we create another 'ushort'  variable  
```cs
public ushort cSPTick { get; private set; }
```
> I named this variable as cSPTick but feel free to name it as currentTick if it makes more sense to you  

## 3) Logic
First we increment our `cSPTick` on our fixed TimeStep  
```cs
private void Update()
{
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        cSPTick++;
    }
}
```
  
Now let's get and save the player inputState and simulationState
```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cSPTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
            
        cSPTick++;
    }
}
```
> You can see we use two functions these are
```cs
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

private SimulationState CurrentSimulationState()
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
```
> They simply return the current inputState and simulationState with it's corresponding tick  

You can also notice the use of a `cacheIndex` what it does is that it serves as an index for where to save the current Simulation/Inputs on the arrays and if the `cSPTick` 
goes over the cache size it starts from the beginning

Now that we have cached our inputs and simulation state we can do some actual client side prediction  
Which means we are going to process the result of the inputs before sending them to the server  
```cs 
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cSPTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
    
        playerMovement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

        cSPTick++;
    }
}
```
> As you can see we have a reference to a script called `playerMovement`, `MovementInput` is responsible for getting and sending the player input to the server as well as doing server reconciliation.  
The `PlayerMovement` script is responsible for receiving the player input and moving the player as well as sending the result of the movement back to the client, the `MovementInput` should run only on the localPlayer and the playerMovement should run both in the local and netPlayer

We will have a look on the movement script soon but first let's send the inputs to the server  
```cs
private void Update()
{
    // This creates a fixed timestep to keep the server and the client syncronized
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cSPTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
    
        playerMovement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

        SendInput();

        cSPTick++;
    }
}
```
```cs
private void SendInput()
{
    Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.input);

    message.AddByte((byte)(cSPTick - serverSimulationState.currentTick));

    for (int i = serverSimulationState.currentTick; i < cSPTick; i++)
    {
        message.AddFloat(inputStateCache[i % StateCacheSize].horizontal);
        message.AddFloat(inputStateCache[i % StateCacheSize].vertical);
        message.AddBool(inputStateCache[i % StateCacheSize].jump);
        message.AddUShort(inputStateCache[i % StateCacheSize].currentTick);
    }
    NetworkManager.Singleton.Client.Send(message);
}
```
> As you can see sending the inputs to the server is not as simple as just sending the currentInput, what we are doing here is that we are sending Redundant inputs to the server, so we send all cached inputs starting from the last received StateTick from the server.  
> 
> Why? 
> Simple this prevents the server from missing any inputs if some packet gets lost over the Net.

for this demo i am sending floats for the vertical/horizontal input, but try to keep the size of the message as small as possible this means using the lowest possible number of bytes for each variable


For now this is it before we tackle the Server Reconciliation  

#### Movement Script  

First of all for out client side prediction to work right all of the players RigidBodies have to be disable unless they are simulating an input so in the `Start` function we disable the RigidBody
```cs
private void Start()
{
    // When not simulating an input the player RigidBody must be kinematic
    Physics.autoSimulation = true;
    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    rb.isKinematic = true;
}
```

We also create a `SetInput` function
```cs
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
```

In this function we reference `MovementTick` which is where i apply my movement logic
```cs
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
```
> This function is the largest difference between my other guide, Enabling the RigidBody and disabling AutoSimulation only when simulating the inputs for this player allows us to apply the correct forces to the player RigidBody and Simulate the physics without affecting other connected players.

We also need to receive the inputs sent by the client
```cs
[MessageHandler((ushort)ClientToServerId.input)]
private static void Input(ushort fromClientId, Message message)
{
    byte inputsQuantity = message.GetByte();
    ClientInputState[] inputs = new ClientInputState[inputsQuantity];

    for (int i = 0; i < inputsQuantity; i++)
    {
        inputs[i] = new ClientInputState
        {
            horizontal = message.GetFloat(),
            vertical = message.GetFloat(),
            jump = message.GetBool(),
            currentTick = message.GetUShort()
        };
    }

    PlayerController.Instance.serverPlayerMovement.HandleClientInput(inputs);
    }   
```
>Here we use Riptide's `MessageHandler` to get the input message, then we create an array to save the inputs and set it's size to the `inputQuatity`, after that we just loop in order to get all the messages that the client sent.  
In  this demo i do not have a player class so i just use a placeholder `PlayerController`  to get access to the `serverPlayerMovement` script

Handling the client's input
```cs
private void HandleClientInput(ClientInputState[] inputs)
{
    if (!serverPlayer || inputs.Length == 0) return;
    if (inputs[inputs.Length - 1].currentTick >= lastReceivedInputs.currentTick)
    {
        int start = lastReceivedInputs.currentTick > inputs[0].currentTick ? (lastReceivedInputs.currentTick - inputs[0].currentTick) : 0;

        for (int i = start; i < inputs.Length - 1; i++)
        {
            SetInput(inputs[i].vertical, inputs[i].horizontal, inputs[i].jump);
            SendMovement();
        }
        lastReceivedInputs = inputs[inputs.Length - 1];
    }
}
```
>First we check to see if the length of input array is more than zero, then we check to see if the newest input tick is larger than the ones we received before.  
If it's all ok we simply look where to start applying the inputs and apply them using a loop and we send the resulting movement back to the client
```cs
private void SendMovement(ushort clientTick)
{
    Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.playerMovement);
    message.AddUShort(clientTick);
    message.AddVector3(speed);
    message.AddVector3(angularSpeed);
    message.AddVector3(rb.position);
    NetworkManager.Singleton.Server.SendToAll(message);
}
```
That is all that there is for the Movement Script, now let's go back to the MovementInput so we can receive the resulting movement and check if there's any need for reconciliation.  

#### MovementInput  

First thing we are going to do is to receive the resulting movement from the server.
```cs
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
```
> We just do a check to see if the received server movement is newer than the one we received before

Now finally the last thing we have to do is checking for server reconciliation  
> In the `Update` method we make a call for a `Reconciliate` function
```cs
private void Update()
{
    timer += Time.deltaTime;
    while (timer >= minTimeBetweenTicks)
    {
        timer -= minTimeBetweenTicks;
        int cacheIndex = cSPTick % StateCacheSize;

        inputStateCache[cacheIndex] = GetInput();
        simulationStateCache[cacheIndex] = CurrentSimulationState();
    
        movement.SetInput(inputStateCache[cacheIndex].vertical, inputStateCache[cacheIndex].horizontal, inputStateCache[cacheIndex].jump);

        SendInput();

        cSPTick++;
    }

    if (serverSimulationState != null) Reconciliate();
}
```
Here's the `Reconciliate` function  
```cs
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
```
> I decided to leave the comments in this function as it it a somewhat a larger function than the ones we did before, and also the comments do explain the workings of it very well  
> I would recommend downloading the demo as most of the code is well commented out, and for you to get a feel of how client side prediction should work
  
## 4) Conclusion & Credits
That's all that there is to client side prediction  
In a short manner, we simply cache the player Inputs and state with a Tick value as a timestamp  
We allow the player to move before any of the inputs even hit the server
Then when they do hit the server we process them and send the resulting position back to the client
The client then compares the received movement from the ones cached using the Tick if they do not match for some reason the client snaps to the server position and redoes it's movement using the cached inputs  
  
>I read lot's of articles about client side prediction, but most of them resorted for pseudo networking code, so i decided to write this using actual networking code  

I used these articles as a reference to this project: [Client-Side Prediction With Physics In Unity](https://www.codersblock.org/blog/client-side-prediction-in-unity-2018) & [Seamless fast paced multiplayer in Unity3D](https://medium.com/@christian.tucker_68732/seamless-fast-paced-multiplayer-in-unity3d-implementing-client-side-prediction-ab520bf49bd1)

--If there's anything wrong or missing in this guide feel free to contact me.
