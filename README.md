# Client-Side-Prediction-Physics
A step by step guide on client side prediction and server reconciliation in Unity with a **Rigidbody**, using real networking code ([Riptide](https://riptide.tomweiland.net/)) instead of pseudo code.

This is the physics version of my [Client-Side-Prediction](https://github.com/DoctorWh012/Client-Side-Prediction) guide. That one moves the player with a `CharacterController`, this one uses a `Rigidbody` and Unity's physics. The prediction loop is the same in both, so read that one first. Here I only go deep on the parts that change when PhysX is moving your player.

> Same story as the other guide: the first version of this one had logic errors and it's not how I write netcode today, so I rewrote it on top of the new base. If you read the old one, check [What was wrong with the old version](#what-was-wrong-with-the-old-version) at the end.

# What is client side prediction?
Short version, the [long one](https://github.com/DoctorWh012/Client-Side-Prediction#what-is-client-side-prediction) is in the other guide. The server is the authority, but the client doesn't wait for it. The moment you press a key the client runs the same movement code on the same input. When the server's answer comes back it should match. If it doesn't, the client snaps to the server's state for that tick and replays every input after it.

# Why physics makes it harder
`CharacterController.Move()` moves one object. `Physics.Simulate()` steps the whole physics world, every Rigidbody in it. That causes three problems the CharacterController version never had:
1. Replaying 15 inputs means stepping the physics world 15 times in one frame, so we have to decide when physics runs, not Unity
2. The demo runs the server and the client in the same game. If the server's player and the predicted player are in the same physics world they collide with each other, and every replay on the client moves the server's player too
3. The server can't step just one player. If one player has 3 inputs to catch up on and another has none, the one with none still gets pulled by gravity in all 3 steps

Each one gets fixed below.

# Requirements
- Unity 2022.3 (the project is on 2022.3.62f3). `Physics.simulationMode` needs 2022.2 or newer, on older versions use `Physics.autoSimulation = false`
- [Riptide Networking](https://riptide.tomweiland.net/) 2.0.0, already in the package manifest. The concepts work with any networking library

# Running the demo
Open `Assets/Scenes/SampleScene.unity` and press play. The server and the client run in the same process so you can see both:
- **Orange** is your predicted player. It moves the instant you press WASD / Space
- **Green** is the server's copy. It trails behind by your ping

The level has a step you can jump on, a pillar that's too tall to jump on, a ramp up to a platform and walls around the edge. That way the physics has something to collide with.

On the `NetworkManager` you can change **Simulated Ping**, **Simulated Jitter** and **Simulated Packet Loss** while playing, and on the player prefabs **Wanna Move Dumb** makes the player walk and jump around randomly on its own.

The HUD shows the unacknowledged inputs (roughly your ping in ticks) and how many times the client had to reconcile.
> I ran it with 200ms ping, ±20ms jitter and 30% packet loss, with the player jumping around, going up the ramp and hitting walls, and the counter stayed at **0**. It only goes up when the two sides actually disagree, try moving the green capsule in the scene view while playing

# How to
Now let's go step by step. Everything here is in [`PlayerMovement.cs`](Assets/Scripts/Player/PlayerMovement.cs) unless I say otherwise.

## 1) Ticks
Same as the other guide, everything runs at a fixed tick rate and the [`NetworkManager`](Assets/Scripts/Multiplayer/NetworkManager.cs) drives it, so the order is always receive messages -> simulate -> send messages.

The only new thing is the second line:
```cs
Time.fixedDeltaTime = 1f / tickRate;

// Physics only steps when we tell it to, replays need that
Physics.simulationMode = SimulationMode.Script;
```
By default Unity steps physics on its own after every `FixedUpdate`. With `SimulationMode.Script` nothing moves until we call `Simulate()` ourselves. Now we can step once per tick normally, and 15 times in a row when we need to replay.

## 2) Two physics worlds
In a real game the server and the client are different machines and each one has its own physics world. Here they share one process, so we give the server its own physics scene:
```cs
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
```
`LocalPhysicsMode.Physics3D` gives the scene its own `PhysicsScene`. What's inside it only collides with other things inside it, and only moves when that scene's `Simulate()` gets called. We copy the level into it because the server needs the same floor and walls as the client.

When a server player spawns it gets moved in there ([`Player.cs`](Assets/Scripts/Player/Player.cs)):
```cs
// Into the server's physics world, it never touches the client's colliders
SceneManager.MoveGameObjectToScene(player.gameObject, NetworkManager.Singleton.serverScene);
```
The client's predicted player stays in the normal scene, which uses the default physics scene.

There's one catch. `Physics.Raycast` and the other queries only look at the **default** physics scene. A raycast from the server's player would hit the client's level, so every physics query goes through the player's own scene:
```cs
private bool IsGrounded()
{
    // This player's own physics scene, Physics.Raycast would only see the client's
    float distance = capsuleCollider.height * 0.5f + movementSettings.groundCheckDistance;
    return gameObject.scene.GetPhysicsScene().Raycast(rb.position, Vector3.down, distance, groundLayer, QueryTriggerInteraction.Ignore);
}
```
> Easy one to miss. The movement still works, you just get a ground check that looks at the wrong world

## 3) Inputs and states
The input is the same one byte bitmask as the other guide, the reason is [there](https://github.com/DoctorWh012/Client-Side-Prediction#2-inputs-and-states).

The state is still everything the movement code reads that isn't the input. For a Rigidbody that's its position and its velocity:
```cs
public struct SimulationState : IMessageSerializable
{
    public Vector3 position;
    public Vector3 velocity;
    public uint tick;
}
```
> Velocity matters even more with physics. Drag, acceleration, the ramp, landing on the step, all of that carries over from one tick to the next through the velocity

Two things are missing from it on purpose:
- `grounded`. In the other guide it came from the last `Move()`'s collision flags, so it had to be saved. Here it's a raycast from the current position into a level that never moves. Same position, same answer, nothing to save
- Rotation. It's locked with `freezeRotation`, a capsule player doesn't need to tip over. If yours rotates, add `rotation` and `angularVelocity` to the state

The ring buffers indexed by tick are exactly the same as in the other guide.

## 4) The simulation
In the other guide `Simulate` was one function that moved the player. With physics it's two parts: applying the input (forces, drag, jump), then stepping the world.

Both sides run the first part:
```cs
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
```
On the client the step comes right after:
```cs
// Local player only, the server steps its scene once for every player in the NetworkManager
private void Simulate(PlayerInput input)
{
    ApplyInput(input);
    gameObject.scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
}
```
As you can see, `ApplyInput` only reads the input, `rb.position` and `rb.velocity`. Even `rb.drag` gets set from `grounded` every tick, so nothing is left over from the tick before. The movement values come from the `PlayerMovementSettings` ScriptableObject.

The jump sets the vertical velocity directly:
```cs
private void Jump()
{
    // Sets the velocity instead of adding a force, so holding it for 2 grounded ticks is still one jump
    rb.velocity = new Vector3(rb.velocity.x, Mathf.Sqrt(2f * movementSettings.jumpHeight * movementSettings.gravity), rb.velocity.z);
}
```
Why? Simple, on the tick right after jumping you're still close enough to the ground for the raycast to hit. With `AddForce` you'd get a jump with double the force. Setting the velocity gives the same jump no matter how many grounded ticks you hold it, and we don't need a jump cooldown in the state.

The Rigidbody needs some setup too:
```cs
private void SetupRigidbody()
{
    // Gravity comes from the settings and the Visual does the interpolation
    rb.useGravity = false;
    rb.freezeRotation = true;
    rb.interpolation = RigidbodyInterpolation.None;

    // A sleeping body ignores small forces, the other side's copy might still be awake
    rb.sleepThreshold = 0f;
}
```
Watch out for `sleepThreshold`. PhysX puts slow bodies to sleep, and a sleeping body doesn't move until something wakes it up. If the client's copy falls asleep and the server's doesn't (or the other way around) they stop agreeing, so the player never sleeps.

The capsule also has a frictionless `PhysicMaterial` (`Assets/Settings/Frictionless.physicMaterial`). The drag already slows the player down, and with friction the player sticks to walls when you jump into them.

## 5) The client tick
This one is the same as the other guide. Reconcile, read the input, save it, simulate it right away, save the state **after** simulating, send the inputs.
```cs
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
```
Sending the inputs doesn't change at all, every unacknowledged input goes out every tick. It's explained [here](https://github.com/DoctorWh012/Client-Side-Prediction#sending-the-inputs).

## 6) The server
Receiving the inputs is also the same: skip the ones we already have, queue the new ones, throw away the oldest if a backlog builds up ([explained here](https://github.com/DoctorWh012/Client-Side-Prediction#receiving-the-inputs)).

Processing them is the part that changes the most. In the other guide each player ran its own inputs with its own `Move()`. Here one step moves every body in the server scene. So the [`NetworkManager`](Assets/Scripts/Multiplayer/NetworkManager.cs) does the stepping, and on each step every player either brings one input or sits it out:
```cs
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
```
Most of the time that's one step per tick, because inputs arrive one per tick. After jitter or a lost packet a player can have a few queued up. The input budget from the other guide still lets them catch up, and that means extra steps in that tick.

Back in the player:
```cs
public void StartServerTick()
{
    previousTickPosition = rb.position;
    processedInputThisTick = false;

    // One input per tick on average, no speedhack by flooding inputs
    inputBudget = Mathf.Min(inputBudget + 1, MaxInputBudget);
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
```
Now, what does "sits it out" mean? On the client a player moves exactly once per input. Say on the server player A is catching up 3 inputs and player B only has 1. B still gets stepped 3 times, that's 2 extra ticks of gravity, drag and momentum that B's client never simulated, so B mispredicts.

So a player with no input gets frozen for that step:
```cs
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
```
A kinematic body doesn't move during the step. We save its velocity and put it back when the next input arrives, so the player continues exactly where it stopped. In the other guide the server player does the same thing when it has no input, it just waits.
> I tested this with a second server player that gets its inputs in bursts of 4, so the two players are never in step. With the freeze I got **0** reconciliations in 30 seconds. Without it, **447**

After the steps the server sends back the state stamped with the last input it processed, same as before:
```cs
public void EndServerTick()
{
    if (!processedInputThisTick) return;
    SendServerState(CurrentSimulationState(lastProcessedInputTick));
}
```

## 7) Reconciliation
Saving the server's state when it arrives works like in the other guide, and so does `Reconcile`. It compares what we predicted for that tick with what the server says, and if they don't match it rewinds and replays:
```cs
// Rewind to the server's state then replay every input since, each one is a full physics step
ApplySimulationState(latestServerState);
stateBuffer[cacheIndex] = latestServerState;

for (uint tick = serverTick + 1; tick <= currentTick; tick++)
{
    uint replayIndex = tick % BufferSize;
    Simulate(inputBuffer[replayIndex]);
    stateBuffer[replayIndex] = CurrentSimulationState(tick);
}
```
> Trimmed, the full one also handles a server state older than our whole history and the visual smoothing

What's new is that `Simulate` now steps the whole client physics world. That's only fine because the local player is the only thing in the client's world that moves. Check [Going further](#going-further) for when that's not the case.

The rewind sets the Rigidbody's position directly:
```cs
private void ApplySimulationState(SimulationState state)
{
    // Moves the body right away, the transform catches up on the next physics step
    rb.position = state.position;
    rb.velocity = state.velocity;
}
```
`rb.position` teleports the body inside PhysX right away, so everything the replay reads (the ground raycast, the saved state) already sees the rewound position. If you set `transform.position` instead, PhysX only gets it on the next transform sync (`autoSyncTransforms` is off by default), and the first `ApplyInput` of the replay would raycast from the old position. It's the physics version of the `Physics.SyncTransforms()` problem from the other guide.

The mesh smoothing hasn't changed either. The `Visual` child gets interpolated between the last two ticks in `Update`, and corrections ease out over a few frames.

## 8) Faking a bad connection
Nothing changed here. Everything gameplay sends goes through `SendToServer` / `SendToClient` on the `NetworkManager`, which holds messages for half the ping (± jitter) and drops some of the unreliable ones. It's explained [here](https://github.com/DoctorWh012/Client-Side-Prediction#7-faking-a-bad-connection).

# What was wrong with the old version
If you used the old version of this guide, these are the bugs it had:
- **Server and client shared one physics world.** Every player was kept kinematic except the one being simulated, but the server's copy and the predicted copy overlap, so each one was a solid wall for the other to collide with. The freeze in step 6 is the same kinematic idea, now only used where it's needed
- **The ground check ran in `Update`**, outside the tick. When the server processed a message with 5 inputs, all 5 used the same grounded, and a replay used the grounded of the present, not of the tick being replayed
- **The state was saved before applying the input**, but the server sent the state after it. Every comparison was off by one tick
- **Rotation was compared but never sent.** The server sent position and velocities but no rotation, so the client compared its rotation with an all zero one and "corrected" on every single server message
- **The server skipped the newest input** in every message (`i < inputs.Length - 1`)
- **The redundant input count was a `byte` with no cap**, it overflowed if the server didn't answer for 256+ ticks
- **`ushort` ticks**, every "is this newer" check broke once they wrapped around (~17 minutes at 64hz)
- **The fake ping was fake.** `Invoke` delayed *calling* `SendInput`, which then read the inputs from that moment, not the ones that were made
- **The server trusted the client's input rate**, a modified client could speedhack by sending more inputs

# Going further
This covers one predicted Rigidbody. In a real physics game there's more to think about:
- **Other dynamic bodies.** Say there's a crate the player can push. It lives in the client's world too, so every replay steps it again. You either save and rewind it together with the player (the whole world rolls back, Rocket League does this), or you don't predict it and only show it where the server says it is
- **Different machines.** Here the client and the server are the same program on the same CPU, so PhysX gives the exact same results on both. Across machines and builds the floats can drift a tiny bit. That's what `reconciliationThreshold` is for, and Unity's **Enhanced Determinism** setting (Project Settings > Physics) helps
- **Players touching each other.** While a player is frozen it's kinematic, so for that step the player who's catching up hits it like a wall
- **Other players, lag compensation and time dilation**, same as in the [other guide](https://github.com/DoctorWh012/Client-Side-Prediction#going-further)

# Credits
These helped me a lot, both back then and now:
- [Fix Your Timestep](https://gafferongames.com/post/fix_your_timestep/) - Glenn Fiedler
- [Networked Physics](https://gafferongames.com/categories/networked-physics/) - Glenn Fiedler
- [Fast-Paced Multiplayer](https://www.gabrielgambetta.com/client-server-game-architecture.html) - Gabriel Gambetta
- [It IS Rocket Science! The Physics of Rocket League Detailed](https://www.youtube.com/watch?v=ueEmiDM94IE) - Jared Cone, GDC 2018
- [Overwatch Gameplay Architecture and Netcode](https://www.youtube.com/watch?v=W3aieHjyNvw) - Timothy Ford, GDC 2017
- [Client-Side Prediction With Physics In Unity](https://www.codersblock.org/blog/client-side-prediction-in-unity-2018)
- [Seamless fast paced multiplayer in Unity3D](https://medium.com/@christian.tucker_68732/seamless-fast-paced-multiplayer-in-unity3d-implementing-client-side-prediction-ab520bf49bd1)

If anything is wrong or missing feel free to contact me.
