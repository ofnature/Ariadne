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

> **Status: v0.1.1, released and installed from the shared listing.** Ariadne runs against the real
> Mnemosyne service over the pipe — no stub: zone detection, cache seeding, out-of-process builds
> and the consumer IPC surface are live, and the movement chain has been verified in-game (a
> character walked a Mnemosyne-computed path with vnavmesh not involved at all). The `vnavmesh.*`
> gates are claimable by policy, so existing consumers can migrate one feature at a time. Since the
> bundle landed the package also **carries the service itself** — v0.1.1 and earlier are the plugin
> alone. Still owed: the `mount` / `dismount` / `jumpOff` leg transitions, and the in-game pass over
> the `meshNotReady` retry. Per-gate detail: [docs/consumer-ipc.md](docs/consumer-ipc.md).

## How it fits together

| Project | Role |
|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) | Builds and consumes navmeshes in-game |
| Mnemosyne | Owns the mesh cache outside the game process; serves and builds meshes — shipped inside this package as `service/` |
| **Ariadne** | In-game bridge: zone detection, cache seeding, consumer IPC |
| [Theseus](https://github.com/ofnature/Theseus) | Downstream consumer (dungeon running) |

## What's in the package

Ariadne answers nothing without a service on the other end of the pipe, so the release package
carries one: installing from the listing is the whole install.

| In `latest.zip` | Size | What it is |
|---|---|---|
| `Ariadne.dll`, `Ariadne.deps.json`, `Ariadne.json` | ~130 KB | the plugin, flat at the archive root — Dalamud does not find files nested in a folder |
| `service/` | 95 MB on disk, ~41 MB in the zip | `Mnemosyne.Service.exe` and `Mnemosyne.Cli.exe`, self-contained for win-x64: symbols and doc XML stripped, one shared runtime, no .NET install required |

The payload is **staged, not run where it sits**. On first use Ariadne copies `service/` to
`%APPDATA%\Mnemosyne\service\<version>\` and launches that copy, because a running service locks its
own DLLs — one launched out of the plugin folder would block Dalamud's next plugin update from
replacing them. The copy runs off the game thread, once per bundled version, and a stage that dies
partway is redone.

A service you built yourself still wins. Autostart resolves in this order:

1. the path in the plugin config (`MnemosyneServicePath`)
2. the marker `%APPDATA%\Mnemosyne\service.path`, which every service and CLI run rewrites
3. the bundled payload — staged and launched as described above

So a machine with a hand-built service (or a development checkout) keeps running its own build and
never pays for the copy, while a fresh install gets a working service with nothing to fetch. The
payload is produced by [tools/bundle-mnemosyne.sh](tools/bundle-mnemosyne.sh) and verified as part
of [RELEASING.md](RELEASING.md), which also lists what the build step needs.

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
