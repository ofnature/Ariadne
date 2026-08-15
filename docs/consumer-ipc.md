# Ariadne — consumer integration reference

Everything another Dalamud plugin needs to talk to Ariadne. Written for BossMod-style
movement consumers and for Theseus. All IPC gates are registered with the `Ariadne.`
prefix via `IDalamudPluginInterface.GetIpcProvider`; subscribe with `GetIpcSubscriber`.
Calling a gate when Ariadne isn't loaded throws — wrap in try/catch and treat as absent.

## Detect Ariadne

Any gate works as a presence probe; `Ariadne.IsConnected` is the cheapest:

```csharp
bool present;
try { _pi.GetIpcSubscriber<bool>("Ariadne.IsConnected").InvokeFunc(); present = true; }
catch { present = false; }
```

## Yield to Ariadne movement (BossMod's question: "is someone else driving?")

**Shared data, not IPC** — read it every frame without try/catch overhead:

```csharp
var flag = _pi.GetOrCreateData<bool[]>("ariadne.PathIsRunning", () => [false]);
// flag[0] == true while Ariadne is following a path (walk or fly)
```

- `ariadne.PathIsRunning` — always published. **Target this in new code.**
- `vnav.PathIsRunning` — vnavmesh's tag, mirrored by Ariadne while its
  "Publish vnav.PathIsRunning too" toggle is on (default on). Existing BossMod builds
  that only know the vnav tag yield to Ariadne unmodified. Shared-data tags aren't
  exclusively owned, so this is safe even with vnavmesh loaded alongside.

Ariadne itself honours user input: with "Cancel on input" enabled, a player touching the
movement keys drops the path (`Path.IsRunning` → false, flag → false). Without it,
Ariadne only steers while the player isn't pressing anything (vnavmesh semantics).

## Movement gates (same shapes as vnavmesh's `Path.*` / `SimpleMove.*`)

| Gate | Signature | Notes |
|---|---|---|
| `Ariadne.Path.MoveTo` | `(List<Vector3> waypoints, bool fly)` action | follow a precomputed path |
| `Ariadne.Path.Stop` | action | drops path and any pending pathfind |
| `Ariadne.Path.IsRunning` | `() → bool` | same truth as the shared-data flag |
| `Ariadne.Path.NumWaypoints` | `() → int` | remaining |
| `Ariadne.Path.GetTolerance` / `SetTolerance` | `() → float` / `(float)` | waypoint-pass tolerance (default 0.25) |
| `Ariadne.Path.GetMovementAllowed` / `SetMovementAllowed` | `() → bool` / `(bool)` | pause/resume — path kept, no input written |
| `Ariadne.SimpleMove.PathfindAndMoveTo` | `(Vector3 dest, bool fly) → bool` | pathfind via Mnemosyne, then follow; false = request rejected (already pathfinding / no player) |
| `Ariadne.SimpleMove.PathfindAndMoveCloseTo` | `(Vector3 dest, bool fly, float range) → bool` | stop within `range` |
| `Ariadne.SimpleMove.PathfindInProgress` | `() → bool` | pathfind pending (movement not started yet) |

Stall recovery is built in: on no-progress Ariadne re-paths from the current position up
to N times (config), then stops. A consumer sees this only as `IsRunning` staying true a
little longer; a give-up looks like a normal stop.

## Pathfinding / mesh gates

| Gate | Signature | Notes |
|---|---|---|
| `Ariadne.IsConnected` | `() → bool` | pipe to Mnemosyne up |
| `Ariadne.CurrentCacheKey` | `() → string` | `""` while the layout loads |
| `Ariadne.ZoneStatus` | `() → int` | 0 NotReady · 1 MnemosyneUnavailable · 2 LocalCurrent · 3 MnemosyneCached · 4 Missing |
| `Ariadne.FindPath` | `(Vector3 from, Vector3 to, bool fly) → Task<List<Vector3>>` | empty = no path/unavailable, never throws |
| `Ariadne.RequestMesh` | `() → Task<string>` | path to a current `.navmesh` file, or `""` |
| `Ariadne.SeedVnavCache` | `() → Task<bool>` | hand this zone's mesh to vnavmesh's cache |

**Readiness**: there is no `Nav.IsReady` twin. "Nav can answer for this zone" =
`IsConnected && ZoneStatus is 2 or 3`. Because Mnemosyne holds meshes out of process,
this is true ~0.1 s after zone-in — not after an in-game build.

## Coordinates and units

World coordinates, Y-up, yalms, identical to `IGameObject.Position`; `fly = true`
requests a volume path (mounted flight / diving). Paths today are plain positions; leg
types (walk/fly/link/teleport transitions) arrive with a future protocol bump and will
be additive.

## Status

Movement gates and shared-data flags landed 2026-08-09; hooks vendored from vnavmesh,
in-game verification pending. Pathfinding/mesh gates verified in-game.
