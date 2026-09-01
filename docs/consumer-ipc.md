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
| `Ariadne.Path.MoveTo` | `(List<Vector3> waypoints, bool fly)` action | follow a precomputed path — **your path is yours**: Ariadne never mesh-re-paths waypoints you supplied (see stall note below) |
| `Ariadne.Path.MoveToWithTolerance` | `(List<Vector3> waypoints, bool fly, float tolerance)` action | same, with per-path waypoint tolerance (global `SetTolerance` untouched) |
| `Ariadne.Path.StallCount` | `() → int` | stalls detected on the current path (reset each Move/Stop); a rise on your supplied path = re-plan it yourself |
| `Ariadne.Path.SteerTo` | `(Vector3 dest)` action | continuous direct steer through the input hook — no waypoints/mesh/stall machinery; re-issue per tick; auto-stops ≤0.5y; sets the PathIsRunning flag |
| `Ariadne.Path.IsSteering` | `() → bool` | |
| `Ariadne.Path.RemainingDistance` | `() → float` | yalms left (steer or along path), -1 idle — deadline arithmetic for dodges |

**Readiness without exceptions:** shared data `ariadne.NavReady` (`bool[]`, same pattern
as the PathIsRunning flag) mirrors `IsConnected && ZoneStatus is 2 or 3` every frame —
poll it instead of try/catching gate calls.
| `Ariadne.Path.Stop` | action | drops path and any pending pathfind |
| `Ariadne.Path.IsRunning` | `() → bool` | same truth as the shared-data flag |
| `Ariadne.Path.NumWaypoints` | `() → int` | remaining |
| `Ariadne.Path.GetTolerance` / `SetTolerance` | `() → float` / `(float)` | waypoint-pass tolerance (default 0.25) |
| `Ariadne.Path.GetMovementAllowed` / `SetMovementAllowed` | `() → bool` / `(bool)` | pause/resume — path kept, no input written |
| `Ariadne.SimpleMove.PathfindAndMoveTo` | `(Vector3 dest, bool fly) → bool` | pathfind via Mnemosyne, then follow; false = request rejected (already pathfinding / no player) |
| `Ariadne.SimpleMove.PathfindAndMoveCloseTo` | `(Vector3 dest, bool fly, float range) → bool` | stop within `range` |
| `Ariadne.SimpleMove.PathfindInProgress` | `() → bool` | pathfind pending (movement not started yet) |

Stall recovery is built in, two detectors: hard stall (displacement below threshold —
frozen against a wall) and soft stall (not closing on the destination — the ±4y
tree-wobble that defeats displacement checks). On either, Ariadne re-paths from the
current position; attempts are budgeted by ground gained, not by count — a recovery that
closes ≥10y earns fresh attempts, and only N consecutive futile ones give up. A consumer
sees this only as `IsRunning` staying true a little longer; a give-up looks like a
normal stop.

**Recovery applies only to paths Ariadne computed itself** (`SimpleMove.*`). A path you
supplied via `Path.MoveTo` is never mesh-re-pathed — your waypoints may encode knowledge
the mesh lacks (danger-aware dodge corners), so on a stall Ariadne keeps following them
and increments `Path.StallCount`; re-planning is the owner's job
(`docs/externally-supplied-paths.md` has the full rationale).

## Pathfinding / mesh gates

| Gate | Signature | Notes |
|---|---|---|
| `Ariadne.IsConnected` | `() → bool` | pipe to Mnemosyne up |
| `Ariadne.CurrentCacheKey` | `() → string` | `""` while the layout loads |
| `Ariadne.ZoneStatus` | `() → int` | 0 NotReady · 1 MnemosyneUnavailable · 2 LocalCurrent · 3 MnemosyneCached · 4 Missing |
| `Ariadne.FindPath` | `(Vector3 from, Vector3 to, bool fly) → Task<List<Vector3>>` | empty = no path/unavailable, never throws |
| `Ariadne.RequestMesh` | `() → Task<string>` | path to a current `.navmesh` file, or `""` |
| `Ariadne.SeedVnavCache` | `() → Task<bool>` | hand this zone's mesh to vnavmesh's cache |
| `Ariadne.ReportTraversal` | `(Vector3 from, Vector3 to, string mode, bool success) → Task<bool>` | feed the mesh-learning channel: `mode` `"direct"` + `success` = "I drove through where the mesh said no" (off-mesh-link evidence — Yedlihmad doorways); `success:false` = a planned route failed there. Best-effort; false = not recorded |

**Readiness**: there is no `Nav.IsReady` twin. "Nav can answer for this zone" =
`IsConnected && ZoneStatus is 2 or 3`. Because Mnemosyne holds meshes out of process,
this is true ~0.1 s after zone-in — not after an in-game build.

## Coordinates and units

World coordinates, Y-up, yalms, identical to `IGameObject.Position`; `fly = true`
requests a volume path (mounted flight / diving). Paths today are plain positions; leg
types (walk/fly/link/teleport transitions) arrive with a future protocol bump and will
be additive.

## vnavmesh compatibility mode

When the real vnavmesh plugin is **not** loaded (and "Claim vnavmesh.* IPC gates" is on,
the default), Ariadne registers the full `vnavmesh.*` surface — `Nav.*`, `Query.Mesh.*`,
`Path.*`, `SimpleMove.*`, `Window.*`, `DTR.*` — with vnavmesh's exact shapes, so existing
consumers work unmodified. Sync-shaped gates (`Query.Mesh.*`, bitmaps) answer via a
bounded blocking wait (default 100 ms budget; warm answers take 1–3 ms) and return the
not-found fallback rather than ever throwing or hanging. Checked once at plugin load: if
vnavmesh is installed, its gates are left untouched.

## Status

Movement verified in-game 2026-08-23: `SimpleMove.PathfindAndMoveTo` walked the
character on a Mnemosyne-computed path end-to-end (signatures resolve, hooks drive
input). Pathfinding/mesh gates verified earlier. Stall recovery is unit-tested but not
yet exercised against a real wedge; `ReportTraversal` and classified `findPath` results
await Mnemosyne's server side.
