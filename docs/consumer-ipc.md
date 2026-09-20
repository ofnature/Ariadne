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
| `Ariadne.SimpleMove.PathfindInProgress` | `() → bool` | pathfind pending, or a teleport leg in flight (movement not started yet) |
| `Ariadne.SimpleMove.PathfindAndMoveToInteract` | `(ulong gameObjectId, bool fly) → bool` | **interact goal**: path to a live object, stop inside interact range (config 3.5y + its hitbox radius); follows it if it wanders (re-paths on >5y drift), keeps the last known spot if it despawns; false = no such object / busy |
| `Ariadne.SimpleMove.PathfindAndMoveAway` | `(Vector3 from, float distance, bool fly) → bool` | **away goal**: end up ≥ `distance` from `from`, at a reachable mesh point Ariadne picks (ring at the distance, fanning out from the direction away through you); satisfied by distance from `from`, not by reaching the point |
| `Ariadne.SimpleMove.LastResult` | `() → string` | `"goal reached"`, `"N waypoints"`, `"no path (reason)"`, `"stuck (Ny short)"`, `"teleporting to X…"`, `"no such object"` |
| `Ariadne.SimpleMove.GetUseAetherytes` / `SetUseAetherytes` | `() → bool` / `(bool)` | teleport legs on/off for this session (overrides the config toggle) |

Stall recovery is built in, two detectors: hard stall (displacement below threshold —
frozen against a wall) and soft stall (not closing on the destination — the ±4y
tree-wobble that defeats displacement checks). On either, Ariadne re-paths from the
current position; attempts are budgeted by ground gained, not by count — a recovery that
closes ≥10y earns fresh attempts, and only N consecutive futile ones give up. A consumer
sees this only as `IsRunning` staying true a little longer; a give-up looks like a
normal stop.

**Goals** (Baritone-style, `SimpleMove.*`): every move carries a goal that is checked
live each tick while following — `MoveCloseTo` is a *near* goal, `MoveToInteract` an
*interact* goal, `MoveAway` an *away* goal. A satisfied goal ends the path early
(`LastResult` = `"goal reached"`); a plain `MoveTo` (range 0) is only ended by the
follower reaching the last waypoint.

**Teleport legs** (config "Use aetherytes when they save time", default off, or
`SetUseAetherytes`): before pathing, Ariadne compares the ETA of going directly (6 y/s
walk, 20 y/s fly) against teleporting to each attuned aetheryte in the zone and going
from there (teleport cost, default 12 s, plus the trip). When a crystal wins by the
minimum saving (default 5 s) it asks **Lifestream** to teleport, waits for the landing,
then paths from the crystal. Never in a duty, in combat, on a quest vehicle, between
areas, for an away goal, or for `vnavmesh.*` compat calls. Without Lifestream loaded
there are simply no teleport legs. Same-zone aetherytes only; cross-zone travel stays
the consumer's job until Mnemosyne's planner emits teleport legs.

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
requests a volume path (mounted flight / diving).

**Legs.** A path may arrive with `legs` — spans of the waypoint array sharing a movement mode,
each with the transition that starts it (`mnemosyne-protocol.md` → `findPath` → legs).
Ariadne executes what it can: walk/fly semantics switch at leg boundaries, and `land` is
performed properly (a fly leg ending in a walk leg holds the walk leg until the game reports
the character on the ground — a 10 s budget, after which it follows the leg on foot and logs
that it could not land). `mount`, `dismount`, `jumpOff` and `teleport` are parsed and logged
but not executed yet, so a leg carrying one is followed as-is. A path with no legs behaves
exactly as before, and a consumer that ignores legs still sees the flat waypoint list.

## vnavmesh compatibility mode

Dalamud IPC names are one global slot each: the last plugin to register wins, and
unregistering empties the slot whoever filled it. Ariadne therefore manages the
`vnavmesh.*` names by policy, re-checked every ~2 s (plugin list + an ownership probe),
in two modes:

- **Claim when absent** (default on): with vnavmesh not loaded, Ariadne registers the
  full `vnavmesh.*` surface — `Nav.*`, `Query.Mesh.*`, `Path.*`, `SimpleMove.*`,
  `Window.*`, `DTR.*` — with vnavmesh's exact shapes, so existing consumers work
  unmodified. If vnavmesh loads later it takes its names back (its registration
  overwrites Ariadne's) and Ariadne steps aside; if it unloads, Ariadne reclaims them.
- **Take over while loaded** (config, default off): Ariadne registers over vnavmesh's own
  names, so consumers path and move through Ariadne — Mnemosyne paths, Ariadne's follower
  with stall recovery — while vnavmesh stays installed for its viewer, its in-game builds
  and the seeded cache. vnavmesh's own status is unreadable through IPC in this mode (the
  gates answer with Ariadne's state), so the reload nudge and the vnavmesh timings are
  off. Caveat: vnavmesh writes `vnav.PathIsRunning` false on every idle frame into the
  same shared array, so that mirror is unreliable while vnavmesh is loaded — read
  `ariadne.PathIsRunning` instead.

Releasing the names (takeover switched off, compat disabled, or Ariadne unloading)
empties them; vnavmesh registers only at load, so reload it to restore its own set.
Sync-shaped gates (`Query.Mesh.*`, bitmaps) answer via a bounded blocking wait (default
100 ms budget; warm answers take 1–3 ms) and return the not-found fallback rather than
ever throwing or hanging.

## Status

Movement verified in-game 2026-08-23: `SimpleMove.PathfindAndMoveTo` walked the
character on a Mnemosyne-computed path end-to-end (signatures resolve, hooks drive
input). Pathfinding/mesh gates verified earlier. Stall recovery is unit-tested but not
yet exercised against a real wedge; `ReportTraversal` and classified `findPath` results
await Mnemosyne's server side.
