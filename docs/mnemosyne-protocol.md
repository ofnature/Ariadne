# Mnemosyne pipe protocol (shared spec)

Single source of truth for the Ariadne ↔ Mnemosyne IPC. Referenced by both
`D:\Dev\Ariadne\PLAN.md` and `D:\Dev\Mnemosyne\PLAN.md` (milestone 5). Version this file;
breaking changes bump `protocol` in the hello response.

## Transport

- Named pipe `\\.\pipe\mnemosyne`, byte mode, one client per server stream instance
  (server should allow several instances — every game client in the fleet connects).
- **Newline-delimited JSON**: one request or response object per `\n`-terminated line,
  UTF-8, no BOM. No binary frames in v1 — mesh payloads are handed off as file paths
  (single-machine deployment). The envelope reserves `data` for future inline bytes
  (base64) so remote support is additive.

## Service lifecycle (verified 2026-08-24)

The service is a plain console app, but nobody should have to remember to start it — so
Ariadne starts it. The contract that makes that safe:

- **Single instance, machine-wide.** The service holds `Global\MnemosyneService`; a second
  launch prints `another Mnemosyne service is already running` and exits 0 without touching
  the pipe. *Verified: four simultaneous launches → exactly one survivor.*
- **Discovery.** On every run the service stamps its own exe path into
  `%APPDATA%\Mnemosyne\service.path`. Ariadne reads that file when its configured path is
  empty, so rebuilding or moving the service needs no plugin config change. A machine that
  has never run the service has no marker — Ariadne logs a one-line hint instead of guessing.
- **Autostart trigger.** Ariadne probes `File.Exists(\\.\pipe\mnemosyne)` before connecting
  (named pipes are real entries in the filesystem namespace, so this is exact and free — it
  beats eating the 2 s connect timeout to discover the same thing). Missing pipe → launch,
  rate-limited to one attempt per 30 s, serialised across game clients by
  `Global\MnemosyneServiceLaunch`. The connect still fails that cycle; the existing backoff
  retry picks it up. Config: `AutoStartMnemosyne` (default on), `MnemosyneServicePath`
  (empty = use the marker).
- **Observability when hidden.** An autostarted service has no console window, so it tees
  everything it prints to `%APPDATA%\Mnemosyne\service.log` (truncated per run).
- **Restart is transparent.** Killing the service leaves clients with zombie pipe handles;
  they drop on the next request timeout and reconnect — no plugin reload needed.

## Envelope

Request: `{ "id": <int>, "op": "<name>", ...op fields }`
Response: `{ "id": <same int>, "ok": true, ...result }` or
`{ "id": <int>, "ok": false, "error": "<message>" }`

`id` is client-chosen and echoed verbatim; clients may pipeline. Server must answer every
request, in any order. Unknown `op` → `ok:false`.

## Operations (v1)

### `hello`
→ `{ ok, protocol: 1, app: "mnemosyne"|"mnemosyne-stub", version: "<semver>", meshVersion: 25 }`
Client sends first; use to gate features and detect the stub.

### `listZones`
→ `{ ok, zones: [ { cacheKey, version, customization, size, mtime } ] }`
Everything the cache index holds. `cacheKey` is the vnavmesh filename stem
(`{bg}__{filter:X}__{festivals}__{sgs}`), `version` the serializer version from the header,
`customization` the header's per-zone customization counter, `mtime` ISO-8601 UTC.

### `zoneStatus`
`{ cacheKey }` → `{ ok, status: "cached"|"missing"|"stale"|"building", version, customization }`
`stale` = present but header version ≠ current `meshVersion` (or unreadable).
`building` = a server-side build for this key is running; `progress` is 0..1 (see below).

### `getMesh`
`{ cacheKey }` → `{ ok, path: "<absolute path to .navmesh>", version, customization, size }`
or `ok:false` if missing/stale. Path must stay valid until overwritten by a newer build of
the same key; Ariadne copies immediately. (Future remote: same op, `data` field instead of
`path`.)

**Builder fallback** (server-side, since 2026-08-08): when a zone is absent from
vnavmesh's cache, Mnemosyne may build it from game files itself and serve the result from
its own store (`%APPDATA%\Mnemosyne\built`). `zoneStatus` reports `cached` when either
vnavmesh's file or a current built file exists. vnavmesh's cache always wins when both
exist. Built meshes are baseline (no festivals, shared groups in default state) and lack
vnavmesh's per-zone customizations.

**Builds never block a request** (changed 2026-08-24, phase 2 — previously `getMesh` and
`findPath` blocked for the whole cold build). A build now runs on its own thread and every
op answers immediately:

- `zoneStatus` → `status: "building"`, `progress: 0..1`
- `getMesh` → `ok: false`, `result: "meshNotReady"`
- `findPath` / `Query.Mesh.*` → `ok: false`, `result: "meshNotReady"`

The old behaviour was actively harmful with a fleet: one cold build held the build lock for
tens of seconds, so all four clients' next request blew the 10 s request timeout, dropped
their pipes and reconnected. `meshNotReady` is retryable — poll `zoneStatus` and re-issue
when it reports `cached`.

**Overrides are baked into served files** (answered 2026-08-24 by the Mnemosyne session;
Ariadne PLAN asked whether to bake or keep in-memory — **bake**). The `.navmesh` handed
back by `getMesh` has the zone's OverrideStore edits already applied (blocks, prunes,
off-mesh links, later cost paints), so a seeded vnavmesh cache carries the curated mesh
too — custom meshes work during the transition, not only after the replacement. The
viewer keeps applying overrides in memory for live editing. Consequence for clients: a
served file can differ from vnavmesh's own build of the same cacheKey by design; the
header's `customization` field is unchanged (it tracks vnavmesh's per-zone version, not
ours).

### `findPath`
`{ cacheKey, from: [x,y,z], to: [x,y,z], fly: bool }`
→ `{ ok, waypoints: [ [x,y,z], ... ], partial: bool }` or `ok:false` with error (no mesh,
no path). Game/world coordinates, Y-up, identical to vnavmesh's `Nav.Pathfind`.
`partial: true` means the path stops short of `to` — for walk, disconnected mesh; for
fly, the server's voxel-search step budget was exhausted (long open-air hops): follow the
returned waypoints and re-query from the last one (receding horizon). **Known
divergence**: runs on the raw cached mesh — no per-festival `CustomizeMesh`, no
flood-fill pruning.

**Classified answers** (spec'd 2026-08-23 from the Odysseus field requirements — Ariadne
PLAN.md §3 "Honest answers instead of one no"; server implementation pending). The
response gains an optional `result` field so consumers can act once, correctly, instead
of disambiguating "no" by experiment:

- `"ok"` — waypoints reach `to` (possibly `partial` per above)
- `"targetOffMesh"` — `to` isn't on the mesh/volume; `nearest: [x,y,z]` carries the
  closest reachable point (consumer decides: fight from here / walk the last yalm)
- `"noRouteOnMesh"` — both ends on-mesh, no route: the mesh is lying (hole, bad voxels) —
  the tonight-signal for "rebuild / add an override"
- `"meshNotReady"` — zone still loading/building server-side; retryable
- `"unreachable"` — goal is on the mesh but in a flood-fill-pruned region (vnavmesh's
  `FLAG_UNREACHABLE`): genuinely disconnected, not a mesh defect. Do not report it as one.

Two further values, added with the implementation (2026-08-24):

- `"startOffMesh"` — `from` isn't on the mesh; `nearest` is the closest point to *`from`*.
  Distinct from `targetOffMesh` because the consumer's move is different: get the character
  onto the mesh first, rather than accepting a shorter goal.
- `"avoidIgnored"` — the avoid circle sealed the only corridor, so the returned route
  ignores it (see `avoidCenter` below).

`nearest: [x,y,z]` accompanies `targetOffMesh` and `startOffMesh`, and is absent otherwise.

One value the server never sends, added 2026-09-08:

- `"serviceUnavailable"` — nothing answered the pipe. The client synthesizes this when a
  request gets no response at all: the service is not running, the connection is still in
  backoff, or the request timed out and the connection was dropped.

  It exists because the alternative was reporting a dead service as `meshNotReady`, and
  those two demand opposite behaviour from a consumer. `meshNotReady` is a *wait* — the
  zone is building, poll and re-issue. `serviceUnavailable` is a *stop* — nothing is
  serving meshes, and no amount of retrying will change that until the service is back.
  Conflating them cost a debugging session chasing a doorway that turned out to path
  perfectly well: every request had been answered "mesh not ready" by a service that was
  not running at all.

  A consumer that only knows the older vocabulary sees an unfamiliar string, which the
  spec already requires it to tolerate — and unfamiliar-but-honest beats familiar-and-wrong.


Servers that omit `result` are treated as legacy (`ok` iff waypoints non-empty). Clients
must tolerate unknown values (treat as `"unreachable"`).

**Status: implemented server-side 2026-08-24.** `ok:false` responses now always carry a
`result`; `ok:true` carries one only when it is not a plain success (`avoidIgnored`).

**Reaching consumers.** The vnavmesh-shaped gates return a bare `List<Vector3>`, so they
structurally cannot carry a classification — "no path" and "your goal is 2 y off the mesh,
stand here instead" look identical through them. Ariadne therefore adds
`Ariadne.Nav.PathfindDetailed(from, to, fly)` → `(result, waypoints, nearest, partial)`
alongside the compat gates. A legacy server that omits `result` is reported as `"ok"` when
waypoints came back and `"unreachable"` when they did not, so the field is never empty.

**Multi-modal legs** (spec'd 2026-08-23 — Mnemosyne PLAN.md milestone 11; additive.
**First case implemented 2026-08-25**: a fly route the volume cannot complete now lands and
walks the remainder, answering `result: "walkedTail"` with a `fly` leg and a `walk` leg
(`enter: "land"`). Constraints and the richer transitions are still pending.)

Why it was needed: the navmesh and the voxel volume disagree at doorways. Measured at the
Yedlihmad door — the mesh leaves it open and walks through fine, while the volume seals it,
because the opening is narrower than a voxel leaf. It is also simply true that you cannot
fly indoors. Before this, a fly request there returned a partial route that died 11.3 m short
at the threshold; now it completes, ending the same 1.8 m from the goal as the walk route. Request gains `constraints?: ["noFly","noMount","noTeleport",
"noDismount"]` (e.g. quest vehicles = `["noFly","noMount","noDismount","noTeleport"]`);
when the server plans multi-modally it adds `legs`:

```
legs: [ { mode: "walk"|"fly", enter?: "mount"|"jumpOff"|"land"|"dismount"|"teleport",
          enterArg?: <aetheryteId>, first: <index into waypoints>, count: <n> } ]
```

Legs index into the flat `waypoints` array so legacy consumers that ignore `legs` still
get a followable (if mode-naive) path. `enter` is the transition the follower performs
before walking/flying that leg's waypoints — mount/land at the leg boundary, land at the
destination's floor, dismount before interiors.

**Client support (Ariadne, 2026-09-19):** `walk`/`fly` mode switching and the `land`
transition are executed — the walk leg is held while the game still reports flight, driven at
its first waypoint, until the character is on the ground (10 s budget; after that the leg is
followed on foot and the log says so). `mount`, `dismount`, `jumpOff` and `teleport` are parsed
and logged as not-yet-executed, and the leg is followed as-is. An unknown mode drops that leg
only, an unknown transition is ignored, a span past the end of `waypoints` is clamped, and a
server that sends no `legs` at all gets the old flat-list behaviour.

### `nearestPoint` / `isPointOnMesh` / `pointOnFloor`  (spec'd 2026-08-24, Mnemosyne session)

The `Query.Mesh.*` gates consumers call (Theseus, Olympus, SealBreaker), served from the
loaded zone. Semantics match vnavmesh's `NavmeshQuery` helpers exactly.

```
nearestPoint    { cacheKey, point: [x,y,z], halfExtentXZ=5, halfExtentY=5, reachableOnly=false }
                → { ok, found: bool, point?: [x,y,z] }
isPointOnMesh   { cacheKey, point, halfExtentY=5, allowUnreachable=true }
                → { ok, onMesh: bool }
pointOnFloor    { cacheKey, point, halfExtentXZ=5, allowUnreachable=true }
                → { ok, found: bool, point?: [x,y,z] }
```

- `nearestPoint` = vnavmesh `Query.Mesh.NearestPoint`; with `reachableOnly:true` it is
  `Query.Mesh.NearestPointReachable` (filter excludes `FLAG_UNREACHABLE` 0x10).
- `pointOnFloor` = largest-Y point still below `point.Y` within the XZ tolerance
  (vnavmesh searches ±2048 y vertically; same here).
- `Query.Mesh.FlagToPoint` stays **client-side**: Ariadne reads the map flag from game
  state, then calls `pointOnFloor`.

### `reachableCells`  (spec'd 2026-09-14, Mnemosyne session — **implemented both sides 2026-09-19**)

"Where can I walk from here?" as a grid. Asked for by Theseus's dungeon auto-solver
(`D:\Dev\Theseus\theseus-autosolver-architecture.md` §5.2) to find unexplored ground and the
edges where the mesh is cut off. Mnemosyne answers only *reachability*: it has no notion of
explored or visited, and must not grow one. That state is per-run and lives in the consumer.

```
reachableCells { cacheKey, from: [x,y,z], radius=120, cellSize=2, minY?, maxY? }
  → { ok, result: "ok",
      start:   [x,y,z],          // `from` snapped onto the mesh: where the flood began
      origin:  [x, z],           // world X/Z of the grid's min corner
      cellSize, width, depth,    // columns = width × depth, row-major: index = zi * width + xi
      columns: [int, ...],       // one entry per surface: its column index, ascending
      heights: [float, ...],     // one entry per surface: Y at the cell centre, 0.1 y precision
      states:  [int, ...],       // one entry per surface: 1 reachable · 2 cutOff
      reachableOutside: bool,    // the flood reached mesh beyond this window
      stats:   { reachablePolys, walkablePolys } }
```

**The grid is aligned to the world, not to `from`.**
`origin = floor((from.xz - radius) / cellSize) * cellSize`, and
`width = ceil((from.x + radius - origin.x) / cellSize)` (same for `depth` on Z). A cell's centre is
`origin + (index + 0.5) * cellSize`. Two queries with the same `cellSize` therefore share cell
boundaries wherever they overlap. That is what lets a consumer keep one visited set across many
queries without resampling. `radius` is half the side of a square, so 120 y at 2 y cells is
120 × 120 = 14,400 columns.

**Columns hold surfaces, not a single value, because dungeons are stacked.** A column can have
several walkable floors, e.g. both ends of a 30 y lift shaft, or a walkway over a plaza. Every one is
reported, highest first within its column. Samples in one column less than 2 y apart vertically
(the agent height, so no two separate floors can be closer) merge into one surface, which is
`reachable` if either sample was. A column with no entry has no walkable mesh (`noMesh`). The
arrays are parallel and sparse: `columns[i]`, `heights[i]` and `states[i]` describe surface `i`.
Optional `minY` / `maxY` (world Y) drop surfaces outside a band, for "just this floor".

**How a surface is found.** A column has a surface wherever a walkable poly covers its cell centre.
Walkable means it passes the same default filter `findPath` uses, so polys blocked by an override
are not walkable. The height comes from the detail mesh at the centre. Sampling is at the centre
only, so a feature narrower than a cell can be absent: a doorway meshed 0.5 y wide will not show at
2 y cells. Connectivity through it is still correct, because reachability is computed on polys,
not cells (below). Use `cellSize` ≤ 1 where narrow openings matter.

**How reachability is decided.** One flood, not a pathfind per cell. Start from the poly nearest
`from` (the same snap `findPath` uses: 5 y each way) and spread across poly adjacency, including
tile borders. The flood uses the `findPath` filter and follows override links in their direction
(both ways when `bidirectional`). **The flood is zone-wide, not clipped to the window.** A cell is
`reachable` even when the only route to it leaves the grid and comes back. It is `cutOff` when it
is walkable mesh the flood never reached. `reachable` means connected; it does not promise a short
route, and `findPath` remains the authority on an actual path.

**`cutOff` next to `reachable` is where gates live, but only at the same height.** Compare
heights before calling an edge a gate. A `cutOff` surface 30 y above a `reachable` one in the next
column is another storey, not a closed door.

**`reachableOutside`** is true when the flood reached any walkable poly that lies at least partly
outside the window (horizontally, or outside `minY`/`maxY`). `false` means everything reachable from
here is in this answer, so an exhausted grid really is exhausted rather than just too small.

**Classified results**, same vocabulary as `findPath`:

- `startOffMesh`: `from` is not within the snap of any walkable poly. Carries `nearest: [x,y,z]`
  and no grid.
- `meshNotReady`: the zone is loading or building; retryable.
- `failed`: bad arguments. `cellSize` must be 0.5–16, `radius` greater than 0 and at most 512,
  and `width × depth` at most 65,536. Alignment can add a column or row, so size for that: radius
  127 at 1 y always fits, radius 128 may not. A bigger grid is refused rather than truncated.

**Deliberately not included.**
- *Detour class.* The CLI `reachmap` marks cells reachable only by a >3× detour. That needs a
  pathfind per cell, and exploration does not use it.
- *Visited / explored state.* Consumer-side, per run (above).
- *Gated links.* Links usable only once an interaction has happened (lifts, activated shuttles)
  are not in the override format yet, and `findPath` has no conditions input either. When gated
  links land, both ops take the same "conditions currently true" list, so a lift's far floor turns
  `reachable` once the caller says the lever is pulled.

**Cost** (measured 2026-09-19). One flood over the zone's polys plus a rasterisation of the
window. The flood is cached per `(cacheKey, start poly, overrides stamp)` — capped at four
components per loaded zone, and dropped with the zone, which is what makes an override edit
invalidate it — so repeated queries from the same area redo only the rasterisation. Measured on
a dungeon (`y6d1`, 4,673 walkable polys): flood 1.7 ms, and on a field zone (`x6f2`, 141,101
walkable polys) 56 ms warm for the spec's 14,400-column case (121 × 121 at 2 y) — the
rasterisation is ~4 µs per column, dominated by the per-surface detail-mesh height sample, so
the 20 ms target above was optimistic; 56 ms is what it is, and it is 180× inside Ariadne's 10 s
request timeout. Cold, add the zone load (596 ms for that 141k-poly field, 76 ms for the
dungeon). Payload: 114 KB for 9,967 surfaces on that window, and 27 KB for a 961-column one —
inside the line sizes `buildZone` already sends.

**Relationship to `reachmap`.** The CLI command answers a similar question differently: one
pathfind per cell, a 20 y horizontal snap that hides holes, and a ±2 y vertical window that
erases storeys. It is a debugging picture, not this computation. The CLI now renders this op
itself — `Mnemosyne.Cli reachcells <zone> [x y z] [radius] [cellSize] [minY] [maxY]`, driving
`ZoneService.Handle` in process so it can run while a service is serving a live game session —
so the two pictures can be put side by side. They agree wherever a cell centre is meshed and
diverge in the fringes: `reachmap`'s 20 y snap calls a cell reachable when *any* ground is
within 20 y of it, while this op samples the centre, as specified.

### `buildBitmap`  (spec'd 2026-08-24)

`Nav.BuildBitmap{,Bounded,Multi,MultiBounded}` — Olympus uses the bounded forms.

```
buildBitmap { cacheKey, startingPoints: [[x,y,z], ...], filename, pixelSize,
              minBounds?: [x,y,z], maxBounds?: [x,y,z] }
            → { ok, path: "<absolute path written>" }
```

Flood-fills walkable polys from the starting points and writes vnavmesh's bitmap format
(vendored `NavmeshBitmap`). Bounds omitted = whole mesh. The server writes the file and
returns its absolute path; the client relays vnavmesh's `bool` from `ok`.

### `findPath` additions  (spec'd 2026-08-24)

Request gains the vnavmesh pathfind variants:

- `tolerance?: <yalms>` — `Nav.PathfindWithTolerance` / `SimpleMove.PathfindAndMoveCloseTo`.
  Implementation note (documented divergence): vnavmesh stops A* early with a goal-radius
  heuristic; Mnemosyne paths to the goal and trims trailing waypoints inside the radius,
  and when the goal itself is unreachable it retries against the nearest reachable point
  within `tolerance`. Same practical result for a follower, and it also answers the
  "goal is 1 y off-mesh" case that made consumers retry blindly.
- **Interacting with an NPC: never path to the NPC.** NPC positions are routinely off-mesh -
  behind a counter, on a dais, inside furniture - so asking for one is asking for a point the
  navmesh does not contain. Pass the NPC position with `tolerance` set to the interaction
  range (4-5 y covers most NPCs) and let the server stop you short: it retries against the
  nearest reachable point and trims the tail inside the radius. Then read `result` - `ok`
  means you are standing somewhere you can interact from, and `targetOffMesh` with `nearest`
  means the NPC is further than the tolerance from anything walkable, so walk to `nearest` and
  interact from there rather than retrying. This also avoids the endpoint caveat under
  `clearance`: endpoints are never padded, so handing the follower a goal flush against a
  counter makes the final approach graze it.

- `avoidCenter?: [x,y,z]`, `avoidRadius?: <yalms>` — `Nav.PathfindAvoid`. Applies to
  **both ground and fly** legs. Two documented divergences from vnavmesh, both deliberate:
  - vnavmesh only engages avoid when the *straight* `from`→`to` segment enters the circle.
    A hazard sitting on the actual (curved) route is therefore ignored — measured on
    `sea_s1_fld_s1f6`, an avoid circle centred on a waypoint of the returned route changed
    nothing: 24 waypoints in, 24 out, still passing through the centre. Mnemosyne filters
    whenever a positive radius is asked for. Same test after the change: closest approach
    0.0 m → 164.4 m, 13 waypoints.
  - The radius is clamped to `min(dist(from, centre), dist(to, centre)) - 0.5`, so avoid can
    never exclude the start or the goal. Standing inside the hazard means "get no closer",
    not "no path exists".
  - If the circle seals the only corridor, the response carries the unconstrained route with
    `result: "avoidIgnored"` rather than failing. Losing the route *and* the reason is the
    worst answer; the consumer can decide whether to walk it or wait.
  - Flying with avoid bypasses the coarse octree (it has no notion of the circle) and uses
    the voxel search, which is what vnavmesh does for volume paths.

`findPath` response gains `result?: string` — why the answer looks the way it does, when
`ok: true` alone would mislead. Absent means plain success. First member: `"avoidIgnored"`.
Phase 2 grows this into the full classification (off-mesh goal, unreachable component,
needs-teleport, budget-exhausted), so treat an unknown value as informational, never fatal.

### `zoneStatus` additions  (spec'd 2026-08-24)

Response gains `progress: <0..1>` and `building: bool` so `Nav.BuildProgress` reports a
real number while `buildZone`/`TryBuild` runs (`-1` when nothing is building, matching
vnavmesh's idle value). `pathfindInProgress: bool` and `pathfindNumQueued: int` are also
returned, serving `Nav.PathfindInProgress` / `Nav.PathfindNumQueued`.

### `buildZone`
`{ cacheKey, scene: { …SceneCaptureDto… } }` → `{ ok }` ack **immediately** (the build
runs async server-side — client polls `zoneStatus` until `cached`; Ariadne polls every
3 s for up to 5 min). Spec'd 2026-08-23 (Ariadne PLAN "Meshing"; Mnemosyne's deferred
"active acquisition"). **Implemented server-side 2026-08-24**; a server without the op still
answers `ok:false` unknown-op and the client degrades. The ack carries
`result: "meshNotReady"` — the build has started, poll `zoneStatus`.

The scene is Ariadne's live capture of the exact zone variant — the part only the game
process can see: active festival layers, zone shared-group states, live layout instances.
Shape (camelCase, mirrors vnavmesh's `SceneDefinition`; authoritative C# DTO:
`Ariadne/Zone/SceneCapture.cs`, wire-shape locked by `SceneCaptureDtoTests`):

```
{ cacheKey, territoryId, cfcId, festivalLayers: [uint], zoneSGs: [uint],
  terrains: [string], analyticShapes: [{ crc, transform, bbMin, bbMax }],
  meshPaths: [{ crc, path }], bgParts: [{ key, transform, crc, matId, matMask, analytic }],
  colliders: [{ key, transform, crc, matId, matMask, type }],
  exitRanges: [{ key, transform }] }
transform = { t: [x,y,z], r: [x,y,z,w], s: [x,y,z], type: int }
```

`transform.type` is the analytic collider kind (0 box, 1 sphere, 2 cylinder, 3 plane) and is
meaningful only on `analyticShapes`. **Added 2026-08-24** — it was missing from the original
shape, and `SceneExtractor` switches on it to decide what to rasterize, so without it every
sphere and cylinder in a captured zone silently became a box. Caught by building the same
scene twice, once through the wire path: 7467 polys against 7527 on Limsa Lominsa Lower
Decks. Counts and transforms all matched; only the mesh disagreed.

Collision file *contents* are not shipped — `meshPaths`/`terrains` are sqpack paths the
server reads itself via Lumina (its builder already does). Note for the server's line
reader: a dense zone capture is a **multi-megabyte single line**; don't cap line length.
Build result goes into the built store under `cacheKey` exactly as `TryBuild`'s output
does; from there the normal `zoneStatus`/`getMesh`/seed machinery takes over.

### `reportTraversal`
`{ cacheKey, from: [x,y,z], to: [x,y,z], mode: "walk"|"fly"|"direct", success: bool, note? }`
→ `{ ok }` (spec'd 2026-08-23; **implemented server-side 2026-08-24**)
Feedback channel from execution back into the mesh (Ariadne PLAN.md §2): the follower (or
a consumer like Odysseus) reports that a traversal succeeded where the mesh said no-path
(`mode: "direct"`, `success: true` = off-mesh-link candidate for the OverrideStore) or
that a planned leg failed (`success: false` = block/cost-paint candidate). Server
accumulates evidence; nothing is auto-applied. Fire-and-forget, idempotent, best-effort.

Server behaviour (2026-08-24): reports land in `%APPDATA%\Mnemosyne\evidence\<bg-key>.json`,
split into link candidates (`mode: "direct"`, `success: true`) and block candidates
(`success: false`). A successful *planned* leg is stored as nothing — the follower reports
every leg and only the surprising ones are evidence. Reports within 5 m of an existing one
merge into a `count` rather than adding a row, since a follower re-reports the same doorway
from a slightly different spot on every attempt. The viewer's edit mode draws them as dashed
candidates, cyan for links and amber for blocks; `Mnemosyne.Cli doors <zone> --record` files
mesh-sealed doors into the same store.

### `notifyMeshBuilt`
`{ cacheKey, path }` → `{ ok }`
Ariadne-side push when it observes vnavmesh finish a fresh build, so Mnemosyne ingests
without waiting on its FileSystemWatcher debounce. Idempotent; Mnemosyne may ignore
duplicates.

### `updateGameState`
`{ cacheKey, territoryId, pos: [x,y,z], rotation: <yaw radians>, flying: bool, speed?: <y/s> }` → `{ ok }`
Ariadne-side push, ~10 Hz while a player is loaded into a zone: the player's live
position for Mnemosyne's viewer (player marker, camera follow, auto zone switch).
`cacheKey` is the zone's exact vnavmesh cache key as Ariadne computes it in-game;
`rotation` is character yaw in radians. Server keeps only the latest sample. Send
best-effort; dropped samples are harmless. `speed` is optional (added 2026-08-10):
if Ariadne can read the character's exact movement speed, send it and the server
uses it verbatim; otherwise the server derives speed from consecutive positions.

### `getGameState`
→ `{ ok, present: bool, cacheKey, territoryId, pos, rotation, flying, ageMs,
     speed, speeds: { ground, fly } }`
Latest pushed game state. `present: false` when nothing has been pushed yet or the
last sample is stale (> 5 s old — treat as "player logged out / Ariadne gone");
the remaining position fields are then absent — but `speeds` is still returned,
since calibration outlives the play session. Poll-friendly (viewer polls ~10 Hz).
`speed` (added 2026-08-10) is the current movement speed in y/s (pushed exactly, or
position-derived). `speeds` holds the server's self-calibrated sustained maxima per
mode, learned by watching the player move (persisted across restarts); fields are
absent until observed. These feed travel-time estimates.

### `findPath` addendum (2026-08-10)
Response gains optional `etaSeconds`: path length divided by the calibrated mode
speed (`speeds.fly` for fly queries, `speeds.ground` otherwise); absent while the
speed is uncalibrated. An estimate — mounting time, casting, and detours are the
client's problem.

### Multi-client (fleet) semantics  (spec'd 2026-08-24 — up to 4+ game clients per PC)

One Mnemosyne service serves every Ariadne on the machine. Rules:

- **One service, many clients.** The service holds a single-instance mutex
  (`Global\MnemosyneService`); a second launch exits immediately rather than racing for
  the pipe. Ariadne may spawn the service when the pipe is absent — spawning is
  idempotent by construction, so all four clients starting at once is safe.
- **Client identity is the connection.** The server tags every request with the pipe
  connection it arrived on; no id is required in the envelope. `updateGameState` should
  additionally carry `character?: string` and `contentId?: number` for display and
  stable identity across reconnects. Ariadne sends `character` as `"Name@World"` and omits
  `contentId` — this Dalamud API version exposes no local content id, and a bare character
  name is not unique across worlds. Treat `character` as the human-facing label and the
  connection as the identity; `contentId` stays in the spec for a client that has one.
- **Zone edits are shared, and hot.** Overrides, obstacles and prunes are stored per *zone*
  (`%APPDATA%\Mnemosyne\overrides\<bg-key>.json`), never per character, so an edit made while
  playing one toon is the same data every other client reads. Since 2026-08-25 the server also
  notices the file changing under a zone it already has loaded and reloads it, so an edit
  reaches every client on its next query — no restart, no zone re-entry. Consumers need do
  nothing; a route simply changes.
- **Per-client state, not global.** Anything that used to be a single latest value is
  now keyed by connection: pushed game state, `pathfindInProgress`, `pathfindNumQueued`.
  A client only ever sees its own counters, matching vnavmesh's per-process semantics.
- **`getGameState` returns the fleet:**

```
getGameState { } → { ok, present, players: [ { clientId, character?, contentId?,
                     cacheKey, territoryId, pos, rotation, flying, speed, ageMs } ],
                     speeds: { ground, fly }, ...legacy single-player fields }
```

  The legacy top-level fields (`pos`, `cacheKey`, …) mirror the **most recently updated**
  player so existing consumers keep working; new consumers read `players`. Entries older
  than the 5 s staleness window are dropped from the list, and `present` is
  `players.length > 0`.
- **Shared, not per-client:** the zone cache, built store, override store and speed
  calibration are machine-wide by design — four toons in the same zone load it once.
  The warm-zone LRU is sized for a fleet (≥ 8) so four clients in four zones do not
  thrash it.
- **Fairness:** queries are per-zone locked, so clients in different zones never block
  each other; clients in the *same* zone serialize briefly on that zone's query object.

## Error/liveness conventions

- Malformed JSON line: server drops the connection (client treats as pipe loss).
- Client reconnect: capped exponential backoff (1s → 30s). All ops are stateless, so
  reconnect needs no session re-establishment beyond `hello`.
- Long ops (`findPath` on cold mesh may need a load): server should still answer other
  pipelined requests; a client-side timeout of 10s per request is reasonable.
- **Zone entry is peak file contention** (CONFIRMED 2026-08-09: holding the built-store
  file with `FileShare.None` and probing `zoneStatus` flips the answer to `missing`;
  releasing flips it back to `cached` — the viewer's auto-zone-switch load is the natural
  holder at exactly the moment Ariadne queries. Second finding, same night: the hold
  lasts the viewer's ENTIRE zone load, which spans vnavmesh's whole build — Ariadne
  retried for 11 s and never got a positive answer, so client-side retries cannot solve
  this. The fix has to be the holder's: **the viewer's loader must open `.navmesh` files
  with `FileShare.Read` — or read-bytes-then-close — never an exclusive handle held for
  the whole load**; while the handle is exclusive, serving *and* copying are both
  impossible for everyone else):
  the moment a player zones in, vnavmesh may be writing its cache file, the viewer may be
  auto-loading the same zone off the `updateGameState` push, and Ariadne is validating —
  all against the same paths. Both sides must treat a transient `IOException` as
  retryable, not as "missing": open mesh files for reading with
  `FileShare.ReadWrite | FileShare.Delete`, and don't let a single failed open turn a
  cached zone into a `missing` answer (Ariadne retries negative answers ×3 with backoff;
  Mnemosyne's `IsCurrentMeshFile` swallowing `IOException` → `false` is the server-side
  spot to harden).

  **RESOLVED Mnemosyne-side 2026-08-09**: all mesh-file reads (MeshCache header/load,
  FastCache segments) now open with `FileShare.ReadWrite | FileShare.Delete`, read bytes
  in a short window and close BEFORE parsing (no handle held across a zone load — the
  viewer's exclusive-hold-for-the-whole-load behavior is gone), and transient
  `IOException`s retry 3× with 150 ms backoff before any negative answer. Verified by
  re-running the repro: a 250 ms exclusive hold on the built-store file now answers
  `zoneStatus: cached` (retry absorbs the hold) instead of `missing`.
