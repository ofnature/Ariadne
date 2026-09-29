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
| `Ariadne.SimpleMove.PathfindAndMoveCloseTo` | `(Vector3 dest, bool fly, float range) → bool` | stop within `range` **of `dest`** — measured to the target itself, in 3D, not to the route's last waypoint. For an off-mesh target (an NPC behind a counter) the route ends at the closest reachable point; if that is inside `range` the move ends `"goal reached"`, otherwise it walks the whole route and reports how far short it ended |
| `Ariadne.SimpleMove.PathfindInProgress` | `() → bool` | pathfind pending, a teleport leg in flight (movement not started yet), or a zone's flight volume still loading — a `meshNotReady` answer is retried for ~5 s before it is reported |
| `Ariadne.SimpleMove.PathfindAndMoveToInteract` | `(ulong gameObjectId, bool fly) → bool` | **interact goal**: path to a live object, stop inside interact range (config 3.5y + its hitbox radius); follows it if it wanders (re-paths on >5y drift), keeps the last known spot if it despawns; false = no such object / busy |
| `Ariadne.SimpleMove.PathfindAndMoveAway` | `(Vector3 from, float distance, bool fly) → bool` | **away goal**: end up ≥ `distance` from `from`, at a reachable mesh point Ariadne picks (ring at the distance, fanning out from the direction away through you); satisfied by distance from `from`, not by reaching the point |
| `Ariadne.SimpleMove.LastResult` | `() → string` | While moving: `"pathfinding…"`, `"N waypoints"`, `"teleporting to X…"`, `"waiting for mesh"` (retrying a `meshNotReady` zone volume). When it ends: `"goal reached"`; `"closest reachable point, N.Ny short"` (the route was walked to its end and the mesh allows no closer — retrying will not help); `"stuck (Ny short)"` (stall recovery gave up); `"no path (reason)"` (reasons include `startOffSurface`: the route began on another level than the one the character stands on, and no surface was found nearby to plan from); `"stopped"` (cancelled, e.g. by player input); `"zone changed"`; `"no such object"`. A finished move never stays at `"N waypoints"` |
| `Ariadne.SimpleMove.GetUseAetherytes` / `SetUseAetherytes` | `() → bool` / `(bool)` | teleport legs on/off for this session (overrides the config toggle) |
| `Ariadne.SimpleMove.GetPreferFlying` / `SetPreferFlying` | `() → bool` / `(bool)` | fly-when-able on/off for this session (overrides the config toggle) |

Stall recovery is built in, two detectors: hard stall (displacement below threshold —
frozen against a wall) and soft stall (not closing on the destination — the ±4y
tree-wobble that defeats displacement checks). On either, Ariadne re-paths from the
current position; attempts are budgeted by ground gained, not by count — a recovery that
closes ≥10y earns fresh attempts, and only N consecutive futile ones give up. A consumer
sees this only as `IsRunning` staying true a little longer; a give-up looks like a
normal stop.

**A walking route begins where the character stands** (2026-09-28). Meshes served by Mnemosyne
still contain islands nobody can stand on — flat planes under the terrain among them — and the
planner snaps a position to the nearest poly within five yalms. Where the surface mesh has a
gap, the start can land on a plane underneath and the route runs underground. For `SimpleMove.*`
Ariadne checks the route's first waypoint against the character's real height: more than 2.5 y
off, and it looks for the surface within 6 y, re-plans from there, or refuses with
`no path (startOffSurface)`. Not applied while flying, swimming or diving. **`Nav.Pathfind*`
returns the planner's answer as it is** — if you follow those waypoints yourself, compare
`waypoints[0].Y` with the character's before you trust the route.

**Arrival is the goal's, not the route's** (fixed 2026-09-27, reported by SealBreaker; verified in-game the same day at the Hall of Flames counter: range 3 stops within 3 y, `"goal reached"`). Paths
Ariadne computes for `SimpleMove.*` run with a destination tolerance of 0 and end when the
goal says so, measured against the target. Before this the follower ended the path inside
`range` of the route's *last waypoint* — for the Flame Personnel Officer that waypoint is the
counter edge, 2 y from the NPC, so a range of 3 ended the move up to 5 y away. `Path.SetTolerance`
still governs how corners are passed, but on these paths the final waypoint is only passed
within 0.25 y, whatever the tolerance: a consumer that sets 3.0 no longer ends every route 3 y
early. Paths you supply yourself (`Path.MoveTo`, `Path.MoveToWithTolerance`) keep vnavmesh's
rules unchanged.

**Goals** (Baritone-style, `SimpleMove.*`): every move carries a goal that is checked
live each tick while following — `MoveCloseTo` is a *near* goal, `MoveToInteract` an
*interact* goal, `MoveAway` an *away* goal. A satisfied goal ends the path early
(`LastResult` = `"goal reached"`); a plain `MoveTo` (range 0) is only ended by the
follower reaching the last waypoint.

**Fly when able** (config "Fly whenever the zone allows it", default off, or `SetPreferFlying`):
a move asked for as a walk becomes a flight when flight is unlocked in the current zone and the
trip is at least the configured distance (default 50 y). "Unlocked" is the game's own answer for
the territory's aether current set, which covers A Realm Reborn zones once the story has
unlocked them. Ariadne then does the whole trip itself: it calls the mount (mount roulette), flies
the route, lands at its end, and puts the mount away again — `PathfindInProgress` stays true until
the character is back on foot, so a consumer that waits on it can interact straight away. While
in the air on a mount Ariadne called, a ranged goal does not end the move overhead: the route is
flown to its end on the ground first. Where walking is quicker the planner answers with the
ground route and no mount is called. If the mount will not come out within 8 s the move falls
back to walking. A flight the caller asked for itself (`fly: true`) also gets the mount called
when the character is on foot, but keeps the mount at the end — that caller manages its own.
Never in a duty, in combat, on a quest vehicle, for an away goal, or for `vnavmesh.*` compat calls.
`LastResult` reads `"calling the mount…"` while it waits.

**Teleport legs** (config "Use aetherytes when they save time", default off, or
`SetUseAetherytes`): before pathing, Ariadne compares the ETA of going directly (6 y/s
walk, 20 y/s fly) against teleporting to each attuned aetheryte in the zone and going
from there (teleport cost, default 12 s, plus the trip). When a crystal wins by the
minimum saving (default 5 s) it asks **Lifestream** to teleport, waits for the landing,
then paths from the crystal. Never in a duty, in combat, on a quest vehicle, between
areas, for an away goal, or for `vnavmesh.*` compat calls. Without Lifestream loaded
there are simply no teleport legs. Same-zone aetherytes only; cross-zone travel stays
the consumer's job until Mnemosyne's planner emits teleport legs.

The `vnavmesh.*` compat gates pass through without ever planning a teleport (enforced in code
since 2026-09-28; before that the rule was documented but not applied). A trip too short for any
crystal to pay is decided on arithmetic alone, and the character's attunements are read at most
once every 30 s: reading them makes the game rebuild its teleport list, and the first version
did that over two hundred times per move request, which froze the client for about a second.

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
| `Ariadne.CaptureZone` | `() → Task<bool>` | capture the live layout and rebuild this zone **even though a mesh already exists** — the only route to a festival or shared-group variant, since the automatic path fires on a cache miss only. False = zone not ready |
| `Ariadne.ReportTraversal` | `(Vector3 from, Vector3 to, string mode, bool success) → Task<bool>` | feed the mesh-learning channel: `mode` `"direct"` + `success` = "I drove through where the mesh said no" (off-mesh-link evidence — Yedlihmad doorways); `success:false` = a planned route failed there. Best-effort; false = not recorded |
| `Ariadne.Query.Mesh.ReachableCells` | `(Vector3 from, float radius, float cellSize, float minY, float maxY) → Task<(string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth, int[] Columns, float[] Heights, byte[] States, bool ReachableOutside)>` | **live both sides 2026-09-19.** Which walkable ground is reachable from `from`, as a world-aligned grid of stacked surfaces (`States`: 1 reachable · 2 cutOff; a column with no surface has no mesh). `minY`/`maxY` = `float.NaN` for no height band. Full semantics: `mnemosyne-protocol.md` → `reachableCells` |

**ReachableCells** is for exploration (Theseus's auto-solver): find ground not yet visited, and
the edges where walkable mesh is cut off. It returns reachability only. Keep visited state on your
side, per run. The grid snaps to world-aligned cells, so answers with the same `cellSize` line up
cell for cell and one visited set can span many queries. The surfaces are three parallel arrays:
`Columns[i]` (`zi * Width + xi`), `Heights[i]` and `States[i]` describe surface `i`, and a column
can hold several (stacked floors). Treat a `cutOff` surface next to a `reachable` one as a gate only
when their heights match; otherwise it is another storey. `ReachableOutside = false` means nothing
reachable lies beyond the window, so an exhausted grid is really exhausted. `Result` uses the
`findPath` vocabulary (`ok`, `startOffMesh`, `meshNotReady`, `serviceUnavailable`, `failed`) and
the arrays are empty unless it is `ok`. The return is a `ValueTuple` of BCL types, like
`Nav.PathfindDetailed`, so no shared assembly is needed. It is `Task`-returning and never
sync-shaped: await it off the framework thread.

**Client status (2026-09-19).** Mnemosyne implements the op as of the same date, so the gate is
live end to end — but the degradation below is what keeps it honest against an older service.
The gate answers honestly whatever the server does:
`failed` when Mnemosyne does not know the op yet (the server's `unknown op 'reachableCells'`
lands in the activity log), `meshNotReady` before the zone's layout is ready,
`serviceUnavailable` when nothing answers the pipe, and `failed` again when a grid arrives that
cannot be indexed safely (arrays of different lengths, a column outside the `Width × Depth`
grid) — an empty grid would read as "no walkable ground here", which is a different and much
more dangerous claim than "I could not answer". `Start` is the server's snapped point, or your
own `from` when it reported none (a `startOffMesh` answer). The wire answer also carries
`nearest` and poly counts; this tuple does not surface them — say the word if the auto-solver
wants `nearest` on an off-mesh start and we will extend the shape.

**Readiness**: there is no `Nav.IsReady` twin. "Nav can answer for this zone" =
`IsConnected && ZoneStatus is 2 or 3`. Because Mnemosyne holds meshes out of process,
this is true ~0.1 s after zone-in — not after an in-game build.

## Parity gates (the `Ariadne.Nav.*` / `Ariadne.Query.Mesh.*` twins)

The same shapes as `vnavmesh.*`, registered under Ariadne's own prefix so a consumer can call both
and diff the answers on the same input before the cutover (the compat section below serves these
under vnavmesh's names once Ariadne owns them). All `Task`-shaped except `FlagToPoint`: nothing here
blocks a caller. Treat the surface as **in flux while parity is established** — it is additive, and a
reshape is announced in this file rather than discovered.

| Gate | Signature | Notes |
|---|---|---|
| `Ariadne.Nav.IsReady` | `() → bool` | a usable mesh for this zone is in hand (local cache or Mnemosyne) |
| `Ariadne.Nav.BuildProgress` | `() → float` | 0..1 while a mesh for this zone is on its way, -1 when nothing is making one. "On its way" covers the whole out-of-process build **and** waiting for a turn behind another zone's build (the service builds one at a time; 0 is reported while queued). Wait on `>= 0`, fault on `IsReady == false && BuildProgress < 0`. Before 2026-09-27 this stayed -1 for the whole build, which made consumers fault mid-build |
| `Ariadne.Nav.Pathfind` | `(Vector3 from, Vector3 to, bool fly) → Task<List<Vector3>>` | empty = no path, never throws |
| `Ariadne.Nav.PathfindWithTolerance` | `(…, float tolerance) → Task<List<Vector3>>` | goal tolerance, the same number `SimpleMove`'s range feeds the planner |
| `Ariadne.Nav.PathfindAvoid` | `(…, Vector3 avoidCenter, float avoidRadius) → Task<List<Vector3>>` | keep the path out of a sphere |
| `Ariadne.Nav.PathfindDetailed` | `(from, to, fly) → Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)>` | the classified answer the list-shaped gates structurally cannot carry |
| `Ariadne.Nav.PathfindInProgress` / `PathfindNumQueued` | `() → bool` / `() → int` | |
| `Ariadne.Query.Mesh.NearestPoint` | `(Vector3 p, float halfExtentXZ, float halfExtentY) → Task<Vector3?>` | null = nothing within the box |
| `Ariadne.Query.Mesh.NearestPointReachable` | same shape | `reachableOnly: true` |
| `Ariadne.Query.Mesh.IsPointOnMesh` | `(Vector3 p, float halfExtentY, bool allowUnreachable) → Task<bool>` | |
| `Ariadne.Query.Mesh.PointOnFloor` | `(Vector3 p, float halfExtentXZ, bool allowUnreachable) → Task<Vector3?>` | **Ariadne's argument order**; the compat gate uses vnavmesh's `(point, allowUnlandable, halfExtentXZ)` |
| `Ariadne.Query.Mesh.FlagToPoint` | `() → Vector3?` | the one sync-shaped gate here (bounded wait, `SyncGateBudgetMs` budget): the map flag exists only in the game process, and Ariadne resolves it on the calling thread |
| `Ariadne.Nav.BuildBitmap` | `(List<Vector3> starts, string filename, float pixelSize) → Task<string>` | returns the **written path** (Mnemosyne owns the output directory); the compat twin returns vnavmesh's `(min, max)` bounds instead |
| `Ariadne.Nav.BuildBitmapBounded` | `(…, Vector3[] bounds) → Task<string>` | `[min, max]`; an empty array means unbounded |

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

**`Ariadne.ServesVnavmesh` `() → bool`** is true while Ariadne is the one answering the
`vnavmesh.*` names. If your plugin keeps vnavmesh as a fallback for moves Ariadne could not
route, check this first: when it is true the fallback reaches the same planner and the same
mesh, so it will give the same answer. Skip it, and walk the partial route or report the move
as unroutable instead. When it is false, vnavmesh is really there with its own mesh and the
fallback is a genuine second opinion.

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

Releasing the names (takeover switched off, compat disabled, or Ariadne unloading) empties them;
vnavmesh registers only at load, so reload it to restore its own set.

Sync-shaped gates answer through a bounded blocking wait **on your thread**, and return the
not-found fallback rather than ever throwing or hanging. `Query.Mesh.*` waits up to 100 ms
(`SyncGateBudgetMs`; warm answers take 1–3 ms). `Nav.BuildBitmap*` waits up to **5 s**: a bitmap is
rasterized server-side, and vnavmesh's own window blocked comparably in-process. Treat that as a
debug-button budget, not a per-frame one — call it off the framework thread, or the game freezes for
as long as it waits. A bitmap that does not answer within it returns **NaN bounds**, never a
zero-area region — no file was written either, so check for both.

## Status

Movement verified in-game 2026-08-23: `SimpleMove.PathfindAndMoveTo` walked the
character on a Mnemosyne-computed path end-to-end (signatures resolve, hooks drive
input). Pathfinding/mesh gates verified earlier. Stall recovery is unit-tested but not
yet exercised against a real wedge; `ReportTraversal` and classified `findPath` results
await Mnemosyne's server side.
