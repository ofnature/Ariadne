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

### `notifyMeshBuilt`
`{ cacheKey, path }` → `{ ok }`
Ariadne-side push when it observes vnavmesh finish a fresh build, so Mnemosyne ingests
without waiting on its FileSystemWatcher debounce. Idempotent; Mnemosyne may ignore
duplicates.

### `updateGameState`
`{ cacheKey, territoryId, pos: [x,y,z], rotation: <yaw radians>, flying: bool }` → `{ ok }`
Ariadne-side push, ~10 Hz while a player is loaded into a zone: the player's live
position for Mnemosyne's viewer (player marker, camera follow, auto zone switch).
`cacheKey` is the zone's exact vnavmesh cache key as Ariadne computes it in-game;
`rotation` is character yaw in radians. Server keeps only the latest sample. Send
best-effort; dropped samples are harmless.

### `getGameState`
→ `{ ok, present: bool, cacheKey, territoryId, pos, rotation, flying, ageMs }`
Latest pushed game state. `present: false` when nothing has been pushed yet or the
last sample is stale (> 5 s old — treat as "player logged out / Ariadne gone");
the remaining fields are then absent. Poll-friendly (viewer polls ~10 Hz).

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
