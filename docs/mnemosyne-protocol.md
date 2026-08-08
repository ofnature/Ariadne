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

### `findPath`
`{ cacheKey, from: [x,y,z], to: [x,y,z], fly: bool }`
→ `{ ok, waypoints: [ [x,y,z], ... ] }` or `ok:false` with error (no mesh, no path).
Game/world coordinates, Y-up, identical to vnavmesh's `Nav.Pathfind`. **Known divergence**:
runs on the raw cached mesh — no per-festival `CustomizeMesh`, no flood-fill pruning.

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
