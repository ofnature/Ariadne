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

> **Status: early development.** Zone detection, Mnemosyne round-trip, cache seeding, and
> the consumer IPC surface work against a stub server; real Mnemosyne integration pending.

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

Movement gates mirror vnavmesh's `Path.*` / `SimpleMove.*` shapes so consumers can switch
by renaming the prefix. While following a path Ariadne publishes the shared-data flag
`ariadne.PathIsRunning` (and mirrors `vnav.PathIsRunning` by default, so plugins that
yield movement to vnavmesh yield to Ariadne unmodified).

**Integrating a plugin against Ariadne?** Read [docs/consumer-ipc.md](docs/consumer-ipc.md).

`/ariadne` opens the status window: connection state, zone mesh status, vnavmesh build
progress, manual seed/reload/pathfind actions, and an activity log.

The Mnemosyne wire protocol is specified in [docs/mnemosyne-protocol.md](docs/mnemosyne-protocol.md).
