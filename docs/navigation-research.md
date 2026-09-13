# Navigation research — alternatives surveyed and what to steal

Written 2026-08-25. Reference for the "smarter nav" follow-on project: everything
surveyed while asking *"is there anything besides vnavmesh we could use or adapt for
FFXIV?"*, with the parts that can improve **our** stack (Ariadne + Mnemosyne) marked
**⭐ ADOPT** and assigned an owner.

## The goal, stated as behaviors

"Smarter nav" means five concrete things (user, 2026-08-25):

1. **Awareness of surroundings** — don't hug walls, don't clip lampposts, prefer roads.
2. **Aetherytes to shorten distance** — teleport when it's faster, as part of the plan.
3. **Fly mid-air, not hugging the ground** — climb to clear sky, cruise straight, descend.
4. **Never caught in geometry** — no wedges, and recover when it happens anyway.
5. **Doors** — walk through interiors instead of routing around them. Doors auto-open in
   FFXIV; nothing needs interacting with. The failure is the *mesh*: door colliders bake as
   solid (the red boxes), so routes stop at or detour around an opening the world lets you
   walk through.

Where each stands today is in the table at the end. The survey's conclusion up front:
**there is no third engine to download.** Every real alternative is either a Recast
navmesh (what we run) or a hand-authored waypoint graph (what the closed bots run), and
the winning design is the hybrid — mesh for free movement, a curated graph of doors /
links / aetherytes layered over it. We already hold both halves.

---

## 1. FFXIV-specific (what actually exists in the ecosystem)

| Project | What it is | Verdict |
|---|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) (awgil) | The open in-game navmesh; every open consumer depends on its 17 IPC gates | The thing being replaced. Its cache format (NVMD v25) is our interop contract |
| [ffxiv_navmesh-cn](https://github.com/AtmoOmen/ffxiv_navmesh-cn) (AtmoOmen) | Active fork: "thickened" VoxelMap, worker-process builds, segment/FastLZ lazy cache (v39, incompatible both ways) | **Reference only.** Worth an A/B for fly paths and build times; never a component |
| **WigglyNav-class closed "smart nav"** | Private, Discord-distributed. Screenshots show a **waypoint-graph editor** (green route splines, node markers, orange edit boxes) and an **interactable/object view** (hit volumes on NPCs/lamps). Features seen: parallel tile builds, stall recovery with a conservatism setting, gil-aware cross-zone travel planner, teleport tickets | Not indexed anywhere; obtainable only from its author. If a DLL turns up: string-scan for IPC gates (the scan used on XASlave works), and if it has `Path.MoveTo`-style gates, wire it as a third backend in Theseus/Minerva for A/B |
| [Lisbeth](https://www.siune.io/products/lisbeth) | Paid crafting bot with its own nav: hand-authored flight graphs + per-zone navigation areas | The pure graph paradigm. Cheap per zone, robust, useless off-graph |
| [Miqobot](https://miqobot.com/forum/forums/topic/help-navigation/) / [RebornBuddy](https://rebornbuddy.com/) / [MMOMinion](https://wiki.mmominion.com/doku.php?id=ffxiv-navmeshes) | External bots. Miqobot: 3D waypoint editor with typed waypoints + connections. MMOMinion: recorded meshes with **area types (Road / LowDanger / HighDanger)** and hand-placed off-mesh links. RB: server-distributed premade meshes | Closed. But ⭐ MMOMinion's area-type costing and RB's central mesh store are precedents for cost painting and the community-store tier |
| **XASlave** (aethertek.io, closed, installed on this machine) | Automation consumer — drives movement through 9 `vnavmesh.*` gates | Not an engine. **Add to the flip's smoke ladder**: an external consumer we never surveyed that the compat surface covers |
| [Lifestream](https://github.com/NightmareXIV/MyDalamudPlugins) (NightmareXIV) | Aetheryte / aethernet / world / house travel automation with IPC | ⭐ **ADOPT (Ariadne)** — the *executor* for teleport legs. Mnemosyne decides "teleport wins on cost", Ariadne calls Lifestream instead of reimplementing aethernet traversal |
| Questionable, QuestFlow, AutoDuty, GatherBuddyReborn | Consumers of vnavmesh; Questionable/AutoDuty carry **recorded route files** | ⭐ Their route files are graph edges for the hybrid (Odysseus already imports AutoDuty paths) |

## 2. Other MMO bot navigation (same problem, solved years ago, open source)

All Recast-based like us — the value is their *engineering*, not their engine.

| Project | Game | ⭐ What to take | Owner |
|---|---|---|---|
| [MQ2Nav](https://github.com/brainiac/MQ2Nav) | EverQuest | Architectural twin (in-game plugin + external **MeshGenerator** editor). **Convex-volume area painting** (cost painting UI + data model, done); **dynamic-object export** (in-game plugin dumps door/object geometry for the offline builder — our `buildZone`, proven); **runtime door clicking** while following ("any door <20 y, every 2 s"); geometry boundary filtering; [MeshUpdater](https://www.redguides.com/community/resources/meshupdater-mq2nav-meshes.1270/) — an open **community mesh distribution** channel (the deferred tier, with precedent). Mnemosyne's plan already cites its `NavMeshLoader` auto-reload and `SwitchHandler` | doors → **Ariadne**; volumes, distribution → **Mnemosyne** |
| [AmeisenNavigation](https://github.com/Jnnshschl/AmeisenNavigation) | WoW | **Navigation server** bots query over TCP — validates Mnemosyne's architecture. **Path smoothing post-passes: Chaikin curve, Catmull-Rom, Bezier** (~50 lines each, trivially portable to C#) — the direct fix for "bottish" polyline following, cleaner than vnavmesh's cost-noise Randomness Multiplier (which only perturbs fly-search g-scores) | **Mnemosyne** (post-process next to PathPadding) |
| [Namigator](https://github.com/namreeb/namigator) | WoW | **Build every map offline, up front** from game files; library / tools / bindings layering; test-harness discipline. Rust/Python bindings exist | **Mnemosyne** — prebuild-all-zones job |
| TrinityCore mmaps (what Ameisen consumes) | WoW | Server-authoritative meshes for all maps, **versioned** | Mnemosyne — same idea, versioning done right |
| [FFxi-Navmesh-Builder](https://github.com/Xenonsmurf/FFxi-Navmesh-Builder) | FFXI | Recast + collision extraction for the other Final Fantasy | Reference for collision-file parsing patterns only |

## 3. Other domains (the genuinely different designs)

These are where the *planner* ideas live. None are drop-in (Java / C++ / ROS / GPL C);
all are open, and the piece to take is a design.

| Project | Domain | ⭐ What to take | Solves behavior |
|---|---|---|---|
| [Baritone](https://github.com/cabaletta/baritone) ([design write-up](https://github.com/WiegerWolf/baritone-ts)) | Minecraft, open since 2018 | A\* over **typed movements with real costs** (~20 kinds: walk, ascend, descend, parkour, swim, climb…) and **goal types** (`GoalNear`, `GoalXZ`, `GoalRunAway`) instead of "go to XYZ"; multi-coefficient search that degrades gracefully under a time budget | **#2 #5 + transitions**: teleport, mount, door-open, land become ordinary movement types with costs; "within interact range" is a goal type. The textbook for Mnemosyne's legged planner (11a) and for Ariadne's `SimpleMove` goal surface |
| **Quake III AAS** ([id-Software source](https://github.com/id-Software/Quake-III-Arena), GPL) | 1999 FPS bots | Precomputed **reachabilities** between areas — walk, jump, ladder, swim, jump-pad, **teleporter** — with cached routing tables: any-to-any route cost is a lookup | **#2**: a teleporter as a graph edge *is* an aetheryte; route caches = Mnemosyne's "precomputed connectivity" item |
| [EGO-Planner](https://github.com/ZJU-FAST-Lab/ego-planner) / [Fast-Planner](https://github.com/HKUST-Aerial-Robotics/Fast-Planner) | Quadrotor robotics (ZJU / HKUST) | Smooth **B-spline trajectories through free 3D space** respecting clearance and dynamics, ~1 ms replanning; Fast-Planner's topological search yields *multiple distinct routes* (over vs around) | **#3**: smooth cruise-altitude flight instead of voxel polylines — the altitude preference is exactly their clearance-gradient term |
| **ROS Nav2 layered costmaps** / [OctoMap](https://octomap.github.io/) | Ground + aerial robotics | Costmap as **stacked layers** — static, obstacle, **inflation** (clearance), custom — merged at query time; OctoMap is the reference occupancy octree | **#1**: Mnemosyne's PathPadding *is* an inflation layer reinvented. Danger (mob camps), cost paint, Minerva's AOEs, audited obstacles: all one abstraction instead of four mechanisms |
| [Game AI Pro 3 ch. 21 — SVO flight nav](https://www.gameaipro.com/GameAIPro3/GameAIPro3_Chapter21_3D_Flight_Navigation_Using_Sparse_Voxel_Octrees.pdf) (Warframe), [Nav3D](https://github.com/darbycostello/Nav3D), [Lazy Theta\*](http://idm-lab.org/bib/abstracts/papers/aaai10b.pdf) | Games / search | Sparse voxel octree + any-angle search | **#3** — **already landed** in Mnemosyne (9b): 1.1–1.8 km hops 1.5 s partial → 5–8 ms full paths |
| [Game AI Pro ch. 20 — precomputed MMO pathfinding](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter20_Precomputed_Pathfinding_for_Large_and_Detailed_Worlds_on_MMO_Servers.pdf) | MMO servers | Per-zone path tables computed offline | reachability in O(1) for planners (Theseus/Odysseus) |
| [recastnavigation](https://github.com/recastnavigation/recastnavigation) / [DotRecast](https://github.com/ikpil/DotRecast) | Engine libs | The industry-standard mesh generator and its C# port | **Already our foundation.** Swapping Recast flavors gains nothing; the FFXIV-specific hard part is the input pipeline, which is done |

## 4. Highlights — the adoption list, mapped to the five behaviors

| # | Behavior | Status today | ⭐ Adopt | Owner |
|---|---|---|---|---|
| 1 | Surroundings awareness | Padding (0→0.95 m clearance) and obstacle dodging landed, verified in-game. Open: cost painting, vetting 1,949 obstacle candidates | **Costmap layers** (Nav2) as the single abstraction; **area-type costing** (MMOMinion) for roads/danger; **convex-volume painting UX** (MQ2Nav MeshGenerator) | Mnemosyne |
| 2 | Aetherytes shorten distance | Not started (planner has teleport legs on paper; Odysseus runs a placeholder rule) | **Typed movements with costs** (Baritone) — teleport as a movement; **teleporter reachabilities + route cache** (AAS); **Lifestream as executor** | plan: Mnemosyne · execute: Ariadne |
| 3 | Fly mid-air | SVO/Theta\* landed (near-straight in open sky); near terrain the volume weaves and ground often wins on ETA | **Clearance-gradient altitude preference + B-spline smoothing** (EGO/Fast-Planner); **topological alternatives** (over vs around) | Mnemosyne |
| 4 | Never caught in geometry | Stall recovery (progress budgets, futility counters) landed, unit-tested, not wedge-proven; per-leg through-solid validation open | **Chaikin / Catmull-Rom smoothing** (Ameisen) so corners aren't scraped; **prebuild every zone** (Namigator/Trinity) so nobody navigates on a half-built mesh; `reportTraversal` evidence feeding obstacle vetting | Mnemosyne; Ariadne keeps the executor |
| 5 | Doors | Mesh-side only (corrected 2026-09-12: FFXIV doors auto-open, so there is no runtime rule to port). Mnemosyne's `doors` sweep already finds baked-closed doors (Ul'dah `sgbg_w1t0_a0_door2`); its known gap is "doors bake as authored" | **Doors as known, passable objects with a link through the opening** (user's direction): auto-listed door objects each get an off-mesh connection on the walk mesh *and* the same edge known to the volume, so a fly route can reach the door and continue as a walk leg through it; the `doors` sweep as the regression check; carving the collider out at build is the fallback | Mnemosyne (link data + both planners); Ariadne executes the leg boundary |

Cross-cutting: **goal types** on the consumer surface (`GoalNear` = `MoveCloseTo` today;
add interact-range and run-away goals) — Ariadne; **community mesh distribution** with an
updater (MQ2Nav MeshUpdater / RB model) — the deferred tier, now with precedent.

## 5. Suggested order for the later project

1. **Door links** (Mnemosyne — auto-listed doors become links known to both the mesh and
   the volume; doors auto-open in-game so nothing else is needed) and **teleport-leg
   execution via Lifestream** (Ariadne, small, works before the planner emits legs, using
   a local rule).
2. **Movement-type planner model** (Mnemosyne) — refactor 11a's legs into Baritone-style
   typed movements + goal predicates; teleport, mount, door become entries, not cases.
3. **Costmap layers** (Mnemosyne) — unify padding, obstacles, cost paint, danger.
4. **Flight smoothing + altitude** (Mnemosyne) — B-spline/Catmull-Rom over the Theta\*
   corridor with a clearance gradient.
5. **Prebuild all zones** (Mnemosyne) — then `buildZone` is variant-only.
6. **Community mesh store** — remote topology + updater, once 5 has content to share.

## Sources

vnavmesh · ffxiv_navmesh-cn · Puni.sh directory · Lisbeth · Miqobot navigation editor ·
RebornBuddy · MMOMinion navmesh wiki · Lifestream (NightmareXIV) · MQ2Nav + MeshUpdater ·
AmeisenNavigation · Namigator (+ namigator-rs, wow-navmesh) · FFxi-Navmesh-Builder ·
Baritone (+ baritone-ts) · Quake III Arena source · EGO-Planner · Fast-Planner · OctoMap ·
Game AI Pro 3 ch. 21 · Game AI Pro ch. 20 · Lazy Theta\* (AAAI 2010) · Nav3D ·
recastnavigation · DotRecast. Links inline above.
