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
`{ cacheKey }` → `{ ok, status: "cached"|"missing"|"stale", version, customization }`
`stale` = present but header version ≠ current `meshVersion` (or unreadable).

### `getMesh`
`{ cacheKey }` → `{ ok, path: "<absolute path to .navmesh>", version, customization, size }`
or `ok:false` if missing/stale. Path must stay valid until overwritten by a newer build of
the same key; Ariadne copies immediately. (Future remote: same op, `data` field instead of
`path`.)

**Builder fallback** (server-side, since 2026-08-08): when a zone is absent from
vnavmesh's cache, Mnemosyne may build it from game files itself and serve the result from
its own store (`%APPDATA%\Mnemosyne\built`). Consequences for clients: `getMesh` and
`findPath` can block for a cold build (~10-30 s — treat a timeout as retryable, the build
continues server-side); `zoneStatus` reports `cached` when either vnavmesh's file or a
current built file exists. vnavmesh's cache always wins when both exist. Built meshes are
baseline (no festivals, shared groups in default state) and lack vnavmesh's per-zone
customizations.

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
- `"unreachable"` — genuinely disconnected after override application

Servers that omit `result` are treated as legacy (`ok` iff waypoints non-empty). Clients
must tolerate unknown values (treat as `"unreachable"`).

**Multi-modal legs** (spec'd 2026-08-23 — Mnemosyne PLAN.md milestone 11; additive,
implementation pending). Request gains `constraints?: ["noFly","noMount","noTeleport",
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

### `buildZone`
`{ cacheKey, scene: { …SceneCaptureDto… } }` → `{ ok }` ack **immediately** (the build
runs async server-side — client polls `zoneStatus` until `cached`; Ariadne polls every
3 s for up to 5 min). Spec'd 2026-08-23 (Ariadne PLAN "Meshing"; Mnemosyne's deferred
"active acquisition") — server implementation pending; a server without the op answers
`ok:false` unknown-op and the client degrades.

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
transform = { t: [x,y,z], r: [x,y,z,w], s: [x,y,z] }
```

Collision file *contents* are not shipped — `meshPaths`/`terrains` are sqpack paths the
server reads itself via Lumina (its builder already does). Note for the server's line
reader: a dense zone capture is a **multi-megabyte single line**; don't cap line length.
Build result goes into the built store under `cacheKey` exactly as `TryBuild`'s output
does; from there the normal `zoneStatus`/`getMesh`/seed machinery takes over.

### `reportTraversal`
`{ cacheKey, from: [x,y,z], to: [x,y,z], mode: "walk"|"fly"|"direct", success: bool, note? }`
→ `{ ok }` (spec'd 2026-08-23; server implementation pending)
Feedback channel from execution back into the mesh (Ariadne PLAN.md §2): the follower (or
a consumer like Odysseus) reports that a traversal succeeded where the mesh said no-path
(`mode: "direct"`, `success: true` = off-mesh-link candidate for the OverrideStore) or
that a planned leg failed (`success: false` = block/cost-paint candidate). Server
accumulates evidence; nothing is auto-applied without the viewer's edit workflow unless
Mnemosyne decides otherwise. Fire-and-forget, idempotent, best-effort.

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
