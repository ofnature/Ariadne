# Externally-supplied paths and stall recovery

**From:** Minerva (boss-module / auto-dodge plugin)
**Status:** ~~request~~ **LANDED 2026-08-25 (Ariadne session)** — all three items, including
both optionals. Diagnosis was accepted as-is: the shared subscription was a real design bug.

**What shipped:**

- **Ownership tracking** — `PathFollower.Move` takes `external` (default **true**: any
  caller that doesn't explicitly claim ownership is treated as supplying its own
  waypoints; only `MoveRequest` passes false). `MoveRequest.OnStalled` returns
  immediately for external paths — no `Stop()`, no mesh re-path, no futility/`RetriesUsed`
  pollution. Your corners keep walking through a knockback; re-issue on your own tick as
  planned. Applies identically on the `Ariadne.*` and `vnavmesh.*` (compat) prefixes.
- **Per-call tolerance** — `Ariadne.Path.MoveToWithTolerance(List<Vector3>, bool fly,
  float tolerance)`: waypoint-pass tolerance for that path only; the global
  `Path.SetTolerance` is untouched and applies again to the next plain `MoveTo`.
- **Stall observability** — `Ariadne.Path.StallCount` `() → int`: stalls detected on the
  *current* path, reset on every `Move`/`Stop`. A rise while your path runs means
  "re-plan with the geometry knowledge you have" — the stall detectors (displacement +
  progress-toward-destination) keep running on external paths; only the *recovery* is
  yours.

One behavioural note: knockbacks/stuns will still tick `StallCount` upward during a long
hold — the detectors can't tell "stunned" from "wedged". If that produces noise for you,
say the word and stall detection can be gated off entirely for external paths instead.

## The problem

`MoveRequest` subscribes to `PathFollower.OnStalled` in its **constructor**, so it is subscribed for the
plugin's lifetime, and the handler has no check for whether it owns the path currently being walked:

```csharp
// MoveRequest.cs — constructor
_follower.OnStalled += OnStalled;

// MoveRequest.cs — handler
RetriesUsed++;
_follower.Stop();
Request(destination, fly, range);   // re-path through the mesh to the final waypoint
```

`Path.MoveTo` (both the `Ariadne.*` and `vnavmesh.*` prefixes) calls `follower.Move(waypoints, fly)`
directly, bypassing `MoveRequest`. But the follower's stall event is global, so a stall on an
externally-supplied path still lands in `MoveRequest.OnStalled`.

## Why that breaks Minerva

Minerva computes a **danger-aware** route: a grid solve over the arena where cells covered by an AOE cost
several times a clear cell, string-pulled down to its corners. It then hands the whole path to
`Path.MoveTo`, precisely because the follower walks the points it is given and does no pathfinding of its
own. The corners *are* the safety.

When a stall fires, `MoveRequest` replaces that with a mesh path to the final waypoint. The mesh knows
about walls; it knows nothing about the AOE the route was bending around. The character then walks the
straight line through it.

Stalls are not an edge case in this context. During a boss fight the character is routinely halted by
knockbacks, stuns, ability lock, and brief collisions with other players — none of which mean the path is
wrong, and all of which currently discard it.

Two smaller consequences of the same shared subscription:

- `_futility` accumulates across callers. A dodge that stalls twice burns budget belonging to a later
  travel request, which can then give up early with "recoveries without gaining ground".
- `RetriesUsed` / `LastResult` report on paths `MoveRequest` never issued.

## What we would like

**Ownership tracking.** `MoveRequest` should only act on `OnStalled` for paths it issued itself. Something
as small as a flag set in `PathFollower.Move` — "this path came from outside" — that `MoveRequest.OnStalled`
returns on, would be enough. The follower already keeps going unless the handler calls `Stop()`, so
ignoring the event leaves the externally-supplied path walking, which is the behaviour we want: the
character keeps following our corners, and we re-issue a fresh route on our own tick anyway (we re-solve
every frame and re-issue whenever the route meaningfully changes).

That is the whole ask. Everything else we need already exists.

## Optional, if it is cheap

- **Per-call tolerance.** `Path.SetTolerance` is global state, so setting it for a dodge stomps whatever
  the user chose for travel. A tolerance argument on `Path.MoveTo` would let us ask for tighter arrival
  without touching their config.
- **A way to observe stalls over IPC.** Not needed if ownership tracking lands — we can poll
  `Path.IsRunning` — but if a stall is surfaced (a counter, a flag), we can re-plan with the geometry
  knowledge Ariadne does not have, which is the right place for that decision to live.

## What we are *not* asking for

- No mesh work. Route-following needs no mesh, so this path is unaffected by build state — which is a
  point in Ariadne's favour over vnavmesh, where a fight can start while the mesh is still building.
- No pathfinding changes. `Nav.PathfindAvoid` and the `Query.Mesh.*` family are useful but we do not need
  them for dodging; Minerva already raycasts real game collision for floor checks, which is more
  authoritative than a navmesh for "will I fall".

## Context

Minerva drives movement exclusively through this IPC when a backend is ready, and stands down entirely
while `Path.IsRunning` is true, so the two plugins never write movement input on the same frame. Keeping
execution in Ariadne is deliberate on our side: Minerva owns *where is safe*, Ariadne owns *how to get
there*. This request is about making sure the first half survives a stall.
