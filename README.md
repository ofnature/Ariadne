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

> **Status: early development.** Zone detection scaffold; no mesh logic yet.

## How it fits together

| Project | Role |
|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) | Builds and consumes navmeshes in-game |
| Mnemosyne | Owns the mesh cache outside the game process; serves and (later) builds meshes |
| **Ariadne** | In-game bridge: zone detection, cache seeding, consumer IPC |
| [Theseus](https://github.com/ofnature/Theseus) | Downstream consumer (dungeon running) |

## IPC (planned)

`Ariadne.IsConnected`, `Ariadne.CurrentCacheKey`, `Ariadne.ZoneStatus`,
`Ariadne.RequestMesh`, `Ariadne.SeedVnavCache`, `Ariadne.FindPath`.

The Mnemosyne wire protocol is specified in [docs/mnemosyne-protocol.md](docs/mnemosyne-protocol.md).
