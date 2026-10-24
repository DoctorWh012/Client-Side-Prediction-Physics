# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

The **Rigidbody** child of the [Client-Side-Prediction](https://github.com/DoctorWh012/Client-Side-Prediction) guide (`C:\Users\John\Documents\Client-Side-Prediction` locally). Same prediction loop, same file layout and style, but movement goes through PhysX instead of a `CharacterController`. Unity **2022.3.62f3**, **Riptide 2.0.0** (git package in `Packages/manifest.json`). The README is the product: it quotes code from `Assets/Scripts/` verbatim (sometimes with comments trimmed), so **any code change must be mirrored in the matching README snippet** (and vice versa). Anything that didn't change from the parent is linked there instead of re-explained, so keep the shared parts (inputs, redundancy, input queue, fake ping) in sync with the parent repo.

## Building, running, verifying

- No CLI build and no test suite in the repo. Run it by opening `Assets/Scenes/SampleScene.unity` and pressing Play.
- All scripts compile into the default `Assembly-CSharp` (no asmdefs).
- The user usually has the project open in the Editor, which locks it, so batchmode can't run on the repo itself. To compile-check or play-test headlessly, copy `Assets/`, `Packages/`, `ProjectSettings/` and `Library/` into the scratchpad and run the Editor there:
  ```
  "C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe" -batchmode -nographics -projectPath <copy> -runTests -testPlatform PlayMode -testResults <copy>/../results.xml -logFile <copy>/../test.log
  ```
  PlayMode tests need the scripts in an asmdef (`CSP`, referencing `RiptideNetworking` and `Unity.TextMeshPro`) plus a test asmdef. Add both **only in the copy**. For scene or prefab changes, use an `-executeMethod` editor script in the copy, then copy the resulting `.unity`/`.prefab`/`.asset` files and their `.meta` files back. Don't hand-edit scene YAML.
- Script, prefab, material and scene GUIDs match the parent repo on purpose. When adding scripts or folders while the Editor is open, write their `.meta` files yourself, with fresh GUIDs, before Unity generates its own.
- What the README claims was measured that way: 0 reconciliations at 200ms / ±20ms / 30% loss with `wannaMoveDumb`, and with a second server player fed bursty inputs 0 with the freeze vs 447 in 30s without it. Re-measure before changing those numbers.

## Architecture

**Loopback host, two physics worlds.** `NetworkManager` starts a Riptide `Server` *and* `Client` in the same process. `CreateServerScene` makes a `"Server"` scene with `LocalPhysicsMode.Physics3D` and copies the `level` object into it with its renderers off. Server players (green) are moved into that scene on spawn, the predicted player (orange) stays in `SampleScene` (default physics scene). They never collide and only communicate through messages.
- `Player.List` holds the client's players and `Player.ServerList` the server's, same ids, separate dictionaries.
- Every physics query must go through the player's own scene (`gameObject.scene.GetPhysicsScene().Raycast`). `Physics.Raycast` only sees the client's world.

**Tick.** `Time.fixedDeltaTime = 1 / tickRate` (64) and `Physics.simulationMode = SimulationMode.Script`, so nothing moves unless we call `Simulate`. `NetworkManager.FixedUpdate` is the only tick driver: `Server.Update` → `ServerTick()` → `Client.Update` → the local player's `ClientTick()` → `FlushDelayedMessages`.

**Prediction loop** (all in [PlayerMovement.cs](Assets/Scripts/Player/PlayerMovement.cs)):
- `ApplyInput(PlayerInput)` is what both sides run before a physics step (drag, forces, speed cap, gravity, jump). It may only read the input, `rb.position` and `rb.velocity`. Anything new that affects movement must be added to `SimulationState` (and its `Serialize`/`Deserialize`) or replays diverge. `grounded` is a raycast from `rb.position` each tick, so it's not state.
- Client `Simulate` = `ApplyInput` + step the client physics scene. Order in `ClientTick`: `Reconcile()` → `currentTick++` → store input → `Simulate` → store the state **after** simulating → `SendClientInputs()`.
- Server: `HandleClientInputs` is unchanged from the parent. `NetworkManager.ServerTick` calls `StartServerTick` (budget refill), then steps the server scene while any player `HasInputToProcess`, calling `PrepareServerStep` on every player before each step: one input each, players without one are `Freeze()`d (kinematic, velocity kept in `frozenVelocity`) so the extra catch-up steps don't move them. `EndServerTick` sends the state stamped with the last processed input tick. `CurrentSimulationState` reads `frozenVelocity` while kinematic.
- Rewind with `rb.position` / `rb.velocity`, never `transform.position`. Replays step the whole client physics scene, which is only correct while the local player is the only dynamic body there.
- Rigidbody setup is in `SetupRigidbody` (no Unity gravity, frozen rotation, no interpolation, `sleepThreshold = 0` so one side can't fall asleep). The capsule uses `Assets/Settings/Frictionless.physicMaterial`. The `Visual` child is interpolated in `Update`; the root is simulation-only.
- Ticks are `uint` everywhere.

**Network simulation.** Gameplay sends through `NetworkManager.Singleton.SendToServer` / `SendToClient`, which apply `simulatedPing`, `simulatedJitter` and `simulatedPacketLoss` (unreliable only).

**Messages.** IDs are the `ServerToClientId` / `ClientToServerId` enums at the top of `NetworkManager.cs`, implicit values, append only. Handlers are `static` `[MessageHandler]` methods in the class they concern, reading values inline into an instance `Handle*` method.

## Code style (mirrors the author's game KILLNETIC)

Follow the `killnetic-code-style` skill and the parent repo's code. In short:
- Singleton property pattern on managers; `Client`/`Server` as `{ get; private set; }`.
- `#region` blocks `ServerSenders`, `ClientSenders`, `ServerToClientHandlers`, `ClientToServerHandlers` at the bottom, feature regions above (`Ticks`, `Moving`, `Prediction`, `Authority`, `Physics`).
- Senders `Send*`, receivers `Get*`, logic `Handle*`. Single-line `if`s for one statement. `[Header]` + `[Space(5)]` groups, `[SerializeField] public` for component references, explicit types, no `var`.
- Tunables live in the `PlayerMovementSettings` ScriptableObject (`Assets/Settings/PlayerMovement.asset`). Protocol limits are `const` on the class that uses them.
- Logs are lowercase with a bracket tag: `[prediction] ...`, `[disconnect] ...`, `[spawn] ...`.
- Comments are short and say why (what breaks otherwise).
- Keep the `wannaMoveDumb` debug toggle, it's how the demo and tests drive movement without a keyboard.
- Unlike KILLNETIC, don't leave replaced code commented out above the fix. The README quotes these files, so they stay clean.
