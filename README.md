# Ariadne

**Hands your fleet the thread — ready navmeshes the moment a zone loads.**

A Dalamud plugin for FFXIV that bridges the live game session and
[Mnemosyne](https://github.com/ofnature/Mnemosyne), an out-of-process navmesh cache. When a
zone loads, Ariadne asks Mnemosyne whether a current mesh already exists; if it does, it
seeds [vnavmesh](https://github.com/awgil/ffxiv_navmesh)'s cache so the build step is
skipped entirely, and exposes its own IPC so other plugins can request meshes or paths
directly.

Ariadne didn't slay anything in the labyrinth — she just made sure the one who did never
had to rediscover the way. Same job here.

> **Status: v0.1.0, released and installed from the shared listing.** Ariadne runs against the real
> Mnemosyne service over the pipe — no stub: zone detection, cache seeding, out-of-process builds
> and the consumer IPC surface are live, and the movement chain has been verified in-game (a
> character walked a Mnemosyne-computed path with vnavmesh not involved at all). The `vnavmesh.*`
> gates are claimable by policy, so existing consumers can migrate one feature at a time. Still
> owed: the `mount` / `dismount` / `jumpOff` leg transitions, and the in-game pass over the
> `meshNotReady` retry. Per-gate detail: [docs/consumer-ipc.md](docs/consumer-ipc.md).

## How it fits together

| Project | Role |
|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) | Builds and consumes navmeshes in-game |
| Mnemosyne | Owns the mesh cache outside the game process; serves and (later) builds meshes |
| **Ariadne** | In-game bridge: zone detection, cache seeding, consumer IPC |
| [Theseus](https://github.com/ofnature/Theseus) | Downstream consumer (dungeon running) |

## IPC

| Name | Signature | Notes |
|---|---|---|
| `Ariadne.IsConnected` | `() → bool` | pipe to Mnemosyne alive |
| `Ariadne.CurrentCacheKey` | `() → string` | `""` while the layout is loading |
| `Ariadne.ZoneStatus` | `() → int` | 0 NotReady, 1 MnemosyneUnavailable, 2 LocalCurrent, 3 MnemosyneCached, 4 Missing |
| `Ariadne.RequestMesh` | `() → Task<string>` | path to a current mesh file for this zone, or `""` |
| `Ariadne.SeedVnavCache` | `() → Task<bool>` | put the mesh into vnavmesh's cache (reload-nudges if a build already started) |
| `Ariadne.FindPath` | `(Vector3 from, Vector3 to, bool fly) → Task<List<Vector3>>` | proxied to Mnemosyne; runs on the raw cached mesh (no festival customization / reachability pruning) |
| `Ariadne.Path.MoveTo` | `(List<Vector3> waypoints, bool fly)` action | follow a precomputed path (Ariadne moves the character itself — no vnavmesh needed) |
| `Ariadne.Path.Stop` | action | |
| `Ariadne.Path.IsRunning` / `NumWaypoints` | `() → bool` / `() → int` | |
| `Ariadne.Path.GetTolerance` / `SetTolerance` | `() → float` / `(float)` | waypoint pass tolerance |
| `Ariadne.Path.GetMovementAllowed` / `SetMovementAllowed` | `() → bool` / `(bool)` | pause/resume without dropping the path |
| `Ariadne.SimpleMove.PathfindAndMoveTo` | `(Vector3 dest, bool fly) → bool` | FindPath + follow, with stall recovery (re-paths up to N times) |
| `Ariadne.SimpleMove.PathfindAndMoveCloseTo` | `(Vector3 dest, bool fly, float range) → bool` | |
| `Ariadne.SimpleMove.PathfindInProgress` | `() → bool` | |
| `Ariadne.CaptureZone` | `() → Task<bool>` | rebuild this zone from the live layout even though a mesh is already cached — the route to a festival/shared-group variant |
| `Ariadne.ReportTraversal` | `(Vector3 from, Vector3 to, string mode, bool success) → Task<bool>` | feedback into the mesh-learning channel: "I drove where the mesh said no" |
| `Ariadne.Path.SteerTo` / `IsSteering` / `RemainingDistance` | `(Vector3)` / `() → bool` / `() → float` | continuous direct steering, and yalms left — deadline arithmetic for dodges |
| `Ariadne.Path.StallCount` | `() → int` | stalls on a path you supplied: Ariadne never mesh-re-paths your waypoints, so re-planning is yours |
| `Ariadne.SimpleMove.PathfindAndMoveToInteract` / `PathfindAndMoveAway` | `(ulong gameObjectId, bool fly)` / `(Vector3 from, float distance, bool fly)` | interact and away goals, checked live while following |
| `Ariadne.SimpleMove.LastResult` | `() → string` | `"goal reached"` / `"N waypoints"` / `"no path (reason)"` / `"waiting for mesh"` / `"stuck (Ny short)"` / `"teleporting to X…"` |
| `Ariadne.SimpleMove.GetUseAetherytes` / `SetUseAetherytes` | `() → bool` / `(bool)` | teleport legs (Lifestream) for this session |
| `Ariadne.Query.Mesh.ReachableCells` | `(Vector3 from, float radius, float cellSize, float minY, float maxY) → Task<(…)>` | exploration grid: which walkable ground is reachable from `from` |
| `Ariadne.Nav.*` / `Ariadne.Query.Mesh.*` | vnavmesh-shaped twins | nearest point, point-on-floor, on-mesh, bitmaps, classified `PathfindDetailed` — for diffing against vnavmesh on the same input |

Movement gates mirror vnavmesh's `Path.*` / `SimpleMove.*` shapes so consumers can switch
by renaming the prefix. While following a path Ariadne publishes the shared-data flag
`ariadne.PathIsRunning` (and mirrors `vnav.PathIsRunning` by default, so plugins that
yield movement to vnavmesh yield to Ariadne unmodified).

**Integrating a plugin against Ariadne?** Read [docs/consumer-ipc.md](docs/consumer-ipc.md).

`/ariadne` opens the status window: connection state, zone mesh status, vnavmesh build
progress, manual seed/reload/pathfind actions, and an activity log.

The Mnemosyne wire protocol is specified in [docs/mnemosyne-protocol.md](docs/mnemosyne-protocol.md).
