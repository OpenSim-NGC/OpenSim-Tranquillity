<!--
  Copyright (c) 2026 Legion Builds

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
-->

# Jolt physics

The Jolt module is an optional physics engine for the region server, built on
[Jolt Physics](https://github.com/jrouwe/JoltPhysics) through the joltc native library and the
JoltPhysicsSharp binding. Each region gets its own Jolt physics system. LSL vehicles are driven by a
controller built on the Second Life vehicle model (motors, friction, hover, the vertical attractor,
banking and deflection), and by default they behave as they do on the InWorldz Halcyon simulator,
where that differs from Second Life's documentation. `VehicleBehaviour = SecondLife` makes a region
follow Second Life's documentation instead (see "Vehicles" below).

This guide is for operators. It covers selecting Jolt, its settings, its console commands, and
the platforms it runs on today.

## Platforms

The module runs on joltc, the native library of the `JoltPhysics.Native` package (version 1.0.4,
the one JoltPhysicsSharp 2.19.1 depends on). The build puts the package's file for each platform
in the output:

| Platform | File in the build output | Tested by this project |
|---|---|---|
| Windows x64 | `runtimes/win-x64/native/joltc.dll` | yes |
| Linux x64 (glibc) | `runtimes/linux-x64/native/libjoltc.so` | yes |
| Windows Arm64 | `runtimes/win-arm64/native/joltc.dll` | no |
| Linux Arm64 (glibc) | `runtimes/linux-arm64/native/libjoltc.so` | no |
| macOS (x64 and Arm64) | `runtimes/osx/native/libjoltc.dylib` | no |

On a platform this project does not test, the module starts and logs a notice saying so.
musl-based Linux (Alpine) has no native: there, keep another physics engine.

All regions in a process share one job pool on this native (`JobPools`, below): its physics systems
share one scratch allocator, so two physics updates at once would stop the process. More pools need a
change in joltc itself. The recorded files and their hashes are in
[native/joltc/README.md](../native/joltc/README.md).

A build with no runtime identifier carries every platform's file, so the same output runs on each.
A `dotnet publish -r <rid>` puts that platform's file beside the assemblies instead, where the
module also finds it. Copy the whole output, including its `runtimes` folder.

### What is supported

| Setup | Status |
|---|---|
| One region per region server process | Tested and supported |
| Several regions in one process, on one job pool (`JobPools = 1`, the default) | Tested and working: the regions take turns on the pool |
| More than one job pool (`JobPools` above 1) | Not available: the module runs one pool |

When `JobPools` asks for more than one pool, the region server still starts and every region runs.
The module runs one pool, sized as `JobPools = 1` would size it, logs one warning at start with the
setting, the reason and the remedy (shown below, under "The native check at start"), and
`jolt capacity` shows the pools in use and why. All the process's regions then take turns on that
pool, so a region whose physics is busy can make the others wait for their step; no step is skipped.

### When the JoltPhysics.Native version changes

The module knows each native file by its SHA-256, so a new `JoltPhysics.Native` version (usually
through a JoltPhysicsSharp update) is refused at start until its files are recorded, and
`JoltNativePackageTests` fails on it with what to check. Before recording it:

1. Re-check the rules that make one shared scratch allocator safe on one pool, against the joltc
   and Jolt sources the new package is built from: the list in `JoltNativePackageTests`
   (`ShapeAndAllocatorRuleTests` checks the module's side of each rule).
2. Record the new files: `JoltNative.PackageVersion` and the stock entries of `JoltNative.Known`,
   the stock table of `assert-joltc-native.ps1` and the stock table of `native/joltc/README.md`.
3. Run the whole Jolt suite and the harness on the new files, and record the harness baselines of
   the new version on Windows x64 and Linux x64 ("Physics harness", below). Until they are
   recorded, the regression check reports the new files as having no baseline.

### The native check at start

When `physics = Jolt`, the module picks the file for the platform it runs on, computes its SHA-256
and compares it with the builds it has a record of. With a good file it logs one line, once per
process, naming the build:

```
[JOLT SCENE] joltc for linux-x64: /opt/opensim/bin/runtimes/linux-x64/native/libjoltc.so sha256 5FC05170... (stock JoltPhysics.Native 1.0.4; one job pool)
```

With `JobPools` above 1 it also logs one warning, and runs one pool:

```
[JOLT SCENE] [Jolt] JobPools = 3, but the module runs ONE job pool: the loaded joltc (stock JoltPhysics.Native 1.0.4) is not safe for more than one job pool: its physics systems share one scratch allocator, and two physics updates at once would stop the process. Regions take turns on that pool. More than one pool needs a change in joltc itself; set [Jolt] JobPools = 1.
```

In the cases below the module logs one error line and throws from its `Initialise`, before any
region's physics exists, the same way as the meshing check under "Selecting Jolt"; the exception
is not caught on the way up, so the simulator does not finish starting. The cases:

- the platform has no native:
  `Jolt physics has no native library for this platform (linux-musl-x64). Supported platforms: win-x64, linux-x64, win-arm64, linux-arm64, osx-x64, osx-arm64. Choose another physics engine in [Startup] physics.`
- the file is missing: `Jolt physics: the native library for linux-x64 is missing: <path>. ...`
- the file's hash is not one the module has a record of:
  `Jolt physics: <path> has sha256 <hash>, which is not a joltc build this module has a record of for linux-x64 (...). ...`

`[Jolt] AllowUnrecordedNative = true` (default `false`) loads a file whose hash the module does not
know, with the start line logged as a warning instead. Use it only for a joltc you built yourself;
the module runs one job pool on it.

## Selecting Jolt

Jolt is chosen per simulator in `[Startup]`, and it needs the Meshmerizer mesher:

```ini
[Startup]
    physics = Jolt
    meshing = Meshmerizer
```

- With any other `physics` value, or none, the module does nothing for any region: the native
  is not loaded or hashed, so a missing native, an unrecognised one or a platform with none
  (musl Linux) makes no difference; no `[Jolt]` key is read, so a missing or invalid `[Jolt]`
  section is not reported; no job pool, thread or timer starts; no `jolt` console command is
  registered, test commands included; and the module logs nothing. The host itself still loads
  the Jolt assemblies, as it loads every plugin assembly in its folder to look for modules, and
  its own debug lines name the module (`[REGIONMODULES]` finding it and adding each region to
  it).
- `physics` is read from the simulator's configuration, as every engine reads it, so all the
  regions of one simulator run the same engine.
- With `physics = Jolt` and any `meshing` other than `Meshmerizer`, the module logs that meshing
  must be Meshmerizer and throws "Invalid physics meshing option for Jolt" when it initialises.
- The shipped default stays `physics = ubODE`.

`assert-joltc-native.ps1` (next to the backend's project file, `Source/OpenSim.Region.PhysicsModules.Jolt.Backend`) checks a build or publish directory
before it is deployed: every `runtimes/<rid>/native/` joltc must be a build the module has a
record of for its platform, and no
other joltc file may be there (`-AllowStray` reports such files without failing, for an
installation that still holds files from an older deploy). It runs in Windows PowerShell and in
PowerShell 7 on Linux:

```powershell
powershell -File assert-joltc-native.ps1 -PublishDir "<publish directory>"
```

```
pwsh -File assert-joltc-native.ps1 -PublishDir "<publish directory>"
```

## Settings

All settings are in the `[Jolt]` section. `OpenSimDefaults.ini` lists every key with its default
and what it does; copy a key into `OpenSim.ini` to change it. An invalid value logs a warning that
names the value and the default used instead. The keys cover gravity, solver sub-steps and iterations, the worker threads
and job pools shared by all regions in the process, body / pair / contact capacities (optionally
scaled with region area for var regions), the per-frame update buffers, the avatar jump speed,
the avatar walk, run and fly speeds (see "Avatar speeds"),
how often capacity warnings are logged, the physics step rate (below), the vehicle settings (the
share of gravity on a ground vehicle, the type presets, the limits and the sled's assist; see
"Vehicles"), the engine's body speed caps, the prims' damping (see "Physics material on objects"), the limits on script ray casts and pushes (below),
`AllowUnrecordedNative` (above), `BodyEnhancedEdgeRemoval` and `AvatarEnhancedEdgeRemoval` (see "Seams in
triangle meshes and terrain"), `KeyframedMotionCarriesRiders` and `MovedPrimsCarryRiders` (see "Prims that keyframed motion moves" and "Prims moved by
plain sets"), and `TestCommands` (below).

### Physics step rate

`[Jolt] PhysicsStepRate` runs physics at a set rate inside each region heartbeat. The default is 45:
fixed 45 Hz physics steps, whatever the heartbeat (`[Startup] FrameTime`, about 11 per second).
`PhysicsStepRate = 0` runs one physics step per heartbeat instead:

```ini
[Jolt]
    PhysicsStepRate = 45            ; the default; 0 = one physics step per heartbeat
    PhysicsStepCollisionSteps = 2
```

`OpenSimDefaults.ini` leaves `PhysicsStepRate` commented out, so the key is set only where an
operator sets it; that decides how a rate the heartbeat cannot honour is reported (below).

- Each step is exactly 1/45 s. An 11 Hz heartbeat runs 4 or 5 steps; the time left over carries
  into the next heartbeat, so the long-run rate is exact.
- In every step: the vehicle controllers, the avatar step, and forces and changes queued by
  scripts. Once per heartbeat: positions and velocities sent to the scene (and from there to
  viewers), the settle update of a body that came to rest, and collision events.
- Collision events cover every step of the heartbeat. A touching pair counts once per heartbeat,
  as before. A contact that begins and ends inside one heartbeat gives `collision_start` in that
  heartbeat and `collision_end` in the next.
- `llApplyImpulse` and `llApplyRotationalImpulse` on a physical object give the same velocity
  change as with one step per heartbeat, and so does a force the scene marks as a push. A force or
  torque set with `llSetForce` or `llSetTorque` acts in every step.
- `PhysicsStepCollisionSteps` replaces `CollisionSteps` while the rate is on: the solver's
  sub-steps per physics step. 2 at 45 steps per second slices the solver at 90 Hz. The module
  takes more where that would slice it slower than 60 Hz, the step Jolt is built around ("In
  general, the system is stable when running at 60 Hz with 1 collision step", Jolt's
  Docs/Architecture.md): 3 at 22.5 steps per second. Ten stacked boxes stepped in 1/45 s solver
  steps can sway and never fall asleep.
- A heartbeat runs at most 16 steps. A heartbeat that would need more runs 16 and drops the rest
  of its time; `jolt capacity` shows how many heartbeats did ("physics steps").
- A rate set below the heartbeat's own rate is refused with one warning at region start, and the
  region runs one step per heartbeat. With the key not set, a heartbeat faster than 45 Hz runs one
  step per heartbeat too, and the region logs one information line saying so, not a warning.
- A value that is not a number from 0 to 1000 (text, a negative number, above 1000) logs one
  warning naming the value and the rate used, and the region steps at the default 45. It counts as
  not set.
- The keys are read once, when the region starts.

What changes at 45: vehicles and avatars integrate in 1/45 s steps, so anything whose behaviour
still depends on the step changes. The vehicle motors, friction, gravity, hover, the vertical
attractor, banking and deflection, a jump's rise and walking speed do not: each is stepped exactly
(see "Vehicle motors and friction" and the sections after it), so in the harness the test car on level ground leaves the key at 3.79 m/s
either way and an avatar jump rises 0.82 m (under `[Jolt] VehicleBehaviour = SecondLife`; under `InWorldz` the two motors run on Halcyon's 15 ms
clock, which gives the same speed at every step rate, see "Vehicle behaviour"). So does the sled's slope assist.
Position updates to viewers, timers and sensors stay at the heartbeat rate. Physics costs more:
in the harness the test car takes about 1.5 times the step time of one step per heartbeat
(`jolt metrics` shows each region's step time).

### Collision repeat rate and time dilation

At any step rate, a part that keeps touching another object is sent `collision` no faster than once in 0.13 s of
simulated time. The collision event's wiki page says "Smallest repeat rate is approximatively 0.13 seconds", so at an
11 Hz heartbeat a repeat comes every second heartbeat (about 0.18 s). A new contact (`collision_start`), a contact that
went (`collision_end`) and the first update after a gap are never held back. Contact with the ground alone
(`land_collision`, whose page gives no rate) and avatars' updates (their collision plane and impact damage) still
come every heartbeat.

The region's time dilation (`llGetRegionTimeDilation`; 0.0 is full dilation, 1.0 none) is the simulated seconds of
the last 11 heartbeats over the real seconds they took, at most 1, so a region that falls behind says so. Before
this the Jolt scene always reported 1.

### Script ray casts and pushes

Scripts written by anyone can cast rays and push avatars. These keys bound what they can make one
region do. Each guards against abuse, so each defaults to its safe value; all are read once, when
the region starts.

| Key | Default | What it limits |
|---|---|---|
| `RayCastBudgetMs` | 5 | Time (ms) one region's script ray casts may take in one heartbeat, at any `PhysicsStepRate`: the casts the module can tell are a script's (Phlox's `llCastRay`). |
| `RayCastSimulatorBudgetMs` | 5 | Time (ms) every other ray cast may take in one heartbeat: the simulator's own (rez placement, landing after login or teleport) and YEngine's `llCastRay`. |
| `RayCastMaxTestedHits` | 1024 | Hits the engine may report to one cast before it is cut short (a mesh reports each triangle the ray crosses). |
| `RayCastMaxHits` | 256 | Hits one cast returns, Second Life's documented maximum for `RC_MAX_HITS` (https://wiki.secondlife.com/wiki/LlCastRay). |
| `AvatarPushMaxSpeed` | 100 | The ceiling (m/s) on the speed pushes can give an avatar. A strong push reaches it; a weaker one stays below. 0: pushes do not move avatars. |
| `AvatarPushRecovery` | 5 | How fast (m/s per second) an avatar's push allowance refills. |
| `AvatarObjectPushPerMomentum` | 100 | Force (N) an avatar pushes a physical object with, per unit of its own momentum into the object (its mass times its speed). Halcyon's figure. |
| `AvatarObjectPushMaxForce` | 2000 | The most force (N) an avatar pushes a physical object with. |

**Ray casts.** A cast holds the region's physics lock, and the region's physics step waits for it
while holding its job pool, so a slow cast delays that region and every region sharing its pool.
The module bounds a script cast three ways:

- It keeps only the closest hits the script asked for, and once it has them the engine skips
  everything further along the ray. A long ray through a pile of prims costs about what the
  part of it up to those hits costs.
- A ray is cut to the region widened by 8 m on each side (the Second Life page for `llCastRay`, https://wiki.secondlife.com/wiki/LlCastRay, says random failures seem to happen when a ray starts or ends more than 8 m outside the current region's bounds), and
  between heights -128 and 10000 m. A ray with a non-finite start, direction or length hits nothing.
- A cast that runs past what is left of its budget in the heartbeat, or past `RayCastMaxTestedHits`,
  is cut short; once the region has spent a budget in a heartbeat, further casts charged to it are
  refused until the next one. Either way the script gets `RCERR_CAST_TIME_EXCEEDED` (-3), which Second
  Life documents as "the parcel or agent has exceeded the maximum time allowed for raycasting. This
  resource pool is continually replenished, so waiting a few frames and retrying is likely to
  succeed." That holds for Phlox's `llCastRay`, which reports an error from the physics query as
  that code (it logs each one as a warning). OpenSim's `llCastRay` (LSL_Api, used by YEngine) cannot
  be told: it gets no hits from the physics engine, and returns only what it tests itself (avatars,
  phantoms and, for long rays, the ground) with a status of that many hits.

What a cast finds follows Second Life's `llCastRay` page (https://wiki.secondlife.com/wiki/LlCastRay): a ray that starts inside a prim or an
avatar does not hit it (the page says a prim is not detected when the line starts inside it), and
goes on to what lies beyond. This holds for both entry points and both budgets. On the 5-argument
`RaycastWorld` the types a cast asks for are filtered inside the engine, so a type the script
rejects never takes one of its `RC_MAX_HITS`; on the 4-argument entry the caller sees every solid
type and filters afterwards, so a rejected hit there does take one. The engine returns up to 256
hits (`RayCastMaxHits`) and finds phantom and volume-detect prims when the cast asks for them
(`RayFilterFlags.phantom` / `volumedtc`, on the 5-argument entry).

The two budgets are drawn separately each heartbeat, and running out of one never refuses a cast
charged to the other. `RayCastMaxTestedHits` and `RayCastMaxHits` apply to every cast in both.

- `RayCastBudgetMs` covers the casts the module can tell are a script's. Today that is Phlox's
  `llCastRay`, the only caller of the physics scene's 4-argument `RaycastWorld`. Under Phlox, a
  script flood cannot affect rezzing or landing.
- `RayCastSimulatorBudgetMs` covers every cast on the 5-argument `RaycastWorld`: where the simulator
  places a rezzed object (`Scene.GetNewRezLocation`), where an avatar lands after login or teleport
  (`ScenePresence.MakeRootAgent`), and YEngine's `llCastRay` (`LSL_Api.llCastRay` through
  `Scene.RayCastFiltered`). The module cannot tell these apart: a script can ask for exactly the
  filter flags and hit count the simulator's own casts use. So under YEngine, script casts share
  this allowance with rezzing and landing, and a script flood there can still make a rez or a
  landing miss its surface, until the script engine marks its casts as a script's.
- The viewer camera's collision ray and the sit ray do not reach the module, which leaves those
  queries to the base physics scene.

The two budgets together bound how long ray casts can hold the region's physics lock in one
heartbeat: with the defaults, 10 ms plus the overrun of the casts in flight when each ran out.

The first ray cast in a simulator process pays one-time costs (compiling the cast code, binding the
native call, first use of the engine binding's filters): measured at 3-4 ms inside the module,
against a few microseconds for a later cast. Charged to `RayCastSimulatorBudgetMs`, that would let
the first landing casts after a start run the budget out. So when a region loads, the module makes
a few casts of its own through the same code, charged to neither budget and not counted in
`jolt capacity`, and logs `ray casts warmed up in N ms`: about 8 ms for the first region of a
process, a few hundredths of a millisecond for each later one.

What the script engines already limit before a cast reaches the module: both cap `RC_MAX_HITS`
at 16 (OpenSim's asks the physics engine for twice that), and Phlox refuses fewer than 1. Neither
limits the ray's length or how often a script casts.

`jolt capacity` shows the casts made, refused and cut short, and their time, for each budget and
for both together; the harness's `raycast-cost` scenario shows them for its casts. The metrics log's
interval line (see "Job pools") gives each interval's refused casts for each budget. The module
writes no log line for a refused cast.

**Pushes.** `llPushObject` on an avatar, and an attachment's `llApplyImpulse` on its wearer, change
the avatar's velocity by the impulse divided by its mass (1.954922 for the default avatar; see Mass below). The script engine checks the
region's and parcel's push restrictions and weakens a push from more than 17 m away before it
reaches the module (the `llPushObject` page, https://wiki.secondlife.com/wiki/LlPushObject, says a push weakens with distance). Second Life also
limits a push by the pushing object's script energy, which depends on its mass; the physics module
is not told which object pushed, so it bounds the avatar's side instead:

- Each push spends the speed it adds from the avatar's allowance, `AvatarPushMaxSpeed`, which
  refills at `AvatarPushRecovery` per second.
- Pushes never take the avatar's speed past `AvatarPushMaxSpeed`, or past the speed it already had
  (a falling avatar can be slowed by a push, not sped up).
- With the refill below gravity, repeated pushes cannot hold an avatar in the air. The ceiling is
  what one strong push can give: at the default 100 m/s an upward push of 1000 or more lifts the
  avatar to about 100^2 / (2 x 9.8) = 510 m, and a horizontal one throws it about 26 m along level
  ground (the push fades in 0.25 s). A push of 10 throws it 1.3 m, 100 about 13 m, 1000 and 10000 the
  same 26 m, as both reach the ceiling. Lower `AvatarPushMaxSpeed` to hold pushes to less.
- The angular part is ignored, as Second Life does for avatars. Push speed fades on the ground
  (0.25 s) and when flying (1 s), is kept in the air, and is taken away by what the avatar runs into.

A push on an object goes to the physics engine as an impulse and wakes the object if it sleeps;
a parked vehicle is not held still against it. It is limited by `BodyMaxLinearSpeed`.

### Job pools

All regions in a simulator share Jolt's worker threads through `JobPools` job pools (default 1).
`ThreadCount` is the total number of worker threads, split evenly across the pools, and each
region is given the pool with the fewest regions when it starts.

All regions in a process share one job pool on the joltc this module runs on, so more than one pool
needs a change in joltc itself. The module runs one pool, sized as `JobPools = 1` would size it,
logs a warning at start naming the setting, and `jolt capacity` shows the pools in use and why.

`ThreadCount = 0` (the default) is automatic: each pool gets its share of the processor count
less one, at most 4 threads and at least 1. On a 20-thread machine with one pool that is 4 threads;
with 2 pools, 4 each; on a 4-thread machine with one pool, 3. A positive `ThreadCount` is the total,
used as given. To use more threads, for example every processor but one, set `ThreadCount` to the
total you want across the pools. The startup log and `jolt capacity` give the pools,
the threads per pool, and whether the count is automatic or set.

The automatic count is per process: each region server process sizes its own pools, at most 4
threads a pool, from the processor count. A host that runs many region server
processes should set a lower `ThreadCount` in each, since small scenes run best on 1 or 2 threads
(one moving body stepped in 0.5 ms on 1 or 2 threads, against 1.0 ms on 4; figures below).

Why at most 4: a region's physics step is spread over its pool's threads, and past a few threads
handing the work out and waking the threads costs more than they save. In the harness's pool
benchmark on a 20-thread desktop processor, one step per heartbeat: a pile of 1000 moving boxes
took 13 ms a heartbeat on 4 threads and 34 ms on 19; 300 boxes 8 ms against 19 ms; one moving body
1.0 ms against 2.1 ms (0.5 ms on 1 or 2 threads). With 300 boxes sharing the process with two idle
regions, 4 pools of 4 threads stepped the busy region in 10 ms against 25 ms for one pool of 19. A pool runs one region's physics
step at a time, so a region whose pool is busy waits for it. The wait is in the heartbeat: the
region's physics still advances by the frame time, and no step is skipped.

Choosing `JobPools`:

- Set it to about the number of regions that carry physics load at the same time (moving vehicles,
  piles of physical prims), not to the total number of regions. A region with nothing moving holds
  its pool only briefly.
- With `ThreadCount` set, give each pool about 4 threads, so `JobPools` about `ThreadCount / 4`: a
  pool's threads are all that one busy region's physics gets, and more than about 4 made it slower.
  With the automatic count each pool gets at most 4 already, so more pools use more of the cores.
- The log warns when a region spent more than 20% of its frame time waiting for its pool over a
  `CapacityLogIntervalSeconds` interval, naming the key to raise.

`JobPoolFairHandoff` (default false) sets how a pool passes from one region to the next. With
`PhysicsStepRate` on, a region runs several physics steps per heartbeat and takes the pool once per
step. With the default, a plain lock that does not queue its waiters, the holder usually takes the
pool straight back for its next step, so a waiting region can wait through the rest of the holder's
heartbeat. With `true` the pool is granted first come, first served: a region that starts waiting
during another's step runs as soon as that step ends. While regions wait, each step then costs a
thread handoff, and the busy region's heartbeat takes longer by the waiting regions' steps. Like
`JobPools`, the first region to start sets it for the process.

Watching the pools:

- `jolt capacity` shows the pools, the handoff, the region's waits for its pool since start (count,
  total and longest), its steps' waits for its own region lock (held by a body change or a query on
  another thread; the step holds its pool while it waits), and the last metrics interval.
- Every 30 s the log prints a `[JOLT METRICS]` summary line and, after it, a `[JOLT METRICS] last
  <seconds>s` line with each region's figures for that interval: heartbeats, the longest heartbeat's
  physics time and the longest single physics step (both include region lock waits and leave out
  pool waits), the pool waits (count, total, longest, and which region held the pool when the
  longest began, or, if the pool was changing hands at that moment, the region that had it just
  before this one; never the region itself), the region lock waits (count, total, longest), the longest gap between two
  heartbeats' physics calls against the frame time, and the ray casts refused in the interval in
  each budget (`ray casts refused script=<n> simulator=<n>`). `jolt metrics` shows both lines.
- The harness's `--pool-bench` (see "Physics harness") measures waits and step times for a heavy
  region sharing pools with light ones, for any `JobPools` and handoff.

## Script forces on objects

What a script does to a physical prim or linkset (avatars are not covered here):

- `llSetForce`, `llSetTorque`, `llSetForceAndTorque`: the force (N) and torque (N m) act in every
  physics step until the script sets them to `ZERO_VECTOR`, and wake a sleeping object. The script
  engine turns a local vector into region axes once, when the call is made, so a local force keeps
  the direction the object faced at that moment; it does not turn with the object.
- `llApplyImpulse`: the velocity changes by the impulse divided by the mass (`llGetMass`), at once.
- `llApplyRotationalImpulse`: the angular velocity changes by the impulse divided by the inertia
  about that axis, at once.
- `llSetBuoyancy`: the object feels (1 - buoyancy) of the region's gravity, as in ubODE: 0 falls
  normally, 1 floats where it is, 2 rises at 1 g. Changing it wakes a sleeping object.
- `llSetStatus` with `STATUS_ROTATE_X`, `_Y` or `_Z` set to `FALSE`: the object cannot turn about
  that axis of its own; a torque, impulse or collision turns it only about the axes left free.
  Locking stops its turning, as ubODE does. ubODE fixes a locked axis in the region where it pointed
  when it was locked; Jolt keeps it on the object. The two agree with all three axes locked and
  with only one free.
- `llGetMass` and `llGetObjectMass`: the engine's own body mass, unscaled: the volume (m3) times
  the density times 0.01, the same number every force, impulse and speed figure here uses. A 1 m
  cube at the default density (1000) weighs 10, a 0.5 m sphere 0.65, a linkset the sum of its
  prims. A non-physical object weighs what it would weigh physical: the volume is the
  shape's physical one (the convex hull for a cut, twisted or mesh prim). A resized prim gets the
  mass of its new size.
- `llGetCenterOfMass`: in region coordinates. A physical linkset reports the centre of all its
  prims, each weighted by its own mass; called from a child prim, that prim's own centre; a lone
  prim, its own centre. For a non-physical object the simulator takes the mass-weighted mean of its
  prims' centres; the Second Life page for `llGetCenterOfMass` (https://wiki.secondlife.com/wiki/LlGetCenterOfMass) says it should return the value last computed while the
  object was physical, or the position (`llGetPos`) when there is none.
- A call that reaches a linked child prim acts on the whole linkset.
- A vehicle keeps its own forces: a set force, torque or buoyancy does nothing while the object is a
  vehicle (as in ubODE), and takes effect again when the vehicle type is removed. Impulses still act.
- A value that is not a number is refused and logged. A finite force, torque or buoyancy too large
  for any step to follow is cut to what takes the object to the engine's speed cap
  (`BodyMaxLinearSpeed`, `BodyMaxAngularSpeed`) in one step.
- Prims carry a linear and angular damping, by default 0.05 per second: a moving object loses
  about 5 percent of its speed each second on top of what forces do (see "Physics material on
  objects").

## Editing and moving physical objects

- Selected in the build tool, a physical object stops where it is and stays there: gravity, a set
  force, an impulse, a push, a set velocity and a vehicle's own motion do not move it, and an object
  that hits it or lands on it does not move it. Deselected, it carries on from rest. A selected
  linkset holds as one object, and a phantom or volume-detect object holds the same way. ubODE also
  lets other objects pass through a selected one; in Jolt a solid object stays solid, as in
  BulletSim.
- Moved or turned while it sleeps (by the build tool, `llSetPos`, `PRIM_POSITION`, or a rotation
  the script engine lets through for a physical object), a physical object wakes and carries on
  from where it was put: lifted, it falls back; turned, it settles. Not while the region is loading.
- Given a velocity while it sleeps (`llSetVelocity`, `llSetAngularVelocity`), a physical object wakes
  and moves at once, as in ubODE.

## Physical objects that leave the region

The simulator decides what happens to an object at the region's edge: `STATUS_DIE_AT_EDGE` deletes it
and `STATUS_RETURN_AT_EDGE` returns it ("if it goes off world", `llSetStatus`), and otherwise it is
handed to the neighbouring region if there is one. The physics module does what ubODE does:

- Past the edge, the object waits 0.1 to 2 m outside it, at the height it crossed and with the
  velocity it left with, while the simulator looks for a neighbour. Handed over, the neighbour gets
  that velocity.
- With no neighbour, the simulator puts it back half a metre inside the edge; it comes back 0.2 m
  higher, at rest, and falls or settles from there. A vehicle comes back with its motors off, so it
  does not drive out again until its script sets a motor.
- Below -100 m or above 100 000 m it is stopped there and the simulator is told it went out of
  bounds, which makes it non-physical (the simulator logs `went out of bounds`).
- A linkset leaves as one object. An avatar seated on it has no physics body of its own while seated;
  the simulator crosses it with the object.
- While it waits outside, and from going out of bounds until it is made non-physical, the object has
  no physics body: it costs no step time, touches nothing and sends no updates.

## Physical linksets

A physical linkset is one rigid body: the root prim's body carries every prim as part of one shape,
and no prim of it has a body of its own. These keep it one body, with the mass of all its prims:

- Linking a prim in, unlinking one, deleting one, or setting a prim's `PRIM_PHYSICS_SHAPE_TYPE` to
  `PRIM_PHYSICS_SHAPE_NONE` and back: the linkset's shape is rebuilt with the prims it now has, once,
  before the next physics step. An unlinked physical prim becomes a body of its own where it was.
- Resizing a prim, changing its shape, or moving or turning it within the linkset (the build tool's
  "Edit linked parts", `PRIM_SIZE`, `PRIM_TYPE`, `PRIM_POSITION` or `PRIM_ROTATION` on a child): the
  same rebuild, with the prim at its new size, shape or place. A move or turn smaller than 1 mm or
  1 milliradian rebuilds nothing.
- Changing a prim's density: the same rebuild, which moves the centre of mass (see "Physics material
  on objects").
- Turning the whole object non-physical: each prim gets a fixed body of its own where it is on the
  object now, wherever the linkset has moved since it was linked. Turning it physical again makes it
  one body.
- A ray cast reports the prim it hit, not the root: `llCastRay` then returns that prim's key and,
  with `RC_GET_LINK_NUM`, its link number, and the root's key only with `RC_GET_ROOT_KEY` (the `llCastRay` page, https://wiki.secondlife.com/wiki/LlCastRay, says that flag
  replaces the hit key with the root's instead of the child's). Both script
  engines look the prim up from what the physics engine reports.
- A linkset meets an avatar as one object with the mass of all its prims, by the rule for a single
  object (see "Avatars and physical objects"): thrown at an avatar it stops against it or carries it
  along by their masses, and an avatar walking into it pushes it along whole.
- Collision events: the prim of the linkset that was touched gets the event, so a script in it gets its
  own link number from `llDetectedLinkNumber`, and the simulator passes the event to the root's script
  by `llPassCollisions`. Whatever touches the linkset, a prim, another linkset or an avatar, names it by
  its root prim: `llDetectedKey` and `llDetectedName` are the object's, and a collision filter set by
  name matches the object's name. Second Life documents no rule for this; the example on its `llCollisionFilter` page (https://wiki.secondlife.com/wiki/LlCollisionFilter)
  has a filter on an object named "Post" detecting a child prim named "Object" of it. ubODE names every collider by its root likewise. A non-physical linkset is not
  one body (the simulator links only a physical one), so each of its prims is named as itself, as in
  ubODE.
- A linkset asleep on something keeps touching it when one of its prims is resized: it stays one body
  at rest, and what it rests on gets no end event. A script in that surface gets one
  `collision_start` for the linkset, naming its root, rather than one for each prim that rests on it.
- The simulator does not pass a sculpt or mesh change made through the viewer's extra parameters to
  the physics engine (`SceneObjectPart.UpdateExtraParam`), on any engine; a prim keeps its old shape
  in physics until something else rebuilds it.

## Limiting how complex a physical object may be

A physical object (a prim or a whole linkset) can be given a limit on its collision shapes, so that one object cannot
bring in hundreds of convex hulls. The limit is `[Jolt] MaxPhysicalComplexity`. `0`, the default, is no limit, and nothing
is refused; a number such as `256` (the limit Halcyon's physics engine applied) turns it on.

- How it counts, over every prim of the object that has a physics shape: a plain box, sphere or cylinder counts 1; any
  other prim counts the convex hulls its shape is made of (a hollow box 4, a torus 12, a physical mesh the hulls of its
  physics model, one hull for a mesh without one, or a prim of shape type `CONVEX`); a prim whose physics shape type is
  `PRIM_PHYSICS_SHAPE_NONE` counts 0.
- An object over the limit is not made physical. It stays as it was (non-physical), every part of it, and its owner gets
  the message "Object was too complex to be made physical: (N of M convex hulls)", where N is the object's count and M the
  limit. The message and the count are Halcyon's.
- It applies wherever an object becomes physical: the build tool's Physical box, `llSetStatus(STATUS_PHYSICS, TRUE)`,
  `llSetPrimitiveParams` with `PRIM_PHYSICS`, rezzing an object that is physical, and the region loading one that was saved
  physical (it comes up non-physical, and the flag is saved off).
- It applies while an object is physical too: a prim linked into it, or a prim's shape, size or physics shape type changed,
  that takes the count over the limit makes the whole object non-physical, with the same message. A prim that is not
  physical is never refused.
- The count is also the physics cost: `llGetObjectDetails` with `OBJECT_PHYSICS_COST` and the viewer's object cost show
  each prim's count, whatever the limit, and a prim of shape type `NONE` costs 0. It is not scaled by the prim's size.
  Setting a part to convex or none lowers it.

## Physics material on objects

`llSetPhysicsMaterial` (and `PRIM_PHYSICS_MATERIAL`) and `PRIM_MATERIAL` on a prim, as the Second
Life wiki documents them (`llSetPhysicsMaterial`, https://wiki.secondlife.com/wiki/LlSetPhysicsMaterial):

- Friction (0 to 255), restitution (0 to 1), the gravity multiplier (-1 to 28) and density (1 to
  22587 kg/m^3) each act on the object; a value outside its range is clamped to it, and one that is
  not a number is refused and logged. A change wakes a sleeping object.
- `PRIM_MATERIAL` sets the material's friction and restitution. `[Jolt] BasicForcesBehaviour` picks
  the table: `InWorldz` (the default, InWorldz's own presets; Second Life's behaviour with InWorldz's
  fixes) or `SecondLife` (the wiki's). Wiki table: stone 0.8 / 0.4, metal 0.3 / 0.4, glass 0.2 / 0.7,
  wood 0.6 / 0.5, flesh 0.9 / 0.3, plastic 0.4 / 0.7, rubber 0.9 / 0.9, light 0.6 / 0.5. The InWorldz
  table differs from it in one row: stone is 0.5 / 0.05 (a road acts mostly like the ground). It
  maps light to wood, whose figures are the wiki's light ones. PhysX has a static and a dynamic friction
  where Jolt has one, so the static one is used. A new prim gets the friction and restitution the simulator keeps for it (wood by
  default). The simulator's own table gives rubber a restitution of 0.95 and light 0 / 0, so a
  rubber or light prim rezzed or loaded has those until a script sets its material again.
- Two touching surfaces combine by `[Jolt] FrictionCombine` and `RestitutionCombine` (the wiki gives
  no rule): `Average` (the default, what PhysX does when no mode is set), `GeometricMean` (Jolt's own
  rule for friction), `Multiply` (ubODE's rule for restitution), `Min` or `Max` (Jolt's own rule for
  restitution). A box sliding on a surface of the same friction `f` feels `f` under every rule. A
  bounce needs a closing speed of at least 1 m/s (Jolt's `MinVelocityForRestitution`). The terrain
  has friction 0.4 and restitution 0.05 (`TerrainFriction`, `TerrainRestitution`; InWorldz's ground),
  so a ball of restitution 0.9 dropped on it rises to 0.475 squared, 23 percent, of its drop.
- A dropped object rebounds to its restitution squared times the height it fell (less its damping),
  within 5 percent, for restitution 0.1 to 0.9, drops of 0.5 to 10 m and step rates of 11 to 90 Hz.
  Jolt bounces a body from wherever its step finds it, up to one step's travel above or into the
  surface, so on its own the height would be off by that much (a 1 m box dropped 2 m with restitution
  0.3 came back 18 percent low to 11 percent high at 45 Hz, by where the step fell). The module hands
  Jolt, for each impact, the restitution that sends the body as high as a bounce from the surface would.
  A rebound lower than Jolt's speculative contact distance (2 cm) can still come out anywhere from
  none to that distance, because the step may find the body already above the top of its bounce.
- The gravity multiplier scales the object's gravity: the object falls at (1 - buoyancy) times the
  multiplier times the region's gravity, as in ubODE. 0 floats, 2 falls at twice the rate.
- Density sets the mass: volume (m3) times density times 0.01, the engine's own body mass, which is what `llGetMass` reads.
- In a linkset each prim keeps its own friction, restitution and density (the Physics Material Settings test page,
  https://wiki.secondlife.com/wiki/Physics_Material_Settings_test, answers yes to whether prims in a linked set can have different
  settings): a
  contact uses the struck prim's friction and restitution, and the mass is the sum of each prim's
  volume times its own density. The centre of mass the engine turns the linkset about, and its
  inertia, follow each prim's density too, so a heavy prim at one end pulls the centre of mass
  toward it, and `llGetCenterOfMass` reports that same point. The root prim's gravity multiplier
  applies to the whole linkset, as in ubODE.
- A vehicle keeps each part's own friction and restitution. While it slides in contact it slows by that
  friction plus its own friction timescales (v(t) = (v0 + mu g T) e^(-t/T) - mu g T along an axis of
  timescale T); in the air only the timescales act. `[Jolt] VehicleContactFriction`, when set, replaces
  every part's friction. A sled's parts all take `SledContactFriction` (0 by default) and keep their own
  restitution, density and gravity multiplier; when the type changes to another or to none, each part's
  material friction comes back. Under `VehicleBehaviour = InWorldz` a sled's contacts also combine friction
  with the smaller of the two values whatever `FrictionCombine` says, so a part at 0 stays at 0 on a prim or
  the land (an average with a wood prim's 0.6 is 0.3, above tan 15 degrees, and held a sled still on a 15
  degree ramp); under SecondLife the combine rule is unchanged. A vehicle is damped as any physical prim is, and ignores the gravity
  multiplier.

Damping is two `[Jolt]` keys:

| Key | Default | What it sets |
|---|---|---|
| `PrimLinearDamping`, `PrimAngularDamping` | 0.02, 0.05 | Linear and angular damping of a physical prim, vehicles included, per second (0 to 100): a moving object loses about that share of its speed and spin each second. The same at every step rate. These are InWorldz's figures for every physical object. ubODE's, as a rate, are 0.1001 and 0.0250 (dBodySetDamping .002 and .0005 per 0.020 s step). |

## Mesh prims

The simulator does not load a mesh asset for the physics engine, so the module fetches it. A mesh prim
whose shape holds no mesh bytes, and whose mesh the mesher has not already made at that size, is created
as its bounding box and asks the scene's request-asset delegate for the asset (the way the ubODE and
BulletSim modules do). When the asset arrives the shape is cooked from it and put on the same body: the
body keeps its position, rotation, velocities, linkset and its physical, phantom and volume-detect state,
and what rests on a fixed prim is woken to meet the new surface.
A sculpted prim (a sculpt map instead of a mesh) is fetched the same way: its map is a JPEG 2000 texture that the
mesher decodes, unless it has already decoded and cached that map.

- Prims that wait for the same asset share one request. The bytes are put on a copy of the prim's shape for
  the cook only, so the part's own shape never holds them.
- A prim removed before the asset arrives is not touched when it arrives, and a prim whose shape has
  changed to another mesh meanwhile is left alone.
- An asset that is missing, empty or unreadable leaves its prims bounding boxes and is logged once per
  asset. Another prim that needs it asks again only after ten minutes, in case the asset service was down.
- A non-physical mesh under 0.05 m on every side is a box and asks for no asset. Second Life says nothing
  about a size limit; the figure is Halcyon's (`ShapeDeterminer.CanUseLowDetailShape`). A physical mesh is
  never boxed for its size. (The mesher's own rule, a box for any prim under 0.2 m on every side, still
  applies to a prim that is not a mesh.)
- A physical mesh whose shape type is `PRIM` collides as the hull list its asset carries: one convex hull per
  entry, at the prim's scale, as Second Life does (for `PRIM_PHYSICS_SHAPE_TYPE` the `PRIM` type takes the physics shape from
  the prim's visible shape, https://wiki.secondlife.com/wiki/PRIM_PHYSICS_SHAPE_TYPE; the viewer's upload writes the hulls of its convex decomposition).
  An open boat or a gateway built that way keeps its inside and its opening. It follows the prim through a resize,
  a switch between physical and not, and a change of shape type, as a lone prim and as a part of a linkset.
- A physical mesh of shape type `CONVEX`, and a physical mesh whose asset has no hull list, is one convex hull of
  all its points, as before.
- A hull of fewer than four points, or one Jolt cannot build, is left out. If none is left, or the list has more
  than 256 hulls or a hull has more than 256 points (the limits of the viewer's upload), the prim is one hull of
  its points. Each case is logged once per asset.
- Mass is the volume of the hulls times density, so a hollow physical mesh weighs what its hulls hold, not what
  the hull of all its points would (a trough of three boxes: 3.84 m3, not the 12 m3 of the box round them).
- Prims of one mesh asset, one physical state and one shape type share one cooked shape, however many there are
  and at whatever size: the shape is cooked once, at the size of the first prim, and a prim of another size
  (a resize, or another copy) takes a scaled shape over it. Nothing is fetched, decoded or cooked again. The scaled
  shape weighs, balances and collides as a fresh cook at that size does, for a hull list, a triangle mesh and one
  hull, scaled the same way on every axis or not (`MeshResizeScaledShapeTests`: the same mass, centre of mass and
  rest height to the figures printed, and the same passage through a gateway). A size the size rules treat
  differently is cooked on its own: a non-physical mesh under 0.05 m on every side is a box, and the mesher makes no
  side under 0.01 m. The cooked shape is freed when the last size of it goes.
- A part of a linkset shares a shape the same way unless its density differs from the root's. The linkset writes each
  part's density, relative to the root's, into the part's shape, and a shape that other prims use keeps the engine's
  default, so such a part takes a shape of its own (a cook from the mesh the mesher kept, without a fetch); a part
  of the root's density needs none written and takes the shared or scaled shape.

**Concave physical prims (hollow, cut, twisted, torus, tube, ring, hollow sphere)**

- A physical prim of shape type `PRIM` that is made from its own profile and path (not a mesh, not a sculpt) and is not
  convex collides as a compound of convex cells: one hull for each convex piece of its profile over each span of its path,
  cut from the rings the mesher builds the prim from, at most 64 cells. A hollow box is four, a torus twelve. A hollow
  prim keeps its cavity, so a ball can rest inside a hollow box. `CONVEX`, `NONE`, the plain box, sphere and cylinder,
  sculpts, meshes and every non-physical prim are cooked as before. A prim that is one cell, or whose cells cannot all be
  built, is one hull of its points, as before; the second case is logged once.
- It weighs the real volume of its shape, the volume of the mesher's closed mesh, times its density, and `llGetMass`, the
  body and a welded linkset's mass read the same number. The cells give the collision shape, the centre of mass and how the
  mass is spread; their inertia is scaled to the real mass. A thin-walled prim weighs what its wall is, not what its cells
  hold (a hollow 0.95 torus: the cells are 3.7 times the real volume). Where the mesh volume cannot be trusted the prim
  weighs the volume of its cells instead, and each reason is logged once: the mesh encloses no volume or winds inside out,
  its surface has a hole, or two of its triangles pass through each other (a heavily twisted prim does this), or its
  volume is above the cells' (they hold the whole solid). `CONVEX` weighs its hull, as before.
- Prims with the same shape parameters share one cooked compound, cooked once at unit size; a prim of another size takes a
  scaled shape over it. Any change to a parameter is another entry, and an entry is freed with its last prim. A part of a
  linkset that weighs less than its cells' volume takes a compound of its own, made from the shared cells' points (no new
  split), because the linkset writes each part's weight into its shape.

| Key | Default | What it sets |
|---|---|---|
| `BodyEnhancedEdgeRemoval` | `false` | `true`: Jolt's enhanced internal edge removal for every body that moves under physics (a physical prim, a vehicle): the contacts with the shared edges inside a triangle mesh or a heightfield that can push a body off its path are removed. Costs more per contact with a mesh or the terrain. Off, nothing changes. See "Seams in triangle meshes and terrain". |
| `SpeculativeContactDistance` | `0.02` | How near two shapes must come, in metres, for the engine to start stopping them closing (the default is Jolt's own value). A box that rests a few millimetres into a prim and slides across the seam to the next prim sees that prim's end face as a wall inside this distance and can be stopped and tipped; 0.0025 and less remove that, 0.005 and more do not. Every value under 0.02 also changes how other contacts settle, so it is not the default. `0` makes contacts begin only on overlap. See "Seams between prims". |
| `SleepVelocityThreshold` | `0.03` | The speed in metres per second under which a body counts as still for sleeping (the default is Jolt's own value). A body, and everything resting on it, falls asleep after half a second below it. A tall stack of boxes sways a little faster than 0.03 when `PhysicsStepRate` is 0, and whether it falls asleep is then a matter of float rounding, which differs between CPUs; 0.05 puts a ten-box stack to sleep on every CPU tested. A higher value also lets a slowly sliding or turning body fall asleep. |
| `AvatarEnhancedEdgeRemoval` | `false` | `true`: the same for avatars. Off, nothing changes. |
| `TwoSidedMeshCollision` | `false` | `true`: a non-physical mesh prim (a triangle mesh) is met from both sides by a moving body. Off, a physical box, ball or vehicle that reaches a one-sided wall or floor from behind passes through it, because a simulated contact ignores back faces and the stock native library has no switch for that. On, each mesh is cooked with a reversed copy of every triangle: twice the triangles in memory and cook time, and a ray cast that reaches such a mesh from behind now hits it (without the setting it never does, with or without the back-face-cull flag). Avatars already meet both sides. Off, nothing changes. |
| `HonourConvexMesh` | `false` | `true`: a non-physical mesh whose `PRIM_PHYSICS_SHAPE_TYPE` is `PRIM_PHYSICS_SHAPE_CONVEX` collides as one convex hull of its points, not its triangle mesh. The simulator gives every mesh that shape type unless the upload sets another, so with this on a hollow mesh without a physics model becomes solid. Off, nothing changes. |
| `MaxPhysicalComplexity` | `0` | The most collision shapes one physical object may have: a plain box, sphere or cylinder counts 1, any other prim its convex hulls, a prim of shape type NONE 0. An object over it is not made physical and its owner is told "Object was too complex to be made physical: (N of M convex hulls)". `0` is no limit. See "Limiting how complex a physical object may be". |

## Prims that keyframed motion moves

`llSetKeyframedMotion` works on non-physical objects only, and its wiki page has avatars standing on such an object while it
moves ("... when standing on a moving platform"). The simulator moves the object with plain position and rotation sets, about
every 50 ms, from the motion's own timer. A fixed body that is set to a new place has no velocity, so an avatar or a physical
object on it stayed where it was and the platform left from under it: a rider on a platform moved 6 m sideways at 1 or 3 m/s fell
the platform's 3 m in every case.

The simulator tells the root prim's actor while a motion plays (`PhysicsActor.MovedByKeyframedMotion`: on when the motion
starts, off when it stops, is paused or has played out, or the object is selected; an actor made while it plays is told too).
For that time the prim's body is a kinematic one on its own layer, `MovingStatic`, which collides exactly as a fixed prim does
(ray casts and sensors find it as a fixed prim) but sits in the moving broad phase, so each move does not touch the large tree of
fixed prims. Each pose set is only recorded; once per heartbeat, before the step, the body is moved to the last pose over the
time that step covers (the whole burst, at a `PhysicsStepRate`), so it carries the linear and angular velocity of the motion.
An avatar takes the velocity of the body under its feet and a physical object is carried by friction.

- A move longer than 4 m in one heartbeat (or faster than 40 m/s over a longer one) is a jump, not a motion: the body is put
  there with no velocity, so nothing is flung, and what rested on it is woken. Keyframed motion at 3 m/s moves 0.27 m a heartbeat,
  a lift at 20 m/s 1.8 m. While keyframed motion moves the prim, the 40 m/s is measured over the heartbeat plus two ticks of the
  motion's 50 ms timer (a heartbeat holds one pose more than its length divides into, and a late timer call moves up to three
  ticks at once): a heartbeat that finds three poses of a 30 m/s motion has 4.5 m to carry and carries it.
- A heartbeat in which no new pose arrived stops the body at the last pose: a kinematic body keeps the velocity it was given and
  would otherwise carry on past where the motion stopped.
- When the motion ends the body is made a fixed one again where the object is, and what rests on it is woken to settle. A
  region that never plays a keyframed motion has no kinematic body. The simulator sets the last pose and turns the hint off at the
  motion's next tick, so a heartbeat can find both: that heartbeat still moves the body to the last pose, and the body is made a
  fixed one at the start of the next, so an avatar standing on the platform is carried through that heartbeat's last move
  instead of being left behind by it (made a fixed body at once, the platform would jump to the last pose in one go: 0.46 m
  behind at 10 m/s, 1.06 m at 22, 1.46 m at 30; 0.21 m at 5).
- An avatar takes the platform's velocity in the step the motion starts. The platform's body is made again when the motion
  starts, so an avatar whose ground was that body finds its ground again before its step; without that, the platform would
  leave it behind for one step (after ten heartbeats at 16.5 m/s it would be 1.5 m from the platform's middle at one step per
  heartbeat and 0.4 m at 45 Hz). With it the same run ends 0.00 m from the middle.
- A platform that turns carries a rider round: 2 m from the centre of a platform turned 90 degrees at 30 degrees a second, a
  cube ended at 89.2 degrees and an avatar at 88.5 degrees, both at their radius (one step per heartbeat; at 45 Hz both about 89.5).
- A linkset moves as one body: the parts' own sets, which follow the root's, change nothing, and the root's body is not made again.
- **Fast starts.** An avatar standing on a platform that keyframed motion starts from rest at 2 to 30 m/s keeps its place on it
  through the first second, at one step per heartbeat and at 45 Hz, with heartbeats that find one, two or three of the 50 ms poses,
  standing in the middle or 0.1 m from either edge, on a 0.5 m or a 0.1 m platform. A physical object on such a platform is not
  carried by friction at these speeds: it slides back, and a cube that slides into an avatar slows it as any body it meets does
  (at one step per heartbeat, an avatar 1.5 m behind a 125 kg cube was left 0.8 m behind at 10 m/s and 1.4 m at 22 m/s in the harness), so an
  avatar with a loose object in front of it on a fast platform can be left behind by that object, not by the platform.
- **A platform that crosses into a neighbouring region.** The simulator hands a seated avatar over with the object and nothing
  else, so what stands on a keyframed platform that crosses is left in the first region: its body goes with the object, and the
  rider falls to the ground there (a stand-in for the crossing, one region: the platform removed once its middle is outside, an
  avatar and a cube both end on the ground). A crossing that fails puts the object back half a metre inside the edge, stopped:
  a rider stays on it and is carried again when the motion runs again (the avatar slid 0.6 m on the jump back, the cube 0.7 m).
  A crossing between two real regions has not been measured.

Not changed: a non-physical prim moved any other way (`llSetPos`, `llSetRegionPos`, the build tool) stays a fixed body that is set
to each new place unless `MovedPrimsCarryRiders` is on (next section), as is a keyframed motion that the actor was not told of;
physical objects and vehicles, phantom and volume-detect prims, and a selected object.

| Key | Default | What it sets |
|---|---|---|
| `KeyframedMotionCarriesRiders` | `true` | `true`: what stands on a prim that keyframed motion is moving is carried with it, as above. `false`: the prim stays a fixed body for the whole motion. |

The harness scenarios `kin-box-*` and `kin-avatar-*` (a 6 x 6 x 0.5 m platform 3 m above the ground, a cube or an avatar on it,
moved 6 m sideways or up at 1 or 3 m/s, by a set every 50 ms or every 0.2 s) and `kin-box-turn`, `kin-avatar-turn` give the figures.
A keyframed run, the platform told, against the same run with it not told (which is what the module did before): sideways, a cube
and an avatar stay on (slide at most 0.15 m, the cube at most 0.04 m and trailing at most 1.0 m at 3 m/s) where they fell 3 m
before; moving up, nothing sank, and riders end at most 0.4 m above their resting height when a fast lift stops. The same
platform moved by plain sets gave the same figures before and after.

## Stepped platforms

Keyframed motion sets its object's pose about every 50 ms, and a script that moves a floor with `llSetLinkPrimitiveParamsFast` does it
10 or 20 times a second. A region's heartbeat is about 91 ms, so one heartbeat finds one set of a 20 Hz motion, the next two, and a slow
one three. A body moved to the last pose each heartbeat would change speed by a half or more from one heartbeat to the next, and a
cube resting on it would be thrown. While the poses come about every heartbeat the body is moved at the mean speed of the last four
heartbeats, kept within 4 cm of the last pose, and carries on at that speed for one heartbeat in which no pose comes before it is stopped
at the last pose. Harness figures, a platform going up at 2 m/s in steps of 0.1 m every 50 ms (regular heartbeats, and heartbeats that
find 1 to 3 sets), how far a rider moves about its place while the platform is at speed: a cube 0.045 to 0.060 m (0.09 to 0.11 m
before), an avatar 0.043 to 0.046 m (0 to 0.08 m before). At the start and the stop of a motion the speed changes in one heartbeat, as
the script made it do, and a cube rises up to 0.3 m on a stop from 2 m/s (0.35 to 0.52 m with uneven heartbeats before).

## Prims moved by plain sets

Second Life does not carry an avatar over a non-physical prim that `llSetPos` moves (the wiki pages for `llSetPos` and
`llSetRegionPos` say nothing of riders), so by default such a prim stays a fixed body that is set to each new place and what
stands on it is left behind, exactly as before. `[Jolt] MovedPrimsCarryRiders = true` carries riders over a **series** of such sets
(`llSetPos`, `llSetRegionPos`, `llSetPrimitiveParams`, dragging in the build tool) with the same kinematic body keyframed motion
uses.

- **A series** is two sets that fall in different heartbeats, at most 0.25 s apart plus one heartbeat, the second no further than 4 m
  and no more than a quarter turn from the one before. The sets are stamped with the region's simulated time, which counts whole
  heartbeats, so a call that waits 0.2 s (`llSetPos`) is seen as two or three heartbeats (0.18 or 0.27 s at 11 Hz) and the
  extra heartbeat keeps both inside the rule. A single set, a set in the same heartbeat as the one before, a longer pause or a
  longer move is a plain set.
- **The body becomes kinematic at the second set**, put at the pose before it and moved to the set's pose by the next step, so
  the second set already carries. A series whose sets come less often than every heartbeat and a half (`llSetPos`) glides to each new pose over
  the time the sets have been apart (a running mean), so what stands on it feels the velocity the sets add up to and not a jolt
  and a stop for each step; the body trails the object by up to one step of the series (0.2 m at 1 m/s, 0.6 m at 3 m/s). A series
  whose sets come about every heartbeat (a script that calls `llSetLinkPrimitiveParamsFast` 10 or 20 times a second) is followed as
  keyframed motion is, below.
- **The quiet time is 0.5 s** with no set. It is above the longest gap a series allows, so a series is never cut between two of
  its sets, and short enough that the body is fixed again soon after the last set, where the object was left, and what rests
  on it is woken. The body is at rest at the last pose from the heartbeat after the glide ends.
- A move further than 4 m, or more than a quarter turn, during a series is a jump: the pose is taken with no velocity.
- **The first set carries too when something rests on the prim.** A set that is not part of a series begins one when a physical
  prim or an avatar stands on or against the prim (an overlap test, one box query, run only for such a set); with nothing on it, the
  prim stays a fixed body. So a single `llSetPos` of a platform with a rider on it moves the rider with it. The body takes that step
  at no more than 3 m/s, and at least a heartbeat (a step of 0.6 m over 0.2 s), and is fixed again after the quiet time. With the setting
  off nothing changes: a platform lifted 0.6 m by each set throws a cube standing on it.
- A prim that keyframed motion moves is not affected: the hint decides, whatever this key says, and a keyframed prim's sets every
  50 ms are not taken for a series. Physical prims and phantom and volume-detect prims are not moved this way; a linkset root is, and
  keeps its body.
- **A child prim moved on its own** (`llSetLinkPrimitiveParams` on a child, the build tool on one child of a linkset whose root is a fixed
  prim) is part of its root's compound, so its move rebuilds the compound and cannot give its riders a velocity. With the setting on,
  what stands on the child (physical prims within a few centimetres of it, and avatars whose feet are on its top) takes the child's
  step with each set: it is moved by the same move and turn, keeping its velocity. A jump (over 4 m or a quarter turn) is not carried.
  A root that is physical, or that keyframed motion moves (its body carries the whole linkset), is not affected. Harness, a child moved
  3 m east or up in sets of 0.1 m, two a heartbeat: a cube or an avatar standing on it ends at its place on the child (0.00 m off);
  with the setting off it is left 3 m behind (a cube, an avatar sideways).

| Key | Default | What it sets |
|---|---|---|
| `MovedPrimsCarryRiders` | `false` | `true`: riders on a prim moved by a series of plain sets are carried, as above. `false`: such a prim is a fixed body set to each new place. |

With the setting on, the harness platform (`kin-*-setpos`, a set every 0.2 s) carries a cube and an avatar 6 m sideways at 1 and
3 m/s (they stayed on in every run; an avatar slid at most 0.6 m, a cube at most 0.4 m) where, with it off, they fall 3 m, and moving up
nothing sank more than 0.45 m, the cube lifted 0.6 m by each set included.

## Seams in triangle meshes and terrain

A triangle mesh (a non-physical mesh prim, a floor or road built of one) and the terrain's heightfield are made of
triangles that meet along shared edges. A body crossing an edge can touch it as if it were a wall and be pushed off its
path: a vertical spike in its speed. Jolt can remove those contacts ("enhanced internal edge removal",
`BodyCreationSettings::mEnhancedInternalEdgeRemoval`, `CharacterVirtualSettings::mEnhancedInternalEdgeRemoval`; Jolt's own comment says it spends extra
effort removing ghost contacts, which is more costly but lets bodies glide more smoothly across a mesh whose edges are convex). Neither Second Life nor Halcyon documents anything of the kind, so it is off:

- `[Jolt] BodyEnhancedEdgeRemoval = true` turns it on for every body that moves under physics, when it is created (a prim that
  becomes physical gets a new body, which has it).
- `[Jolt] AvatarEnhancedEdgeRemoval = true` turns it on for avatars.

The harness scenarios `seam-ball`, `seam-sled` and `seam-walker` cross a flat floor of 12800 triangles (a mesh prim),
the `seam-ripple-*` ones a floor that ripples (4 cm every 3 m), the `seam-terrain-*` ones rolling terrain. A hitch is a
sample after the first 1.5 s whose vertical speed differs by more than 0.1 m/s from what the two samples before it
predict. `EnhancedEdgeRemovalTests` prints the count with each setting off and on, at one physics step per heartbeat and
at 45 Hz (lines "SEAM ..."); `SeamScenarios.HitchCount` is the figure.

Measured (Release, 5 s runs, hitches with the setting off, then on, at one physics step per heartbeat / at 45 Hz): a ball on
the flat mesh floor 15 then 0 / 19 then 0 (it also keeps its speed: it ends 128.6 m along instead of 122.3); on the rolling
terrain 3 then 0 / 3 then 0; a sled on the rippled floor stops dead at its start with the setting off and slides 6 m with
it on; a sled on the flat floor, and an avatar on every ground, show no hitch either way. The ball on the rippled floor
counts about the same either way (20 then 21): the ripple itself bends its vertical speed by more than the threshold.
With `BodyEnhancedEdgeRemoval` on, 11 of the 13 recorded harness baselines move (the car, crash and ramp rows: the terrain is
a heightfield, so every body on the ground meets the removal); with `AvatarEnhancedEdgeRemoval` on none does. Both stay
off by default for that reason and for the extra cost per contact, which was not measured.


## Seams between prims

Two prims that touch (a floor made of boxes, a road of slabs) are two separate bodies, and Jolt's enhanced internal edge
removal does not join them: it removes contacts with the shared edges inside one mesh or heightfield. A box that rests a few
millimetres into the top of one prim and slides on to the next prim sees the next prim's end face, within the speculative
contact distance, as a wall in its path. The contact stops the box's leading edge and tips it. In a test (a 1 m cube pushed
at 5 m/s across a 10 m slab and the next one, flush, with a 1 mm gap, with a 1 mm step or with the surface bent 0.1 degree at
the seam, four starts nudged by up to 1.1 mm, at 32, 45 and 64 Hz with 1 to 4 collision steps) the cube turned over about the
y axis in 28 of the 40 seam cells with the engine's own distance, 0.02, and in 16, 1 and 0 with 0.01, 0.005 and 0.0025 (and
with 0.001 and 0). A vehicle driven across the same seams did the same (without the spike cut below: 28, 16, 2 and 0 of 40).

`[Jolt] SpeculativeContactDistance` is nevertheless left at 0.02. Every smaller value changes how other contacts settle, and
tests of existing behaviour fail with it: with 0.0025 ten of them (the sphere dropped into a physical bowl ends 5.6 cm off where the test allows 5; a ring dropped over a standing cylinder rests lower than the test allows; a cube in a tipped hollow box slides out 4 cm where the test needs 30; the tests of a physical box falling through a volume-detect prim and through a phantom prim count 3 where they expect 2; a sled sliding down a 15 degree slope is 2.8 % slower at one step rate; an airplane's steady speed at 11 Hz is 3.4 % lower; a vehicle with a seated avatar driven off a region edge is still moving at the end; the 90 degree mouselook turn settles later; and a fast linkset is through an avatar it should push; with `BodyEnhancedEdgeRemoval` on as well, the sled, the mouselook turn and the linkset pass again and the rest still fail, which points to catches on the terrain's triangle edges for those three); with 0.005 five; with 0.01 six; with 0, ten. Its other costs, measured on the same seam and landing
tests: a box dropped on the terrain at 20 m/s dips a little deeper before it is stopped (0.28 m under the surface at 0.005
and below, 0.13 m at 0.01 and above; it never passes through the terrain or a 5 cm plate at 20 to 200 m/s at any value), and 0
leaves a box at gravity multiplier 28 still bouncing at 5.98 m/s 0.3 s after it lands on the terrain at 32 Hz with 3 collision
steps, which no other value does. An operator whose regions have floors or roads built of many prims can set 0.0025 and accept
those differences.

The tests are `PhysxSpikeTests` (seams, rough ground, walls, tunnelling, overlap, gravity multiplier) and
`PhysxVoidEdgeTests` (an object driven into the edge of a region with no neighbour).

Under `[Jolt] VehicleBehaviour = InWorldz` a vehicle's step also cuts the spikes that Halcyon's vehicle code cut
(`VehicleDynamics.MitigatePhysxSpiking`): an angular acceleration about the vehicle's y axis over 100 rad/s^2 takes off the
angular velocity about x and y, an upward acceleration over 50 m/s^2 takes off the vertical velocity and half the sideways
one. Both are measured over the engine's change in the last 15.6 ms, whatever the step rate, and only over what the engine
did: the velocity changes the vehicle's own step made are taken out, so the cut acts on contacts (a hard landing, rough
ground, an edge) and never in free flight. `[Jolt] VehicleSpikeMitigation` sets it: left out, it follows the behaviour (on
under `InWorldz`, off under `SecondLife`); `true` or `false` forces it. On rough ground at 15 m/s it lowers the vehicle's
worst upward and angular accelerations and keeps it upright (a car type driven over the test ground tipped past 90 degrees
in 4 of 10 step-rate cells without the cut under `SecondLife` behaviour, and in none with it), and a vehicle that lands hard
does not bounce. It does not stop a body that the engine's position solver turns over, which is what a seam does.

## Mass: what a script reads and where it comes from

- `llGetMass` and `llGetObjectMass` return the engine's own body mass, unscaled: the same number the engine uses in every
  force, impulse and speed figure in this document (an impulse of 10 on a mass of 2 changes the speed by 5). An avatar
  reads its mass from its shape and size: 1.954922 for the default avatar (the figure `llGetObjectMass` gave for an avatar
  before it went by shape, https://wiki.secondlife.com/wiki/LlGetObjectMass), scaled by the volume of the avatar's capsule
  as Halcyon scales a density by it. `llGetMassMKS` is 100 times `llGetMass`.
- A prim's density setting is `SceneObjectPart.Density` (default 1000, accepted from 1 to 22587; `PRIM_PHYSICS_MATERIAL`
  and `llSetPhysicsMaterial` set it as given). The module multiplies it by 0.01 to get kg/m3, so a prim's mass is its
  volume (m3) x density x 0.01, and a linkset's is the sum over its prims, each at its own density.
- Worked example, a 0.6 m cube (volume 0.216 m3): at the default density 1000 it weighs 0.216 x 10 = 2.16; at density
  15432 it weighs 0.216 x 154.32 = 33.33, so three such cubes linked weigh 100. To make those three weigh 1, set density
  154.32. Reading the number as "kg / 100" and choosing the density from that gives a mass 100 times too small.

## Move to target and hover on objects

`llMoveToTarget` and `llSetHoverHeight` on a physical prim or linkset (avatars are not covered here).
The Second Life pages for `llMoveToTarget` and `llSetHoverHeight` (https://wiki.secondlife.com/wiki/LlMoveToTarget, https://wiki.secondlife.com/wiki/LlSetHoverHeight) say each brings the object to its target with critical damping over tau seconds. In Jolt each is a
critically damped spring with tau as its timescale: from rest, the distance left after t seconds is
d0 (1 + t / tau) e^(-t / tau), so it is about a quarter of the way in after tau, 91 percent of the
way after 4 tau, and it does not pass the target. The motion is the same at every
`PhysicsStepRate`, the engine's damping included.

- `llMoveToTarget(target, tau)`: the object goes to the target and holds there, until
  `llStopMoveToTarget`, which lets it fall from where it was held. A target 65 m or more away does
  nothing (the `llMoveToTarget` page says the distance must be less than 65, or no movement occurs). A tau of 0 or less moves nothing (the
  simulator also stops an earlier target then); a tau under 2/45 s acts as 2/45 s, the smallest tau the wiki says functions.
- `llSetHoverHeight(height, water, tau)`: the object holds its centre `height` above the ground, or
  with `water` TRUE above the ground or the water, whichever is higher. It follows the ground as it
  moves, so it keeps its height over a slope, and it pulls the object down if it is above the height.
  `llStopHover` or a height of 0 lets it go. Without volume detect it does not go under the ground;
  a negative height leaves it on the ground. Heights are cut to 4096 m, the limit the `llSetHoverHeight` page gives. A tau of 0
  or less is refused.
- While either acts, gravity does not, as in ubODE, so buoyancy changes nothing. A set force still
  acts, and holds the object `F tau^2 / m` from its target, where the spring balances it. A set
  torque still turns it. With both asked for, move to target acts and hover waits.
- Each call wakes a sleeping object, and a call that reaches a linked child prim acts on the whole
  linkset. The object may sleep once it has settled where it is held.
- On a non-physical object neither acts, but the request is kept and acts once the object is
  physical (the `llMoveToTarget` page says a call appears to stay in effect even once physics is switched off).
- A vehicle keeps its own hover and motion: neither acts while the object is a vehicle.
- Selected in the build tool, the object is held still and neither acts. The request is kept:
  deselected, the object goes on to its target from rest where it was held, or falls if the script
  stopped it meanwhile.
- `llGroundRepel` reaches the physics engine as the same request as `llSetHoverHeight`, so in Jolt
  it acts as `llSetHoverHeight` does: it also pulls an object down to the height, which Second Life's
  `llGroundRepel` does not.
- Under Phlox, `water` TRUE asks for a height above the water level alone, also over land that is
  higher than the water.

## Rotation target on objects

The physics actor's rotation target (`APIDTarget`, `APIDStrength`, `APIDDamping`, `APIDActive`) turns a physical prim or
linkset to a rotation. The Jolt scene answers true to `PhysicsScene.SupportsRotationTarget()`, so the simulator hands the
target of `llRotLookAt`, `llLookAt` and the rotate-drag (Ctrl-Shift drag) to the root prim's actor, and does not run its own
look-at controller for that object (which sets the angular velocity once per frame and leaves a body resting on the ground
several degrees short). The rotate-drag gives the viewer's rotation as the target on every update, with strength and damping
0.1 s; ending the drag or a look-at switches the target off. No setting is needed.

The Second Life wiki gives no formula (https://wiki.secondlife.com/wiki/LlRotLookAt, https://wiki.secondlife.com/wiki/LlLookAt). `llRotLookAt`: the object turns smoothly, with strength setting how much it resists and damping the number of seconds to critically damp in; damping has an upper limit of 1.0, and the smallest strength that works appears to be 0.0445.
`llLookAt`: strength is an inverse value, so lower values turn the object faster. In Jolt the target is a
critically damped spring on the rotation: with r the rotation vector from the target to the object (the short way round),
r'' = -r / T^2 - 2 r' / T, so from rest the angle left after t seconds is a0 (1 + t / T) e^(-t / T) and the object does not
pass its target. T is the slower of strength and damping (damping cut to 1): the wiki gives both as times and does not say
how they combine, so that is a choice. A strength under 0.0445, or one that is not a number, does nothing (the request is
kept). Each physics step the engine sets the body's angular velocity to the one that takes it, through the engine's angular
damping, from where it is to where the spring is at the end of the step, so:

- the motion is the same at every `PhysicsStepRate` and for any mass or size (a velocity does not depend on inertia);
- an object resting on the ground reaches the target as well as one in the air, because what contact friction took from one
  step is asked for again in the next (a 90 degree turn on the ground ends within 0.1 degrees of the target in the figures);
- the target may be moved many times a second: the spring carries on across a new target, and trails it by about twice T at
  the target's speed (T of 0.1 s: 32 degrees behind a 90 degree turn made in 1 s, 6 degrees behind one made in 6 s, the same at
  15, 30 and 60 updates a second);
- a linkset turns as one body, and a call that reaches a child prim is the root's.

Switching it off (`APIDActive` false) sets the angular velocity to zero before the next step, as the simulator does when it stops
a look-at; the rotation reached is kept. Left as it was the object would go on turning at the spring's speed, since the body's
own damping takes about a minute. A body that has settled at its target may sleep; a new target, or any change to it, wakes it.

Together with other things a script sets:

- a move to target and a rotation target act together (the first sets the linear velocity, the second the angular);
- a set force acts as it does alone and the object still reaches its target; a set torque is held against and the object
  still reaches its target;
- gravity and buoyancy are not touched (a move to target turns gravity off while it acts; this does not): an object with
  buoyancy 0 falls as it does with no target, one with buoyancy 1 stays where it is;
- an angular velocity a script sets is taken as the speed the spring starts from and is brought to the target, not fought;
- `STATUS_ROTATE_X`, `_Y` and `_Z` locks: the spring acts on the axes left free, and an object with its turning axis locked
  does not turn about it;
- a vehicle keeps its own controller: the target does nothing on an active vehicle;
- selected in the build tool the object is held still; the request is kept and acts when it is let go.

On the ground at one physics step per heartbeat the object can twitch back by under a degree as it settles; it stays within
2 degrees of the target. Through the simulator's own handlers (a rotate-drag of 90 degrees in 1 s at 30 updates a second, a 1 kg
or 100 kg cube, a 4 m cube or three linked cubes resting on the ground) the object ends within 0.3 degrees of the cursor's
rotation without passing it by more than 0.2 degrees, at one physics step per heartbeat and at 45 steps a second; it trails the
cursor by about 42 degrees at the fastest part of the turn, since the simulator hands the target over once per frame.

When a rotate-drag ends the simulator switches the target off and then gives back an angular velocity a script had set with
`llTargetOmega` during the drag. Switching the target off does not zero an angular velocity written since the last physics step,
so that spin carries on (clear of the ground; on the ground contact friction stops a set spin, as it does without a target).

## Avatar speeds

For a held forward or back key the simulator asks every physics engine for the same speed, 4.096 m/s, walking
or running: the viewer sends running as a separate flag (its SetAlwaysRun message), and each
engine decides what running does. Flying, it asks for four times that, 16.384 m/s. Each engine turns
the request into a speed of its own: ubODE divides it by `av_movement_divisor_walk` (1.3) or `av_movement_divisor_run` (0.8);
BulletSim multiplies it by `AvatarWalkVelocityFactor` (1) or `AvatarAlwaysRunFactor` (1.3) and does not
scale a flight. Jolt multiplies it by one of four `[Jolt]` factors, whose defaults give Second Life's
documented speeds on level ground, and flying up and down ([Default Avatar Movement Speeds](https://wiki.secondlife.com/wiki/Default_Avatar_Movement_Speeds)).
The viewer sends the same fast flag for flying up and for flying down, so the simulator asks 16.384 m/s for
each, as for flying level:

| Key | Default | Applies to | Level-ground speed at the default |
|---|---|---|---|
| `AvatarWalkSpeedFactor` | 0.78125 | walking (horizontal part) | 3.20 m/s |
| `AvatarRunSpeedFactor` | 1.2524414 | always run on (horizontal part) | 5.13 m/s |
| `AvatarFlySpeedFactor` | 0.9765625 | flying (all three axes, so up and level match) | 16.00 m/s |
| `AvatarFlyDownSpeedFactor` | 1.395874 | the downward part of a flight, in place of `AvatarFlySpeedFactor` | 22.87 m/s straight down |

Each takes a number from 0.1 to 10; an invalid value logs a warning and the default is used. The
factor multiplies what the simulator asks, so a script's or region's speed change on the avatar
(`osSetSpeed`) still scales the speed. A velocity handed over at a region crossing or teleport is
kept as it is. Jump height is set by `AvatarJumpSpeed`, not by these. All four are listed, commented
out with their defaults, in the `[Jolt]` section of `OpenSimDefaults.ini`.

A sideways step on its own asks less: the simulator's full speed needs the fast flag of the forward key
or of the up key, and the viewer marks a sideways key with a fast flag of its own that the simulator does
not test, so a sideways step asks 0.6 x 4.096 = 2.4576 m/s (with always run on, 4.096). Jolt applies the
walk or run factor to that like any walk, giving 1.92 m/s walking and 5.13 running. The Second Life page
above lists no sideways speed.

What each engine gives for the same request on level ground (Jolt measured in the harness at
`PhysicsStepRate` 45 and at one step per heartbeat; ubODE's walk measured in a region, its run and
fly worked out from its code; BulletSim from its code):

| | Asked | Jolt with the factors at 1 | Jolt, default factors | ubODE | BulletSim | Second Life |
|---|---|---|---|---|---|---|
| Walk | 4.096 | 4.096 | 3.200 | 3.15 (3.13 measured) | 4.10 | 3.20 |
| Run | 4.096, always run on | 4.096 | 5.130 | 5.12 | 5.32 | 5.13 |
| Fly, level | 16.384 | 16.384 | 16.000 | 12.60 (20.48 with always run on) | 16.38 | 16.00 |
| Fly, straight up | 16.384 | 16.384 | 16.000 | not scaled | not scaled | 16.00 |
| Fly, straight down | 16.384 | 16.384 | 22.870 | not scaled | not scaled | 22.87 |

## Arriving avatars

An avatar's physics body is made at login, at a teleport, when it arrives from another region or
crosses a border, and when it stands up from a seat or a vehicle. Jolt puts it at the position the
simulator gives, as BulletS and ubODE do: on a prim platform it stands on the platform, in the air it
falls (or stays, when flying) until it lands on what is below. Flying and velocity are kept as given.
The one correction: a position that would put the body below the terrain is lifted to stand on the
terrain. An arriving avatar is never moved down.

An avatar made over a platform that is held at rest, or one that is already moving by keyframed motion,
stands on it from the first step and is carried by it without a drop (measured to within 0.05 m under
both step modes, `RidersCrossArrivalTests`). An avatar taken out of physics and made again at the same
place on its platform, as a failed region crossing does, is carried again. While it stands on a platform
its feet contact names the platform's root in every collision report, moving or still. Every avatar is
also held inside the region's edges after each step, so one carried past the edge by a platform would be held
at the edge and left behind by the platform. The region exempts an avatar that a crossing object carried in:
it sets `AllowOutsideRegion` on the avatar's actor (a rider behind the object's centre arrives outside the
region), and while it is set the avatar stays where the platform puts it, even with the platform wholly outside
the region. The region clears it when the avatar leaves the object, and the edge hold then applies again.

A physical object that crossed into the region together with avatars is held still until they have arrived
(`PhysicsActor.HoldStill`, then `ReleaseHold` when the last one has, or the hold time ran out): its body is
kept still the way a selected object's is (no gravity, no drift, nothing that touches it moves it) but stays
solid, so an avatar made over it stands on it. Let go, it moves on with the velocity it arrived with. An
object that is selected when the hold ends stays still until it is deselected (`HeldPhysicalPrimTests`, both
step modes). ubODE's held body is also solid; it stops and disables the body instead.

A vehicle is held the same way and takes up where it was: the vehicle step is skipped while it is held, so no motor
or hover builds up, and the release tells the controller the gap was a hold (`ResumeAfterHold`). Without that, a
hold of over a second looked to the controller like a vehicle that had been idle, and it released its motors
(`HeldVehicleTests`: a balloon with a motor and a hover height, held five seconds, moves after the release as
one that was never held does, both step modes).

Under `BasicForcesBehaviour = InWorldz` such an object is also kept from crossing out again for three seconds after it
arrived (`PhysicsActor.WaitsBeforeCrossingBack`, as Halcyon's `SceneObjectGroup.TestForAndBeginCrossing` does). Core tells
the actor when that wait begins and ends (`PhysicsActor.KeepInsideBorder`). While it runs, a body that a script or motor
drives across the border stays in the engine: it is set back to half a metre inside the region, loses its speed outward,
and the avatars standing on it are set back to the same place on it with no velocity (Halcyon leaves the object in its
scene and sets it back inside, `PhysxPrim.CrossingFailure`). Taken out of the engine as for any crossing, the body would
leave its riders with nothing under them and the speed it gave them (`RecrossWaitRiderTests`). Under `SecondLife` there
is no wait and nothing changes.

## What an avatar stands on

An avatar standing on the terrain reports ground (`CollidingGround`), and one standing on a prim,
including a prim lying on the terrain, reports an object (`CollidingObj`); in the air it reports
neither. The avatar's collisions reach the simulator each heartbeat as they do with BulletS and
ubODE: the terrain as land, so scripts in its attachments get `land_collision_start`,
`land_collision` and `land_collision_end`, and a prim or another avatar as an object, giving
`collision_start`, `collision` and `collision_end`. The floor contact also sets the collision plane
under the avatar's feet. Each contact carries the speed at which the two met (see "Avatars and physical
objects"), from which the simulator plays collision sounds and works out impact damage. Where damage is
on, a prim with a damage value set damages the avatar it touches and is removed, as the simulator does
with every physics engine.

An avatar's capsule is changed in place when its height changes. A change of its radius also makes the
avatar's controller again, because Jolt takes the supporting plane (one radius below the capsule's centre)
only when the controller is made; position, velocity, contact events and the avatar-against-avatar collision
carry over.

## Avatars and physical objects

Second Life documents that an avatar can push a physical object by walking or flying into it,
that an object dragged into an avatar should move it, and that colliding with physical objects hurts residents (the Push and Damage page, https://wiki.secondlife.com/wiki/Push_and_Damage). It gives no figures, so Jolt follows
ubODE, where an avatar is a body that meets an object with no bounce:

- An object that strikes an avatar never goes into or through it. The two go on along the line of the
  hit at their common speed, weighted by mass, with the avatar's mass (1.95 for the default avatar): a 1 kg box thrown at
  5 m/s carries it along at 1.7 m/s, and a 100 kg box at up to 4.9 m/s. The
  avatar's share is a push, held to `AvatarPushMaxSpeed` and the push allowance like any other; what
  it cannot take the object loses. An avatar standing on the ground is not pushed down into it: an
  object that lands on its head stops there and comes to rest on it, and falls once they part.
- An avatar walking or running into an object pushes it along, level, with a force of its mass times its
  speed into the object times `AvatarObjectPushPerMomentum` (Halcyon's rule), held to
  `AvatarObjectPushMaxForce` and scaled by its push strength, and never faster than the avatar moves that way.
  Running pushes harder than walking. A light box goes along ahead of it at its pace; a 2 m cube of
  mass 80 is moved at about 3 m/s by a walk and 5 m/s by a run; one held by more friction than the
  force (a mass over about 340) barely moves and stops the avatar. The avatar does not step up onto
  an object it is pushing.
- What an avatar stands on carries it as before: it can stand on a physical object without sinking or
  sliding it away.
- Two avatars still push each other as before, as in ubODE.
- Every contact report carries the speed at which the two sides met along the contact normal
  (`ContactPoint.RelativeSpeed`, below zero while they close), for prims, avatars and the land. The
  simulator plays a prim's collision sound above 0.2 m/s, and an avatar takes impact damage where
  damage is on below -5 m/s, from the land (a fall from about 1.3 m or more) or from a prim.
- An avatar names an object it touches by the object's root prim, as a prim does (see "Physical
  linksets") and as ubODE names every collider: an attachment's `llDetectedKey` and `llDetectedName` in
  its collision events are the object's, a collision filter set by name matches the object's name, and
  an avatar touching two prims of one linkset has one collision with it, carrying the harder strike. The
  prim it touched names the avatar, so a script in that prim gets its own link number from
  `llDetectedLinkNumber`.
- Two avatars that meet are both told, each naming the other, as in ubODE. Jolt finds the contact in
  the update of the avatar that meets the other; before, at one step per heartbeat, an avatar walking
  into another could push it along and never get a collision event for it.

A flying avatar meets objects by the same rule. Second Life documents nothing on a flying avatar that
is hit; Jolt follows ubODE, where it stays flying:

- An object thrown at a flying avatar moves it along the line of the hit at their common speed by
  mass, within the push limits; it keeps its height and stays flying, and the push fades over about a
  second, as any push on a flying avatar does. A 100 kg box thrown at 5 m/s carries a flying avatar
  along at up to 2.8 m/s, about 3 m in all; a 1 kg box moves it about 6 cm at 5 m/s and up to 25 cm
  at 20 m/s.
- A flying avatar flying into a fixed prim or a heavy object stops at it. Flying into a light object
  pushes it along ahead at about the avatar's own speed. A flying avatar does not press what it
  touches with its weight: an object it brushes or passes over is not driven into the ground.

The simulator takes a seated avatar out of physics (`ScenePresence.RemoveFromPhysicalScene`, on every
engine), and the physics engine is not told who sits where. So on Jolt:

- A seated avatar adds no mass or shape to what it sits on, as on ubODE: a physical seat or vehicle
  keeps its own mass, and objects pass through where the avatar sits. Second Life documents that the mass of a seated avatar counts toward the object's mass (`llGetObjectMass`, https://wiki.secondlife.com/wiki/LlGetObjectMass) and a seated avatar's collision volume
  (`SIT_FLAG_NO_COLLIDE`, `llSetLinkSitFlags`, https://wiki.secondlife.com/wiki/LlSetLinkSitFlags); neither can be done in the physics engine alone.
- The seat's prims get a `collision_end` for the avatar when it sits, and the seated avatar's
  attachments get no collision events until it stands.
- Standing up, the simulator puts the avatar back where `ScenePresence.StandUp` places it, without
  looking at what is there. Next to an object or on top of one, it stands where it is put. Inside an
  object, fixed or physical, it is set out at the nearest side in the step it is put there, at once,
  and stays on the ground beside it: it is not thrown out (ubODE's contacts correct an overlap at up
  to 60 m/s). Being set out is not moving: it reports no speed for that step (before, 13.7 m/s for
  1.2 m at one step per 11 Hz heartbeat). It is left 1 cm further than Jolt's character padding from
  the side it was set out of, and what it stands on and touches is found again there, so that side
  is not its ground afterwards: it falls to the ground beside it at the speed it really falls, and the
  object does not carry it. Before, it could be left touching the side face with its feet a few
  millimetres up, which Jolt can count as standing on that face: it hung there, or, at
  PhysicsStepRate 45, slipped off and was snapped to the ground (4.75 cm in one step, 2.14 m/s), or
  rode a light object up as it tipped (8 cm, at up to 1.04 m/s). Which of these happened turned on
  rounding that differs between platforms. A physical object it is put inside is not pushed (before,
  the avatar was set out of a 1 kg cube a step late, reported 38 m/s for that step, and pushed the
  cube about 0.4 m aside).

## Buoyancy and hover on an avatar

A script in an attachment reaches its wearer: core hands `llSetBuoyancy` to the avatar's physics body
as its buoyancy, and `llSetHoverHeight` and `llStopHover` as its hover (SceneObjectGroup.SetBuoyancy,
SetHoverHeight). Jolt acts on both, following ubODE where Second Life documents nothing:

- Buoyancy scales the gravity on the avatar by (1 - buoyancy), as on a prim (`llSetBuoyancy`, https://wiki.secondlife.com/wiki/LlSetBuoyancy: at 1.0 the object floats, above 1.0 it rises). At 1 an avatar that walks
  off a ledge goes on level in the air; at 0.5 it falls at half gravity; above 1 it rises off the ground.
  As in ubODE it does nothing while the avatar flies or hovers. A floating avatar (buoyancy 1 or more) is
  not drawn down onto the floor as it walks off an edge.
- Hover holds the avatar's position at the height above the ground, the higher of ground and water, the
  water, or the region's zero, as the call asks (core's hover type), with the critically damped spring a
  prim's hover uses and tau as its timescale ("Critically damps to a height above the ground (or water)
  in tau seconds", `llSetHoverHeight`). The height follows the ground along the avatar's path, so it
  holds over a slope while the avatar walks. Gravity is off and a jump does nothing while it hovers, as
  in ubODE; walking and flying across still work. It never holds the avatar lower than standing on the
  ground. A height of 0 or `llStopHover` ends it and the avatar falls. Hover moves the avatar up or down
  at no more than 50 m/s (ubODE's limit).
- Neither is kept when the avatar's physics body is made again (a teleport, a region crossing, standing
  up); core does not hand them over again, with any engine.

Script engines differ (no change made to either): YEngine reaches the wearer for both calls. Phlox
returns from `llSetHoverHeight` and `llStopHover` when the script's own prim has no physics body, which
an attachment never has, so under Phlox they do nothing on an avatar; its `llSetBuoyancy` reaches the
wearer. `llGroundRepel` from an attachment does nothing on either engine.

## A constant force on an avatar

`llSetForce` in an attachment sets a constant force on its wearer ("Used on an attachment, it will apply
the force to the avatar", `llSetForce`). Core keeps it on the avatar and hands it to the avatar's physics
body (`PhysicsActor.SetConstantForce`), and again to every new body the avatar gets (standing up, a
teleport). Second Life documents nothing more, so Jolt applies plain mechanics: the avatar accelerates by
force / mass (the avatar's mass, 1.95 for the default avatar) on every step, on top of whatever else moves it, until the next call.

- A local force is in the avatar's own axes and turns with the avatar; a region force does not.
- Up and down, it adds to gravity (and to buoyancy's share of it). An upward force equal to the weight
  (784.5 N) holds an avatar in the air at its height; twice that lifts it at g. On the ground, a downward
  force, or an upward one less than the weight, changes nothing.
- Across, and in all three axes while flying, it adds a speed kept from step to step. What blocks the
  avatar takes it, as it takes a push's speed, and it fades as a push's speed does: on walkable ground
  with a 0.25 s timescale and while flying with 1 s, so under a steady force a standing or walking avatar
  drifts at force / mass x 0.25 s on top of its walk (2 m/s2 gives 0.5 m/s) and a flying one at force /
  mass x 1 s. In the air it does not fade: the avatar accelerates at force / mass.
- While hovering, the vertical part moves the height the hover settles at by force / mass x tau^2.
- A flying avatar has no gravity for the force to cancel, so an upward force equal to the weight lifts it
  (9.8 m/s2 up, settling at 9.8 m/s with the 1 s fade) instead of holding it. A wearer an autopilot has put
  into flight (`ScenePresence.MoveToTarget` flies any walker sent to a point higher than the ground plus the
  avatar's height) is such an avatar. When its flight ends it keeps the speed the force gave and carries on at
  that speed, since the weight up and gravity cancel.
- A new call replaces the force and a zero force ends it. The speed it gave is kept, and fades as above.
  A force that is not a number is refused and logged.
- It never takes an avatar past the engine's body speed cap (500 m/s, the cap a prim under `llSetForce`
  meets). Pushes keep their own cap, and hover its 50 m/s.
- A seated avatar has no physics body, so the force does nothing while it sits; it acts again when the
  avatar stands.

It has an effect only under a script engine that calls `SetConstantForce` for an attachment. Phlox does
(`llSetForce` from an attachment); YEngine does not, so under YEngine an attachment's `llSetForce` does
nothing on its wearer.

## The velocity an avatar reports

An avatar reports the velocity it really moved at in its last physics step, with the gravity it gained
after the move: what a wall or the ground stopped is not in it. Walking into a wall it reports about 0;
walking freely, its walk speed; standing on a moving object, the object's speed; pushed, the speed the
push gives it. ubODE reports its avatar body's velocity, which a wall stops likewise. Before, Jolt
reported the velocity the avatar was asked to move with, so an avatar held at a wall reported its full
walk speed. Core sends this velocity to viewers to move the avatar between updates, returns it to
scripts (`llGetVel` in an attachment, `llDetectedVel`, sensors' ACTIVE flag), carries it into a teleport
or a region crossing, and the animator reads its vertical part to choose falling.

Jolt's character also moves an avatar out of anything it overlaps, all at once in one step. That is
not speed, and is not reported as such:
- In the step after the simulator puts the avatar somewhere (it arrives, stands up, or its position is
  set), an avatar moved further than its own velocity takes it was set out of what it was put inside,
  and reports no speed for that step.
- An object coming at the avatar that may push it carries it at the object's speed; what the avatar is
  moved beyond that in the same step was the object's overlap, and is not reported. A heavy linkset
  catching up a flying avatar as its push fades went a little into it before it could push it, and
  the avatar would report 14.05 m/s for one step at PhysicsStepRate 45 while carried at 8.7; it reports
  the 8.7.
Where the avatar moves is unchanged by this, and so is everything an avatar hit by nothing reports.

## Fast objects

A physical prim that moves fast is checked along its path, so it does not pass through a thin wall,
another object or the ground between two steps. In each of Jolt's collision steps (`[Jolt]
CollisionSteps` per physics step), a physical prim that would move further than 0.75 times its shape's
inner radius (the radius of the largest sphere that fits inside it; Jolt's `mLinearCastThreshold`) is
cast along its motion, Jolt's `LinearCast` motion quality, and stops at the first thing it would hit.
At one physics step per 11 Hz heartbeat (six collision steps) that is a 0.2 m ball from about 5 m/s
and a 1 m box from about 25 m/s; the faster the steps, the higher the speed. A prim slower than that
is not cast and moves exactly as it did before. The test is made with the prim's velocity at the
start of each physics step, so a prim knocked to a high speed inside a step is cast from the next one.

A cast prim that hits something gets one contact from it, with the same combined friction and
restitution as any other contact (friction the square root of the two prims' product, restitution
their product), so it bounces or slides as a slower prim would. The cast follows the prim's motion in
a straight line: it does not cover what the prim's turning sweeps through in a step.

A vehicle is cast in every collision step in which it is fast enough, whatever its speed was at the
start of the step, as before.

Jolt's cast does not see avatars: an avatar meets objects in its own update, once per physics step,
before the simulation's update. So before each update, a physical prim that would go on more than an
avatar's radius past where it first touches the avatar in that update, with nothing nearer in its way,
is moved to that touch and strikes the avatar there, by the rule in "Avatars and physical objects":
both go on at their common speed by mass, the avatar's share held to its push limits, and both are told
of the contact with the speed at which they met. A 0.2 m ball shot at 50 m/s at a standing avatar
stops against it and barely moves it. A slower prim reaches the avatar in its own update, as before.
A physical linkset is checked the same way, as one object: a three-prim linkset of 100 kg thrown at
20 m/s at a standing, walking or flying avatar stops against it or carries it along within the push
limit, at either physics step rate. A flying avatar it has struck can show one physics step's velocity
above the limit (14 m/s at a 45 Hz step rate) when the linkset catches it up again as its push fades.

A phantom prim touches only the terrain, so a fast prim goes through it. Jolt does not cast against
sensors, and a volume-detect prim finds what is inside it at the start of each collision step, so a
fast prim that is never inside it at such a start, which can happen when it crosses it in less than a
collision step, would raise no `collision_start` from it. The module closes that: in an update that has
a volume-detect prim in the region, each awake physical prim that moves further than 0.1 m is swept from
where it was to where the update left it against the volume-detect prims, and a detector the path went
through, that the engine did not report in that update and that the prim does not overlap where it ended
(the engine reports that one in the next update), gets one `collision_start`; the next heartbeat has no
contact, which is the `collision_end`. A prim stopped by a wall before the detector never crosses it and
raises nothing. Second Life says only that a volume-detect prim raises both events while
objects interpenetrate it (https://wiki.secondlife.com/wiki/LlVolumeDetect); it documents no speed beyond which a crossing
is missed. `VolumeDetectFastCrossingTests` shoots a 0.2 m ball through a 0.1 m detector at 5, 25, 50 and
200 m/s at one physics step per heartbeat and at 45 Hz: one start and one end each.

An avatar is swept the same way after its own move: when the avatar moved further than 0.1 m in a step and the region has a
volume-detect prim, its capsule is cast from where it was to where it is, and a detector the path crossed that the avatar's update
did not report and the avatar does not overlap where it ended gets one `collision_start`. Without it a flying avatar that crossed a
0.1 m detector at 48 m/s at one step per heartbeat, or at 96 m/s at either step rate, raised nothing. `VolumeDetectFastAvatarTests`
flies an avatar through a 0.1 m detector at 16, 48 and 96 m/s at both step rates (one start and one end each) and past one without
touching it (nothing).

`FastObjectTests` shoots 0.05, 0.2 and 1 m balls at 10 to 500 m/s (`[Jolt] BodyMaxLinearSpeed`, 500 m/s
by default, is the most a prim can move) at a fixed wall 0.01 m and 0.1 m thick, at a resting 0.5 m
box and down at the ground, at each heartbeat rate of the harness and at 11 Hz with
`PhysicsStepRate` 45, and none passes through. `FastObjectsLinksetsAndAvatarsTests` covers where fast
prims, linksets, resting contacts and avatars meet: a fast ball at an avatar, a fast ball's bounce, a
fast ball on a sleeping box, a linkset thrown at or walked into by an avatar, a sleeping linkset with a
resized prim, and a ray at a sleeping linkset. `FlyingSeatedAndFastLinksetAvatarTests` covers a flying
avatar hit by a box and flying into a wall, a light post and a heavy one, a seated avatar, an avatar
standing up next to, on and inside an object, and a fast linkset at a standing, walking and flying
avatar.

## Phantom and volume-detect prims

Second Life documents both (`llSetStatus` with `STATUS_PHANTOM`, https://wiki.secondlife.com/wiki/LlSetStatus, and `llVolumeDetect`, https://wiki.secondlife.com/wiki/LlVolumeDetect): objects and avatars pass
through a phantom object, while a physical phantom one still collides with the ground but does not pass through it, and queues land
collision events; with `llVolumeDetect` a physical object or an avatar can go straight through the prim, which
raises `collision_start` and `collision_end` while they interpenetrate. Jolt does this as follows:

- A volume-detect prim is a sensor. Avatars and physical objects pass through it without slowing, and
  it reports what is inside it; the simulator turns that into one `collision_start` when each enters and
  one `collision_end` when it leaves. What passes through is not told, so an avatar's attachments get
  no event from it. A non-physical one detects avatars and physical objects; a physical one falls
  through the ground.
- A physical phantom prim collides with the terrain and nothing else: it rests on the ground, raises
  land collision events, and passes through prims and avatars. A non-physical phantom prim is kept out
  of physics by the simulator, with every engine.
- Switching phantom or volume detect on or off on a prim, or on a linkset, changes the prim's body in
  place: it keeps its position and its physics body, and a physical one that was resting on something
  it can pass through falls.
- A ray cast finds phantom and volume-detect prims only when it asks for them: YEngine's `llCastRay`
  with `RC_DETECT_PHANTOM`. Phlox's `llCastRay` does not pass that option to the physics engine, so its
  casts do not find them.
- `PRIM_PHYSICS_SHAPE_CONVEX` on a non-physical prim that is not a mesh makes it collide as its convex
  hull. A non-physical mesh prim still collides as its triangle mesh whatever its shape type;
  `[Jolt] HonourConvexMesh = true` makes it one convex hull of its points (see "Mesh prims").

The harness scenarios beginning `vd-` and `phantom-` show each of these, and print the collision events
each part raised.

## Resting contacts and collision events

Second Life documents `collision_end` as raised when the object stops colliding with another object, and
`land_collision_end` when it stops colliding with land, and that an object or avatar resting on a physical object does not
keep triggering collisions, apart from a few times, unless there is movement (the event pages https://wiki.secondlife.com/wiki/Collision_end, https://wiki.secondlife.com/wiki/Land_collision_end and https://wiki.secondlife.com/wiki/Collision). Jolt does this as follows:

- An object that comes to rest on another object, on the ground or inside a volume-detect prim and falls
  asleep keeps touching it: no end event comes when it falls asleep, and one comes when it is moved off.
  Jolt looks for contacts only where a body is awake, so the module keeps the contacts a body had when
  neither side of them is awake, as ubODE does with its sleeping prims.
- `collision` comes each heartbeat while the object settles and stops once it is asleep.
  `land_collision` goes on while it sleeps, as in ubODE; the wiki says nothing about it at rest.
- Only prims whose scripts have a collision event are tracked, so the rest cost nothing extra.
- `VelocityIterations` and `PositionIterations` in `[Jolt]` set the engine's velocity and position
  steps (defaults 20 and 2; Jolt's own are 10 and 2). The velocity default is 20 because a tall stack
  needs more velocity steps to come to rest: ten stacked 0.5 m boxes do not fall asleep at 10 at 11 or
  45 Hz, and fall over at 22.5 Hz; at 20 they stand and sleep at 11 to 90 Hz.
- A sleeping object moved by setting its position wakes (see "Editing and moving physical objects"), and
  an object asleep on a fixed prim wakes when that prim is moved or turned, or its friction, restitution or
  material changes, so it falls or slides instead of hanging in the air. Either way the contact ends once they part: one end event on each side.
- A selected object keeps its contacts while it is held, and gets no end event for them.

## Vehicles

LSL vehicles run on a controller that steps each behaviour Second Life documents, on that documented
model. By default (`VehicleBehaviour = InWorldz`, see "Vehicle behaviour: SecondLife or InWorldz" below) a
vehicle also follows the InWorldz Halcyon simulator where it differs from the documentation: its type
presets, a boat's ground drag and camera steering, for example. `VehicleBehaviour = SecondLife` keeps to what Second Life
documents. The sled's slope assist (`VehicleSledAssist` below) is not a documented Second Life behaviour; it comes from
the InWorldz Halcyon sled code. The controller's frame structure and parameter handling come from the InWorldz Halcyon vehicle
code. Every behaviour is stepped as the exact solution over the step
the engine takes, so a vehicle drives the same at one step per heartbeat and at any
`PhysicsStepRate`. The sections below give the model, the `[Jolt]` keys that govern vehicles, the
known gaps against Second Life, and how to try a vehicle in the harness before a region.

### Ground vehicles and gravity

`[Jolt] VehicleGroundGravityFactor` (0 to 1, default 1) is the share of gravity a car or sled
(`VEHICLE_TYPE_CAR`, `VEHICLE_TYPE_SLED`) gets while it touches the terrain or another object.
"Touching" is the physics engine's contact in the last step. The default 1 leaves gravity whole.
BulletSim's vehicle code uses 0.2 (`[BulletSim]
VehicleGroundGravityFudge`).

```ini
[Jolt]
    VehicleGroundGravityFactor = 0.2
```

A car that only its linear friction slows rolls down a slope at a steady speed in proportion to the
factor: `f * g * sin(angle) * Tf`. In the harness the test car (linear friction timescale 1 s)
rolls 0.86 m/s down 5 degrees with 1, which is the formula's value, and about a fifth of that
with 0.2.

### Vehicle type presets

`llSetVehicleType` gives each type its defaults. `[Jolt] VehiclePresets` chooses which (where the key is not set,
[`VehicleBehaviour`](#vehicle-behaviour-secondlife-or-inworldz) does: `InWorldz`, the default, gives `legacy`, and
`SecondLife` gives `documented`):

- `documented`: Second Life's documented values, from the wiki page of each type (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial)
  (`VEHICLE_TYPE_SLED`, `_CAR`, `_BOAT`, `_AIRPLANE`, `_BALLOON`). The sled page's hover efficiency
  of 10 is held to 1, as `llSetVehicleFloatParam` holds any efficiency, and the sled has hover off:
  the page's hover timescale of 10 s would turn hover on, against the page's own "no hover", so the
  sled's is 1000 s.
- `legacy`: the InWorldz Halcyon values, for vehicles tuned against them, except the car (below). A region
  gets these by default.

```ini
[Jolt]
    VehiclePresets = legacy
```

A script's own `llSetVehicleFloatParam`, `llSetVehicleVectorParam` and `llSetVehicleFlags` calls
override either set; most scripted vehicles set their own values after the type, and differ only
in what they leave at the preset. Choosing a type sets that type's flags and clears every other
flag, also one a script set before `llSetVehicleType`. The module's own extension parameters (wind, mouselook,
motor disabling) are not Second Life parameters and are the same in both sets. The legacy boat and
airplane also carry the module's torque-about-world-z flag, which no documented type sets.

The legacy car is the InWorldz car, as the Halcyon source gives it: linear friction (100, 0.1, 10), angular
friction (100, 100, 0.3), the linear motor 0.5 / 1 / 1 s with decay 10 / 2 / 2 s, the angular motor
0.2 / 0.2 / 0.05 s with decay 0.3 / 0.3 / 0.1 s, linear deflection 1 / 2 s, angular deflection 0.5 / 2 s, the vertical
attractor 0.6 / 2 s, banking -0.2 with mix 1 and timescale 1 s, and the flags `NO_DEFLECTION_UP`, `LIMIT_ROLL_ONLY`,
`HOVER_UP_ONLY` and `LIMIT_MOTOR_UP`. It has no torque about world z (the legacy boat and airplane do; the car does
not). It differs from the documented car in the friction, motor and decay timescales, the angular deflection and the
attractor.

Two vehicle types exist on InWorldz and not in Second Life: `VEHICLE_TYPE_SAILBOAT` (10001) and `VEHICLE_TYPE_MOTORCYCLE`
(10002). Both are always available, with either preset set and either behaviour, since there is no Second Life type to differ
from; their values are the InWorldz source's, value for value (Halcyon's `VehicleDynamics.cs`, read, not run):

| | Sailboat (10001) | Motorcycle (10002) |
|---|---|---|
| linear friction / angular friction | (200, 0.5, 3) / (10, 1, 0.2) | (100, 0.1, 10) / (3, 0.2, 10) |
| linear motor timescale / decay | (1, 5, 5) / (1, 10, 10) | (0.5, 1, 1) / (10, 1, 1) |
| angular motor timescale / decay | (2, 2, 0.1) / (0.3, 0.3, 0.1) | (0.1, 0.1, 0.05) / (0.2, 0.8, 0.1) |
| linear motor offset | 0 | (0, 0, -0.1) |
| hover height / efficiency / timescale | 0.0001 / 0.8 / 0.5 s | 0 / 0 / 1000 s (off) |
| buoyancy | 0 (1 under `SecondLife`) | 0 |
| linear deflection efficiency / timescale | 0.5 / 3 s | 1 / 2 s |
| angular deflection efficiency / timescale | 0.5 / 5 s | 0.8 / 2 s |
| vertical attractor efficiency / timescale | 0.5 / 0.3 s | 1 / 1 s |
| banking efficiency / mix / timescale | 0.8 / 0.5 / 0.5 s | 0.95 / 0.5 / 0.1 s |
| inverted banking modifier | -0.2 | -0.5 |
| motors disabled above / after | 0 / 0 | 1.5 m / 2.5 s |
| flags | `NO_DEFLECTION_UP`, `HOVER_WATER_ONLY`, `LIMIT_MOTOR_UP`, `LIMIT_MOTOR_DOWN`, `REACT_TO_WIND`, `REACT_TO_CURRENTS`, `TORQUE_WORLD_Z` | `NO_DEFLECTION_UP`, `HOVER_UP_ONLY`, `LIMIT_MOTOR_UP`, `LIMIT_MOTOR_DOWN`, `LIMIT_ROLL_ONLY`, `TORQUE_WORLD_Z` |

The sailboat's wind and water-current efficiencies (0.02, 0.001, 0 and 0.1, 0.01, 0) and its two react flags act under
`InWorldz` ("Wind and water currents" below) and are only held under Second Life's behaviour. A motorcycle is a ground vehicle where `VehicleGroundGravityFactor`
is concerned, as InWorldz treats it as a car. A script's type number reaches the engine as it is (`llSetVehicleType` ->
`SceneObjectPart.SetVehicleType` -> the physics actor's `VehicleType`); the Phlox script engine knows both constants and accepts
the numbers, the YEngine takes its constants from `ScriptBaseClass` (`LSL_Constants.cs`, `XMREngine.cs:397`), which lacks both names (a script there can pass the number).
The simulator's own saved record of a vehicle (`SOPVehicle`) has no preset for these two types: a vehicle of either type that is
handed back from its saved record (a region restart, a rez, a crossing) has the no-vehicle values for every setting the record
holds, with only what its script set, until the script sets its type again; `SOPVehicle.ProcessTypeChange` would need a case for each.

A vehicle rotation or vector parameter that is not a number (a NaN or an infinity in any component) is refused,
as a float one is, and the stored value is kept; this includes `VEHICLE_REFERENCE_FRAME`, also when a saved
vehicle hands one back.

The module's vehicle debugging and enablement switches (`DoMotors`, `DoBanking` and the others on
`VehicleSettings`, all on, and the `Debug*` ones, all off) are part of a region's vehicle settings: one region's
setting does not change another's. They have no `[Jolt]` keys.

### Vehicle behaviour: SecondLife or InWorldz

`[Jolt] VehicleBehaviour` chooses whose behaviour a region's vehicles follow where Second Life's documentation and the
InWorldz (Halcyon) simulator differ. It is read once when the region starts.

- `SecondLife`: what Second Life's wiki documents, with the documented presets. A region that wants only what the wiki documents sets this.
- `InWorldz` (the default): Second Life compatible behaviour with the fixes of the InWorldz Halcyon simulator: the behaviours in the table below,
  camera steering (below the table), and the InWorldz preset values (`VehiclePresets = legacy`). A boat's ground drag, the motor offset on every
  linear force, NO_DEFLECTION_UP removing only an upward change, banking by the sine of the roll without the attractor and the vehicle's gravity
  multiplier are part of it too.

A vehicle's running motors cross a region border under both behaviours.

```ini
[Jolt]
    VehicleBehaviour = InWorldz
```

Any other value is refused at start-up: the log names the allowed values (`SecondLife, InWorldz`) and `InWorldz` is used.

**How it combines with `VehiclePresets`:** where `VehiclePresets` is set it decides the preset values, whatever the behaviour;
where it is not set, the behaviour does (`InWorldz` gives `legacy`, `SecondLife` gives `documented`).

| Behaviour under `InWorldz` | `SecondLife` (documented) | `InWorldz` (Halcyon) | What a driver notices |
|---|---|---|---|
| `LIMIT_ROLL_ONLY` | The attractor leaves the pitch alone for every type. | Cars, boats and sleds keep a tenth of the pitch spring (airplanes and balloons keep none). | A roll-only car left nose-up or nose-down comes slowly back to level. |
| `HOVER_UP_ONLY` | Hover never pushes down, and buoyancy vanishes above the hover height. | Above its height the hover acts only while buoyancy is positive, and then pushes down; buoyancy does not vanish. | A buoyant up-only hover vehicle (a balloon) is held at its height instead of drifting above it. |
| Friction that would push up | Friction acts on every axis, so a falling vehicle with a short vertical friction is slowed. | That friction is dropped. | A falling car falls at g, not at its slowed terminal speed. |
| Motor decay | The motor starts to decay as it is set. | A motor whose timescale vector is shorter than 0.9 holds its whole grip for up to 0.06 s first. | A quick motor (a car's turning motor) reaches more of its speed before its decay takes it back. |
| Self-righting | The attractor is the spring of the vehicle's timescale and efficiency. | Vehicles other than airplanes are righted harder (the spring is `1 + (1 + tilt / pi)^4` times as stiff), and while overturned (tilt of 108 degrees or more) and not improving, harder by a factor of 1.3 every 15 ms, up to 10 times. | A flipped car rights itself sooner. |
| `VEHICLE_BUOYANCY` | The share of gravity is `1 - buoyancy`. | A buoyancy from 0.977 up to (not including) 1 acts as 1. | A balloon set to 0.98 floats instead of sinking slowly. |
| Upward linear motor | The motor pulls toward its direction on every axis. | The motor's upward change of velocity in a step has `g * (1 - buoyancy)` (g negative) added to it, and is dropped if that leaves it below zero (`VehicleDynamics.cs:2707-2712`, read, not run). The comparison is a change of velocity in one step against an acceleration, as there, so for a vehicle with no buoyancy an upward push is dropped unless it changes the velocity by more than 9.8 m/s in one step, and a push bigger than that has 9.8 m/s taken off it. | A car or boat with an upward motor component does not rise (a vehicle with buoyancy 1, a balloon, is not touched). A motor that pulls up against a fall (a motor target below the falling speed) is dropped too. |
| Hover moderation | Hover and the linear motor act together. | While a hover flag (`HOVER_GLOBAL_HEIGHT`, `HOVER_TERRAIN_ONLY`, `HOVER_WATER_ONLY`) is set, the hover timescale is below 300 s and the motor still moves the vehicle up or down by more than 0.005 m/s in a step, the linear motor's decay clock is put on 10 s each step (`VehicleDynamics.cs:2714-2720`). | A hovering vehicle with a vertical motor loses its whole linear motor, all three axes, within a few steps. |
| Hover at height 0 and efficiency 0 | A hover of height 0 with efficiency 0 hovers (bouncing about the ground). | It is no hover, "legacy scripts" (`VehicleDynamics.cs:1563-1564`); a hover that was on has its timescale put to 300 s as well, so a later height and efficiency do not bring it back until the script sets the timescale again. | A script that clears its hover by zeroing the height and efficiency stops hovering. |
| Gravity while hover acts | Gravity and buoyancy act alongside the hover spring, so a vehicle with less than full buoyancy hovers below its height, where the spring holds its weight (the Linden Vehicle Tutorial: "Hover"). | Hover switches gravity off while it acts, whatever the buoyancy (`PhysxPrim.pid.cs` `DoHover`, `DoHoverTarget`: `_hoverDisableGravity`; `PhysxPrim.cs` `WantsGravity`), so the vehicle is held at its hover height itself. | A sailboat (buoyancy 0, hover height 0.0001 m, hover timescale 0.5 s) rests on the water surface; under `SecondLife` the spring alone would settle it about 2.5 m below (g times the timescale squared), so the sailboat preset gets buoyancy 1 there and floats at the surface too. A hover that is off (timescale 1000 s, or height 0 with efficiency 0) leaves gravity as it was. |
| Attractor dormancy | The attractor acts all the time. | It goes dormant once the tilt has not changed by pi / 256 (0.7 degrees) in a step for 1.5 s, never while the tilt is 108 degrees or more, and wakes when the tilt has moved 0.7 degrees from where it went dormant (`VehicleDynamics.cs:1255-1264`). The step's change is taken per 15 ms of time while it acts, so the step rate does not decide how long it stays awake. | A vehicle that has stopped tilting stays at the lean it has (a car on a slope, a boat settled at an angle) until something moves it 0.7 degrees. |
| Motor timescales for each axis | `llSetVehicleVectorParam` with a linear or angular motor timescale or decay timescale (codes 30, 31, 34, 35) does nothing: Second Life documents them as floats. A float sets all three axes. | The call sets each axis, each held to the range the float setter holds all three to (`VehicleDynamics.cs:406-427`, `LSLSystemAPI.cs:8572-8601`, read, not run). A float still sets all three. | A script that sets a car's throttle and steering timescales apart, or an airplane's roll and pitch, gets what it asked for. |
| Motor decay timescale cap | 120 s, the longest the Second Life page documents. | 1000 s (`VehicleDynamics.cs:406-421`; its 120 s constant is named for these values and not used there), unless `VehicleMaxDecayTimescale` is set in the region's configuration. | A script that sets a decay of 500 s gets 500 s, not 120 s. |
| Angular deflection | The nose turns by a share of the remaining angle each step, `1 - e^(-efficiency x speed share x h / T)`. | A push of angular velocity each step: `speed share x pi x (h / T) x efficiency x ln(1 + angle)` about the normalised Euler angles of the rotation from the nose to the velocity, less a damping term `angular velocity x (h / T) x efficiency x ln(1 + pi - angle) / ln(1 + pi)` within pi / 64 of aligned. It is dropped while the linear motor is stalled against something, and applied only when a component of it is at least pi / 512 in a 15 ms step, a shorter or longer step taking a proportionate share of that line (`VehicleDynamics.cs:1114-1169`). Off at a timescale of 1000 s or more, and below 0.2 m/s, as under Second Life. | A plane swings toward its heading by a push that grows with the angle's logarithm and eases off close to alignment, and stops swinging when it is jammed. |
| Angular motor | The motor and angular friction are one equation, `dw/dt = g (M - w) / Tm - w / Tf`, solved exactly: a friction timescale shorter than the motor's holds the motor down to `M Tf / (Tf + Tm)`. | The motor ramps the velocity by a per-step growth, `next = a (1 + ln(abs M / abs a) / (Tm / h))` (from rest, a stiction start of pi / 64 rad/s; toward zero, `-ln(1 + abs a)`; across zero, to zero and back), with `a` the motor's target when that is further along than the velocity, blends the result with the velocity by its grip `e^(-s / Td)`, and friction takes its share `1 - e^(-h / Tf)` of the velocity at the start of the step on top as its own change. So a short friction does not hold the motor down (`VehicleDynamics.cs:2733-2960`, `1402-1458`). | A quick airplane roll or a car's turn reaches a speed the documented equation would hold down: with a 1 s motor and 0.5 s friction the documented settle is a third of the direction, this ramp goes past three quarters of it. |

**Camera steering** (`InWorldz` only): the mouselook flags do not steer or bank while the camera is within about 2.8
degrees of the vehicle's heading (or of its pitch); outside that the turn is the camera's offset, shaped, as a share of the script's
motor direction, and `VEHICLE_FLAG_MOUSEPOINT_STEER` and `_BANK` do the same in any view; see "Mouselook steering" below.

**InWorldz extension flags.** A script sends them on InWorldz's numbers: `VEHICLE_FLAG_REACT_TO_CURRENTS` 0x10000,
`REACT_TO_WIND` 0x20000, `LIMIT_MOTOR_DOWN` 0x40000, `TORQUE_WORLD_Z` 0x80000, `MOUSEPOINT_STEER` 0x100000 and
`MOUSEPOINT_BANK` 0x200000 (`VehicleFlags.cs:48-60`). Each reaches the engine as itself, from `llSetVehicleFlags`,
`llRemoveVehicleFlags` and a saved vehicle (rezzed, copied, loaded with the region, crossing in). `MOUSEPOINT_STEER`,
`MOUSEPOINT_BANK`, `REACT_TO_WIND` and `REACT_TO_CURRENTS` act under `InWorldz` (camera steering and wind, below) and are held and
read back under Second Life's behaviour. `LIMIT_MOTOR_DOWN` and `TORQUE_WORLD_Z` act under either behaviour,
because a script has to ask for them. Each flag number is read as the flag the script named, so a vehicle that sets one of
them does what the script asked.

**InWorldz parameters** (`llSetVehicleFloatParam`; the code is `VehicleParamCodes.cs`'s, the range is InWorldz's
`ClampFloatParam`, and a value that is not a number is refused). Each is held and read back under both behaviours; a type
change puts the preset value back. The region's saved record of a vehicle (`SOPVehicle`) has no field for them, so a value a
script set is lost when the vehicle is rezzed, copied, loaded with the region or crosses into another: the preset comes back.

| Parameter | Code | Held to | What it does here |
|---|---|---|---|
| `VEHICLE_MOUSELOOK_AZIMUTH` | 11001 | pi / 32 to pi | The sideways range of the mouselook dead zone (`InWorldz` only). |
| `VEHICLE_MOUSELOOK_ALTITUDE` | 11002 | pi / 32 to pi | The vertical range of the dead zone (`InWorldz` only). |
| `VEHICLE_BANKING_AZIMUTH` | 11003 | pi / 128 to pi | The roll range banking counts as a share of (`InWorldz` only; the default pi / 2 is Second Life's). |
| `VEHICLE_DISABLE_MOTORS_ABOVE` | 11004 | as sent | Under `InWorldz`: the motors go off once the vehicle has been higher than this above what lies below it for the delay below (0 is off); see "Motor height" below. Under Second Life's behaviour it is held only. |
| `VEHICLE_DISABLE_MOTORS_AFTER` | 11005 | as sent | Under `InWorldz`: how long the vehicle must have been above the height before the motors go off, on Halcyon's clock, which runs eight times fast (see "Motor height" below). Held only under Second Life's behaviour. |
| `VEHICLE_INVERTED_BANKING_MODIFIER` | 11006 | -10 to 10 | An airplane flying inverted banks by this factor times its banking efficiency (`InWorldz` only; no other type is marked inverted there). |

Each behaviour follows Halcyon's code as read (not run: it needs PhysX); the figures its formula gives are checked in the
tests. Two things are the module's own: the self-righting factor stops at 10 where Halcyon has no limit, and it grows per 15 ms
of time (Halcyon's step) instead of per step, so it does not depend on the step rate. Halcyon's buoyancy adjustment also covers
`llSetBuoyancy` on an ordinary object; here it is applied to a vehicle's buoyancy only.

Under `InWorldz` both motors are Halcyon's (`VehicleDynamics.cs:2576-2945`), and Halcyon ran them once in each physics step of 15 ms
(`PhysxScene.cs`, `TIMESTEP`): a motor step reads the velocity the last step and the contact friction left, sets it toward the motor's
target (the ramp, the decay, and the rule that the target replaces a lower velocity: `VehicleDynamics.cs:2587-2589` for the linear motor,
`2778-2780` for the angular one), and the engine then takes 15 ms of friction from it. The 15 ms is part of the law: the replacement is how
a car gets going against friction, and the distance it loses between two sets of the motor depends on how long they are apart. The
controller runs in every collision step of the engine, which is shorter or longer than 15 ms, so each motor keeps a clock of the time
carried since its last step and runs one of Halcyon's steps (always of 15 ms) for every 15 ms that has gone by, at the start of the
collision step in which it falls due: a collision step shorter than 15 ms runs it on some of its steps only, one longer than 15 ms
runs several, one after the other on the velocity the one before left. Friction, the attractor, banking, deflection and gravity are
integrated in every collision step as before. A vehicle then drives at the same speed at every step rate, and agrees with Halcyon's
motor stepped at 15 ms against contact friction taken as a constant deceleration (the test model `HalcyonMotorModel`): within 2.5 % of its
speed for collision steps up to 31 ms, and about 4 % low at 45 ms and 10 % low at 91 ms, because the engine's step cannot be split to put
the motor's steps between its friction (the ground takes a whole step's worth between two sets of the motor). The angular motor's
clamp against the vertical attractor (`VehicleDynamics.cs:2809-2856`) is part of it, and so is the dead band of its turn about the
world's z: a change under pi / 512 rad/s in a step of 15 ms is not made (`VehicleDynamics.cs:3029-3038`), so a gentle turn keeps
the rate it had when the key went up. The linear motor keeps the documented equation under `SecondLife`.
A per-axis timescale or a decay above 120 s set by a script acts at once, but the region's saved record of a vehicle (`SOPVehicle`)
holds one float for each and keeps a decay at 120 s at most, so after the vehicle is rezzed, copied, loaded with the region or crosses into
another region the motor comes back with one value on all three axes.

Choose `InWorldz` for a region whose vehicles were built for InWorldz or Halcyon grids and are tuned against these behaviours; set
`SecondLife` for vehicles built for Second Life that are tuned against its documented behaviour. In the harness the choice is `--jolt VehicleBehaviour=InWorldz`; the runs `iw-car-pad`,
`iw-car-ramp`, `iw-airplane` and `iw-mouselook-turn` of `ci/runs.txt` record it (a car on level ground, a car down a 15 degree ramp,
an airplane, a car turned 90 degrees by the mouselook camera), checked with the regression check below like the other runs.

Not built from the InWorldz code, and why: the friction stop below 0.002 m/s (`VehicleDynamics.cs:1500-1508`) is reached only when
the vehicle's speed is under 0.002 m/s and one component of its local velocity is at least 0.002 m/s, which cannot both hold (no
component is larger than the speed); it is a patch for PhysX's sleeping, and the rest rule (`VehicleRestSpeed`) does that here.
### Vehicle limits

The limits a script's vehicle parameters are held to are `[Jolt]` keys, read once when the region
starts, so a vehicle maker can tune them. Each default is the value the module always had, except
the motor decay cap, which is Second Life's documented 120 s under `SecondLife` (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial) and 1000 s under `InWorldz`.

| Key | Default | What it limits |
|---|---|---|
| `VehicleMaxLinearSpeed` | 200 | Linear motor speed a script can set (m/s, each axis) and the motor's result. Second Life documents about 30 (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial). |
| `VehicleReferenceSpeed` | 30 | The forward speed (m/s) at which dynamic banking and angular deflection reach full strength. |
| `VehicleMaxAngularSpeed` | 12.566371 (4 pi) | Angular motor speed a script can set (rad/s) and the angular result: the magnitude of the direction under `SecondLife`, each axis under `InWorldz` (below the table). |
| `VehicleAngularLimiterFloor` | 0.1 | The angular rate limiter's floor, 0 to 1 (below the table). 1 turns the limiter off. |
| `VehicleMinTimescale` | 0.0156 | Shortest timescale a script can set (s), 0.001 to 1. |
| `VehicleMaxTimescale` | 1000 | Longest friction, motor or deflection timescale (s), at most 1000. 1000 s or more is "off" for friction and deflection. |
| `VehicleMaxDecayTimescale` | 120 under `SecondLife`, 1000 under `InWorldz` | Longest motor decay timescale (s), linear and angular. Where the key is set it decides, whatever the behaviour; where it is not set, the behaviour does (the stock configuration leaves it unset). |
| `VehicleMaxHoverTimescale` | 300 | Hover is off at or above this hover timescale (s). |
| `VehicleMaxAttractTimescale` | 500 | The vertical attractor and banking are off at or above this timescale (s). |
| `VehicleMaxMotorOffset` | 100 | Linear motor offset (m, each axis). |
| `VehicleMinHoverHeight`, `VehicleMaxHoverHeight` | -128, 10000 | Hover height range (m). Second Life documents a maximum of 100 (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial). |
| `VehicleSledAssist` | 0.045 | The sled's slope assist: a sled with its nose down is pushed along it at this share of g times the square root of the sine of its pitch (a tenth of it, back down the slope, nose up). 0.045 is what the InWorldz sled code gave at its 15 ms step; it is now the same at any step rate. 0 turns it off. |
| `VehicleContactFriction` | unset | Left unset, each part of a vehicle keeps its own material's friction, which adds to the vehicle's friction timescales while it slides in contact. A number (0 to 10) replaces every part's friction; 0 leaves the timescales as the only friction on a vehicle in contact. |
| `SledContactFriction` | 0 | The contact friction of every part of a sled, whatever its material (0 to 10). Under `VehicleBehaviour = InWorldz` a sled's contacts take the smaller friction of the two surfaces. |
| `BasicForcesBehaviour` | InWorldz | `InWorldz` or `SecondLife`: whose behaviour the basic forces follow (the `PRIM_MATERIAL` preset table, hover, move to target, ground repel and look-at, mouse drags, physical size limits, the 0.977 buoyancy rule and targets across a region border; the list is in the description of the key in `RegionServerDefaults.ini`). |
| `FrictionCombine`, `RestitutionCombine` | Average, Average | How two surfaces combine: `Average`, `GeometricMean`, `Multiply`, `Min` or `Max`. |
| `TerrainFriction`, `TerrainRestitution` | 0.4, 0.05 | The terrain's friction (0 to 255) and restitution (0 to 1). |
| `VehicleRestSpeed` | 0.1 | The rest rule (m/s), see "Parked vehicles sleep" below. 0 turns it off. |
| `VehicleMotorTargetOnUndrivenAxes` | false | `InWorldz` only. Halcyon's linear motor has a rule that its target replaces a lower velocity ("the target ensures forward progress", `VehicleDynamics.cs:2585-2589`), there to get a driven axis going against friction. With the key up (or on an axis it never had a direction on) the sign of the direction is taken as positive, so on such an axis a velocity below the target is pulled up to it and one above is only braked. By default the rule acts on an axis the motor has a direction on, and on one it has had a direction on (which is how a car glides to a stop after the key goes up), and not on an axis it never had one on, where it pulled a car that had left a crest down the slope to twice the motor's speed. `true` is Halcyon's code as written. |
| `VehicleSpikeMitigation` | unset | Whether a vehicle's step cuts the velocity spikes the engine's contacts put into it, as Halcyon's vehicle code did (see "Seams between prims"). Unset: on under `InWorldz`, off under `SecondLife`. `true` or `false` forces it. |
| `BodyMaxLinearSpeed`, `BodyMaxAngularSpeed` | 500, 47.12389 | The physics engine's own cap on every moving body, vehicle or not (Jolt's defaults). Keep it above `VehicleMaxLinearSpeed`. |

**The angular rate limiter** (under both behaviours). When a script sets `VEHICLE_ANGULAR_MOTOR_DIRECTION`, the direction the
motor works on is the script's times the ratio

    s = (angular deflection timescale + 0.001) / (angular deflection efficiency + 0.00001)

when `s` is below 1, but by no less than `VehicleAngularLimiterFloor`. A vehicle whose angular deflection is strong and quick
(efficiency up, timescale down) is turned more slowly by its motor: 0.5 s and 1 gives a direction of 0.5 times what was set, and
anything at 0.1 s or quicker with efficiency 1 gives the floor, 0.1 times. The ratio is taken from the deflection settings at the
moment the direction is set, as InWorldz does (settings made afterwards do not rescale a direction already set), and what the script
set is kept as it was (`VEHICLE_ANGULAR_MOTOR_DIRECTION` reads back unchanged). The formula and the 0.1 floor are the InWorldz
code's (`VehicleDynamics.cs:3107-3116`, read, not run), which says it copies Second Life; Second Life's pages do not document
such a limiter, so there is no Second Life figure to prefer. Second Life does document a cap of 4 pi rad/s on the *magnitude*
of the angular motor: the vehicle tutorial's "Steering the Vehicle" section (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial) caps it at two rotations per second (4 pi
radians/s), and the `VEHICLE_ANGULAR_MOTOR_DIRECTION` page (https://wiki.secondlife.com/wiki/VEHICLE_ANGULAR_MOTOR_DIRECTION) gives the same cap, 4 pi, or 2
revolutions per second in total. Under `SecondLife` a direction longer than
`VehicleMaxAngularSpeed` is scaled down to that length with its direction kept, so <10, 10, 10> is held as <7.26, 7.26, 7.26>;
a direction inside the cap is stored as sent. Under `InWorldz` each axis is held to 4 pi, as InWorldz does
(`VehicleDynamics.cs:394-398`), which lets a diagonal direction reach 4 pi times the square root of 3. The scalar form of the call
(which sets all three axes) and a saved vehicle's direction are held the same way. A diagonal is therefore limited
per axis under `InWorldz` and by its length under `SecondLife`.
None of the type presets (either set) has a ratio below 1, so a vehicle that sets no deflection of its own is not limited.

### Vehicle motors and friction

The linear motor and linear friction act on each axis of the vehicle's frame as one equation, as the
Second Life wiki's vehicle tutorial (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial) describes them:

    dv/dt = g * (M - v) / Tm  -  v / Tf  +  a          g = e^(-s / Td)

`M` is `VEHICLE_LINEAR_MOTOR_DIRECTION`, `Tm` the motor timescale, `Td` the motor decay timescale,
`s` the time since the script last set the motor, `Tf` the friction timescale, and `a` the vehicle's
gravity along the axis (its share: 1 - buoyancy, times the ground factor above). The angular motor
and angular friction follow the same equation on the angular velocity, with no gravity term. What
follows from it:

- Each step is the exact solution of the equation over that step, so a vehicle drives the same
  at any heartbeat or physics step rate.
- The velocity approaches the motor's exponentially from whatever it starts at; a few mm/s at the
  start (a rezzed car settling) make no visible difference.
- Friction acts on every axis all the time, the motor's axis included and the vertical axis
  included. With the motor at full grip a vehicle settles at `M * Tf / (Tf + Tm)`: the test car
  (motor 8 m/s, timescale 1 s, friction 1 s) at about 4 m/s, less the motor's decay between
  key events.
- The motor's grip fades exponentially from each set and is never cut off. A motor set to zero
  brakes toward zero; a motor left to decay stops acting.
- A friction timescale of 1000 s (the largest a script can set) is no friction on that axis.
- `VEHICLE_FLAG_LIMIT_MOTOR_UP` keeps the motor from pushing up; friction's own upward share
  (slowing a fall) stays. A car running down a slope faster than its motor is braked by its motor,
  and that push back up the slope loses its upward part.
- Gravity is inside the equation, so on a slope a vehicle settles where motor, friction and gravity
  balance, `(M / Tm + a) / (1 / Tm + 1 / Tf)` at full grip, at any rate.

### Hover, the vertical attractor, banking and deflection

Each follows the Second Life wiki's vehicle tutorial (https://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial) and `llSetVehicleFloatParam` (https://wiki.secondlife.com/wiki/LlSetVehicleFloatParam), and each is the
exact solution over the step the engine takes:

- Hover is a damped spring on the height error `e`: `e'' = -e / T^2 - 2 eff e' / T`, with
  `VEHICLE_HOVER_TIMESCALE` `T` and `VEHICLE_HOVER_EFFICIENCY` `eff` from bouncy (0) to critically
  damped (1). The hover flags choose terrain, water or the global height as before; with
  `VEHICLE_FLAG_HOVER_GLOBAL_HEIGHT` set the global height wins over `VEHICLE_FLAG_HOVER_WATER_ONLY`
  and `VEHICLE_FLAG_HOVER_TERRAIN_ONLY` set with it (the flag's page: "instead of height above ground or
  water"). With `VEHICLE_FLAG_HOVER_WATER_ONLY` and `VEHICLE_FLAG_HOVER_TERRAIN_ONLY` both set and the global
  flag clear, the vehicle hovers over the higher of the water and the ground, as with neither flag: Second
  Life's pages call the case undefined, and InWorldz reads both flags set as hovering over both. With
  `VEHICLE_FLAG_HOVER_UP_ONLY` hover never pushes down, and buoyancy vanishes above the hover height.
  A vehicle without full buoyancy hovers below its height, where the spring holds its weight.
- The vertical attractor is the same spring on the vehicle's roll and pitch, with
  `VEHICLE_VERTICAL_ATTRACTION_TIMESCALE` and `_EFFICIENCY` (from wobbling, 0, to exponential decay,
  1); off at 500 s. `VEHICLE_FLAG_LIMIT_ROLL_ONLY` unlocks pitch for every type: the attractor then
  corrects roll only, so the vehicle can climb and dive. The roll rate the attractor sets belongs to the
  roll axis it was set about: each step it is put back on the axis the vehicle has by then, so a turn about
  the world's vertical (banking's) cannot leave part of it across that axis as a pitch rate (under both
  behaviours).
- Motor height (`InWorldz` only; Halcyon's `VehicleDynamics.cs:2494-2573`). With `VEHICLE_DISABLE_MOTORS_ABOVE` above 0, about
  eight steps in every sixty-four the vehicle casts a ray straight down its own axis from the lower left edge of its box
  (and, if that finds nothing near, from the lower right), starting a metre up and reaching three times the limit; its own
  parts are not hit. The height is the distance less that metre, or the limit plus a metre when nothing is hit. While the height
  is above the limit the clock counts, and at `VEHICLE_DISABLE_MOTORS_AFTER` the linear and angular motors stop
  acting (friction, the attractor and gravity go on); at or under the limit the clock clears. The clock runs as Halcyon's does (`VehicleDynamics.cs:2564`, kept on purpose): it adds eight steps' time on every step, although the height is measured once in eight, so it runs eight
  times fast and a delay set as 2.5 s, the legacy car's, runs out in about 0.3 s (15 steps at 45 Hz). A script that wants a
  delay of `t` seconds sets `t` times eight. The rule is off for a vehicle
  with hover on, positive buoyancy, or a speed under 0.5 m/s. The box is the root prim's size grown over the linked parts'.
- `VEHICLE_LINEAR_MOTOR_OFFSET` moves the point the linear motor pushes at away from the centre of
  mass, so the motor also turns the vehicle: each step's motor change `dv` is an impulse `m dv` at
  the offset `r`, turning the body by `I^-1 (r x m dv)`. A thrust below the centre of mass pitches
  the nose up, as a rocket's would.
- Banking turns the yaw rate about world z toward a target in proportion to the roll angle and
  `VEHICLE_BANKING_EFFICIENCY` (and, with `VEHICLE_BANKING_MIX` toward 1, the forward speed over
  30 m/s), with `VEHICLE_BANKING_TIMESCALE` as its time constant: `target = -roll * eff * pi * ((1 - mix)
  + mix * speed / 30)`. The roll is the angle about the vehicle's x axis, as the attractor measures it,
  whatever the pitch; upside down it is measured from the nearer side and turns the other way. A
  negative efficiency leans out of the turn. It needs the attractor on.
- With `VEHICLE_FLAG_LIMIT_MOTOR_UP`, banking's strength fades in the air as `e^(-t / T)`, `t` the
  time since the vehicle last touched something and `T` the banking timescale, as `llSetVehicleFlags`
  documents; it is whole again on the step after the vehicle touches something.
- `VEHICLE_FLAG_MOUSELOOK_STEER` and `VEHICLE_FLAG_MOUSELOOK_BANK` aim the angular motor where the
  driver's camera looks (see "Mouselook steering" below).

### Mouselook steering

The simulator hands a vehicle's root the driver's camera once a script takes the seated driver's
controls (`llTakeControls`): `PhysicsActor.TryGetCameraData`, the direction the camera looks. With
`VEHICLE_FLAG_MOUSELOOK_STEER` the angular motor turns the vehicle so that its x axis points that way,
in yaw and pitch. With `VEHICLE_FLAG_MOUSELOOK_BANK` the pitch aims the same way, and the camera's
yaw becomes a roll toward it (a camera to the left rolls the vehicle left), which banking then turns
into a yaw; with both flags set, the bank rule is used.

Each aimed axis of the angular motor asks for `a / Tm`, the turning rate that would close its angle
`a` to the camera in one motor timescale, held to that axis of the script's
`VEHICLE_ANGULAR_MOTOR_DIRECTION`: the flags "only turn the vehicle up to the current
VEHICLE_ANGULAR_MOTOR_DIRECTION", and a zero direction does not turn it. The motor equation acts on
that as on any motor direction, with its timescale and decay. Together with the motor's own lag this
gives a turn with half critical damping: it overshoots the camera by about a sixth of the angle and
settles in a few motor timescales. The axes not aimed keep the script's direction.

The flags act only while the driver is in mouselook (the viewer's mouselook control flag, which the simulator
passes with the camera as `CameraData.MouseLook`). Out of mouselook the vehicle behaves as if the flags were
clear: the angular motor turns it at the script's direction. Second Life's pages do not say what happens
outside mouselook; InWorldz acts on the camera for these flags only in mouselook. With no driver's camera (no
script has taken the controls) the flags change nothing. `VEHICLE_FLAG_CAMERA_DECOUPLED` is a hint for the viewer and does nothing in the engine.

Under `[Jolt] VehicleBehaviour = InWorldz` there is a dead zone, as in InWorldz (`VehicleDynamics.cs:819-844`, read, not run): the
sideways part of the camera's direction in the vehicle's frame, times pi / 2 over the vehicle's mouselook azimuth (pi / 4 in every
preset, and a script sets it with `VEHICLE_MOUSELOOK_AZIMUTH`, 11001), held to -1..1, is compared with pi / 32; while it is no more
than that, which with the preset azimuth is a camera within 2.81 degrees of the heading, the yaw (or, with the bank flag, the roll)
is not aimed at all, and the motor on that axis is zero. The pitch has the same threshold over the altitude range
(`VEHICLE_MOUSELOOK_ALTITUDE`, 11002). Outside the zone InWorldz does not aim the vehicle at the camera: the share is shaped
"into a soft exponential", (e^x - 1) / (e - 1) (6 percent of the way just outside the zone, 39 percent for a camera 15 degrees off,
the whole of it from 30 degrees), and the angular motor is set to that share of the direction the script gave
(`VehicleDynamics.cs:833-856`, read, not run): steering sets (0, pitch share * the script's y, yaw share * the script's z), banking
(-yaw share * the script's x, 0, 0), both set banking stands, and the camera's pitch is not steered under `LIMIT_ROLL_ONLY`.
Each camera step is an ordinary set of the motor, so its decay restarts while the camera is held off the heading, and the angular
rate limiter applies; with the camera inside the zone nothing is set and the motor goes on as it was, fading by its decay.
While the driver is in mouselook with a mouselook flag set a script's own set of the angular motor direction is kept as the
direction the share is taken of and does not move the motor (`VehicleDynamics.cs:478-482`). `VEHICLE_FLAG_MOUSEPOINT_STEER`
and `_BANK` steer and bank from the camera in any view, not only mouselook (`VehicleDynamics.cs:813-816`); a vehicle with either
set is never let to sleep. Measured on the real engine (`VehicleInWorldzMouselookTurnTests`, the car preset, a camera turned 90
degrees, the angular motor 1.5 rad/s held for 16 s): InWorldz turns at the whole rate until the camera is within 30 degrees,
then slower, and does not overshoot (89.99 degrees at most at 45 Hz, 89.3 at one step per heartbeat; 20 degrees short at 1 s, 3
at 2 s, within a degree from 4 s, within half a degree from 9.3 s at 45 Hz, and about 0.7 short once inside the zone at 11 Hz);
Second Life's aim overshoots by 11.4 degrees and is within half a degree from 8.7 s. A bare motor with no angular friction
or decay hunts about a camera 20 degrees off by up to 5 degrees (a period of about 4.5 s), as any rate steered by an offset
does; a car with its own friction settles. InWorldz reads the camera once in eight frames (`VehicleDynamics.cs:796`); here it is read
on every step, which only makes the turn answer the camera sooner by under a fifth of a second. The pitch is taken from where the camera
looks: the vertical part of the camera's forward axis in the vehicle's frame (a driver who looks up pitches the nose up, one who
looks down pitches it down, and a level camera gives none). InWorldz's code reads the z of the camera's sideways axis there
(`VehicleDynamics.cs:808, 820`, `ycam = localUp.Z`), although its own comment calls the value up and down; a camera that is not
rolled keeps that axis level, so the pitch of its steering never acted. That line is taken as a slip and not followed; the dead
zone, the curve, the azimuth and altitude ranges, `LIMIT_ROLL_ONLY` and banking are as InWorldz has them. It is InWorldz's alone
because the engine holds steady without it: measured on the real engine (`VehicleMouselookHuntingTests`, a car driven straight with
the camera on its heading, the camera shaking by 0.2, 0.5 and 1 degree at 15, 30 and 60 updates a second at 45 Hz physics, and at
one step per heartbeat), the heading wobbles by less than the shake itself (a standard deviation of 0.05 to 0.2 degrees, never above
0.6 of the shake, and not growing over 20 s), a held camera leaves it straight to 0.03 degrees, and after a 90 degree turn of the camera
the car overshoots by 11 to 16 degrees and is within half a degree of the camera by 12 s. A dead zone is therefore a matter of feel,
not a cure for hunting.
- Linear deflection turns the velocity toward the vehicle's nose, keeping its speed, the angle between
  them decaying as `e^(-eff t / T)`; with `VEHICLE_FLAG_NO_DEFLECTION_UP` only the horizontal part
  turns. Angular deflection turns the nose toward the velocity the same way, scaled by the speed over
  30 m/s.

### Vehicles handed back from their saved settings

The simulator keeps each vehicle's type, flags, parameters and reference frame (`SOPVehicle`): in the
object's XML in inventory, in a region crossing and in an attachment, and in the region's database. It
hands them back to the engine through `PhysicsActor.SetVehicle` when a vehicle is rezzed from inventory,
loaded with the region, copied, arrives from another region, is dropped as an attachment, or is a phantom
prim whose physics is switched back on (`SceneObjectPart.AddToPhysics`). The module overrides
`SetVehicle`, as ubODE does, and the vehicle gets exactly what was saved:

- the type, and the saved flags in place of the type's;
- every saved parameter, held to the region's vehicle limits as the same value from a script is (a saved
  hover timescale of 1000 s becomes 300 s, `VehicleMaxHoverTimescale`; both turn hover off);
- the reference frame;
- the saved motor directions, with neither motor running until a script sets it, as on ubODE. A car
  saved while it was being driven does not drive off when it is rezzed. The one exception is a region crossing: a
  vehicle whose motor is running when core starts to cross it carries that motor's running state in the record it sends
  (the time on its decay clock, its direction and the velocity its ramp has reached; `PhysicsActor.CaptureVehicleRunningState`,
  the `LMRAGE`, `LMRDIR` and `LMRTGT` elements and their angular `AMR` twins of the record's XML), and the neighbour region
  starts the motor again from there. A held throttle keeps driving through a border, under both `VehicleBehaviour` values. The
  record's running state is dropped once it is taken up, and when a crossing fails.

Its velocity is the one the simulator replays onto it, so a vehicle arriving from another region at speed
keeps it. A vehicle loaded with the region has its velocity zeroed on each step of its first 0.27 s, until
a script sets its linear motor, as one whose script sets the type has.

Physics switched off and on by a script or the build tool keeps a solid prim's actor, and its vehicle
keeps every setting, as on ubODE. A phantom prim's actor is removed when its physics goes off and built
again from the saved settings when it comes back on.

The flag calls act as on ubODE: `llSetVehicleFlags` sets the flags it names, `llRemoveVehicleFlags`
removes them, and removing `-1` removes every flag.

The simulator saves its own type presets, which differ from the documented ones in a few fields. A
vehicle that is handed back has the simulator's value in these fields, where the same vehicle just set up
by its script has the documented one; a script that sets these itself is not affected:

| type | field | simulator's preset | documented preset |
|---|---|---|---|
| sled | hover timescale | 10 s (hover on) | 1000 s (off) |
| sled | angular deflection timescale | 1000 s | 10 s (its efficiency is 0 in both) |
| sled | vertical attraction efficiency | 0 | 1 (its timescale is 1000 s, off, in both) |
| boat | `VEHICLE_FLAG_HOVER_UP_ONLY` | off | on |
| balloon | vertical attraction efficiency | 0 | 1 (its timescale is 1000 s, off, in both) |
| balloon | `VEHICLE_FLAG_LIMIT_ROLL_ONLY`, `VEHICLE_FLAG_HOVER_GLOBAL_HEIGHT` | on | off |

The car and the airplane presets agree in every field.

### Wind and water currents

Under `[Jolt] VehicleBehaviour = InWorldz` a vehicle with `VEHICLE_FLAG_REACT_TO_WIND` (0x20000) or `VEHICLE_FLAG_REACT_TO_CURRENTS`
(0x10000) set is pushed by the region's wind, as InWorldz's is (`VehicleDynamics.cs:863-992`, read, not run). Second Life has
neither, so under `SecondLife` the flags and the two efficiencies are held and nothing reads the wind. Where the wind comes from:
the region's wind module (`IWindModule.WindSpeed` at the vehicle's position, truncated to the metre). The ZephyrWind plugin gives
the water's current below the water, the surface wind above it and the wind aloft over that; the default plugins give one wind
everywhere, which a vehicle then feels as well. A region with no wind module gives no wind, and the vehicle behaves as if the air
were still.

- The wind is read once in eight steps (frame number mod 8) and held between reads.
- It is turned into the vehicle's own frame and each axis is cut once the vehicle moves the way the wind blows faster than the wind
  does (it "tapers off once the vehicle achieves the current wind speed"); what is left is scaled axis by axis by
  `VEHICLE_LINEAR_WIND_EFFICIENCY` (12001, held to -10..10 on each axis) and added to the velocity over the step, as the wind
  times that efficiency times the step: a sailboat with 0.02 in 10 m/s of wind gains 0.2 m/s every second. A push under 0.001
  is dropped.
- `VEHICLE_ANGULAR_WIND_EFFICIENCY` (12002, the same range) turns the vehicle: a wind along its x axis pitches it about y by the
  wind's x part, and one along its y axis rolls it about x by minus the wind's y part, each scaled by that axis's efficiency
  and cut by the same taper against the vehicle's angular velocity. There is no push about z.
- A vehicle that is not moving on an axis (a velocity of exactly 0) gets the whole wind on that axis from either side. Halcyon's
  taper takes the sign of 0 as positive, so there a boat at rest got no push from a wind along its negative axis, nor the roll
  that a wind along +y gives; that is cured here.
- Halcyon reads the water's current for a vehicle below the water that has either flag but pushes only for
  `REACT_TO_WIND`, so `REACT_TO_CURRENTS` alone did nothing there. Here the current flag does what its name says: a vehicle
  below the water is pushed by the current with either flag, and a vehicle above it by the wind with `REACT_TO_WIND` only.
- A vehicle with either flag never sleeps (Halcyon's sleep threshold is 0 for it).
- Physical objects that are not vehicles get no wind: nothing outside the vehicle code reads it in Halcyon, and none does here.
- `VEHICLE_FLAG_BLOCK_INTERFERENCE` has nothing to do with it (see "Known gaps").
- The saved record of a vehicle (`SOPVehicle`) keeps 12001 and 12002 (as optional `LWEFF` and `AWEFF` elements, written only when a
  script set them), so a value a script set survives rez, copy, region load and crossing; a record that holds none gives the
  vehicle its type's own, and under `SecondLife`, which takes neither code, the record's values change nothing. The flags are saved.

### Known gaps against Second Life

These documented behaviours are not simulated:

- `VEHICLE_FLAG_BLOCK_INTERFERENCE` (passengers' attachments cannot push the vehicle): stored, no effect. Halcyon does not
  implement it either: neither Halcyon tree has the flag or the name at all (`VehicleFlags.cs` ends at 0x200 and the InWorldz
  bits), and its wind code has no shield. Its push-blocking half belongs in core's push path and is not built.
- Wind and water currents under Second Life's behaviour: Second Life has none. `InWorldz` has them ("Wind and water currents").

### A vehicle found under the ground

A physical vehicle whose centre is more than 0.5 m under the terrain (edited down while physical, or pushed through) is lifted
back to it by its step. The lift is a speed of 1 m/s plus 3 m/s for each metre it is still under the ground, at most 10 m/s;
it goes on until the centre is at the ground, where it ends and takes off the upward speed it gave. A vehicle that was
rising faster is left as it is. Halcyon's vehicle code adds a velocity change of 1 + 3 |vz| m/s at every update while the
vehicle is under (`VehicleDynamics.cs:668-678`), which feeds on itself (about fourfold per update) and throws a vehicle that
was edited 2 m under the ground past 340 m; the bounded lift is the one change from it. In the test a vehicle edited 2 m under
the terrain comes to rest on it, within 0.1 m, at 32, 45 and 64 Hz with 1 to 4 collision steps.

### Parked vehicles sleep

A vehicle's body may sleep, as any other body does, while nothing in the vehicle would move it: no
motor pulling (never set, faded, or set to zero), hover off or at its height, and the attractor done
or the vehicle resting on something. A script setting a motor or any vehicle parameter wakes it, as
does a collision. A vehicle with `VEHICLE_FLAG_MOUSELOOK_STEER` or `VEHICLE_FLAG_MOUSELOOK_BANK` set
never sleeps, whether or not its driver is in mouselook: a sleeping vehicle would read no camera, and
InWorldz keeps such a vehicle awake for that reason (its sleep threshold is 0, `VehicleDynamics.cs:784-789`,
read, not run); under `InWorldz` a vehicle with `VEHICLE_FLAG_MOUSEPOINT_STEER`, `_BANK`, `VEHICLE_FLAG_REACT_TO_WIND` or
`_REACT_TO_CURRENTS` set never sleeps either (a sailboat, whose preset has both, among them). Second Life's
pages say nothing about sleeping.

Second Life's vehicle friction is a velocity decay with no static part, so without more a car that
comes to rest a fraction of a degree tilted (inside the engine's 2 cm contact allowance) slides along
the tilt at a few centimetres a second. The rest rule holds such a vehicle still: when no motor is
pulling (and hover and the attractor are done), the vehicle moves slower than `VehicleRestSpeed`
(0.1 m/s; turning slower than 0.1 rad/s), and the steady speed the motor-and-friction equation above
gives on each of its axes from its present pose is also slower than that, its velocity is set to zero
each step, and after half a second (the engine's own time before sleep) it is put to sleep. Gravity
along the axis nearest the vertical is taken by what the vehicle rests on. The steady speed is
(g M / Tm + a) / (g / Tm + 1 / Tf): with no motor grip, a Tf. So a sled let go on a slope (forward
friction 30 s) starts, and a car whose forward friction lets it roll down a slope rolls, at the same
speed as without the rule; a car let go with its motor set to zero is braked by the motor's grip and
held on a tilt where the equation gives it under 0.1 m/s. In the harness a car parked after a drive
sleeps 3.6 to 5.1 s after its key at 11, 22.5, 45 and 90 Hz and does not move after coming to rest.
A documented car (forward friction 100 s) with no motor grip is held only within about 0.006 degrees
of level along its nose: on any real slope it rolls, as the equation says it should.

### Tuning a vehicle in the harness

The harness (below) runs the module's own vehicle controller on the real engine with no region, so
a vehicle's parameters can be tried in seconds:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --scenario testcar --rate all --vparam LINEAR_FRICTION_TIMESCALE=1,2,1000 --vparam LINEAR_MOTOR_TIMESCALE=0.5
```

- `--vparam` and `--vflag` apply a script's own parameters and flags after the scenario's, by their
  LSL names, so a script's values can be pasted in.
- `--jolt` sets any `[Jolt]` key for the run, e.g. `--jolt VehiclePresets=legacy` to compare the
  preset sets, or `--jolt VehicleMaxLinearSpeed=400` to try a limit.
- `--rate all` runs 11, 22.5, 45 and 90 Hz heartbeats: a figure that differs between them points to
  something that still depends on the step.
- Each run's summary line gives the speeds, distances, time to rest, peak height, tilt and the time
  the engine let the body sleep; `--out` also writes a CSV per run with one line per heartbeat.

## Console commands

`jolt` acts on the region selected with `change region`; at the root prompt every region answers in
turn, each under a `--- region '<name>' ---` line.

These read and report only, and are always available:

| Command | What it shows |
|---|---|
| `jolt capacity` | Bodies, characters, the pair and contact caps, solver errors, dropped contacts, job pools, the region's waits for its pool and its own lock, the last metrics interval, and buffers |
| `jolt metrics` | Process memory and threads, each region's step time and active bodies, and the last metrics interval's timing (see "Job pools") |
| `jolt terraintest` | Raycasts at points across the region, to check the terrain collision surface |
| `jolt probe <x> <y>` | The terrain collision height at one point |
| `jolt heights <x> <y>` | The collision height, the scene heightmap and the water height at one point |
| `jolt avatarstatus` | Each avatar's physics position against the terrain under it |
| `jolt charframe [secs]` | Turns on a per-frame avatar trace in the log (Debug level) for a while |
| `jolt sitstatus` | Each avatar's sit state against its physics presence |
| `jolt sensortest` | A physics overlap query around the first avatar |
| `jolt raytest` | A physics ray down through the first avatar |
| `jolt reloadcheck` | Physical prims that moved since they were loaded |
| `jolt vehiclestatus` | The vehicle state of every prim that has one |

When a region is removed its physics is torn down and its `jolt` commands stop answering. The console has no
call to take a command back, so they stay listed but do nothing; the other regions' commands are not touched.

### Test commands

The test commands are not part of the Jolt module. They are in a separate, opt-in assembly,
`OpenSim.Region.PhysicsModules.Jolt.Diagnostics.dll`, that the region server does not reference, so
a region server built or published as usual does not contain it. The module's own commands, the
read-only ones above, are all it carries.

To use the test commands:

1. Build the assembly from the same source as the module:
   `dotnet build Source/OpenSim.Region.PhysicsModules.Jolt.Diagnostics -c Release`.
   The file is `OpenSim.Region.PhysicsModules.Jolt.Diagnostics.dll` in that project's
   `bin/Release/net10.0` folder.
2. Copy it into the region server's folder, beside `OpenSim.Region.PhysicsModules.Jolt.dll`. It needs
   no other file. Use the copy built with the module in that folder: the assembly uses the module's
   internal members, so one from another build can fail when a command runs.
3. Set `[Jolt] TestCommands = true` and restart the region server.

`[Jolt] TestCommands = true` then adds commands that rez, move or delete objects and avatars, or
reshape the terrain, to exercise the engine: `jolt linktest`, `unlinktest`, `collidetest`,
`collidelinktest`, `boattest`, `cartest`, `sledtest`, `planetest`, `balloontest`,
`terrainslope`, `terrainhill`, `hilltest`, `rezprims`, `rayprims`, `rezmesh`, `raymesh`,
`rezmeshn`, `droptest`, `dropmesh`, `dropstatus`, `sittest`, `unsit`, `sittarget`, `clearprims`,
and `jolt parity`, which runs drop and boat scenarios through the standard physics surface and
writes their figures to `parity-Jolt.txt` (and `parity-boat-Jolt.txt`), to set beside another
engine's figures for the same scenarios. Like every `jolt` command it exists only in a region on
Jolt. `help Physics` lists each one with a line of help.

They are meant for test regions. `TestCommands` is off by default, and then none of them exists
and the module does not look for the assembly. With `TestCommands = true` and the assembly not in
the module's folder, the module logs one warning line (once, not once per region), registers only the
read-only commands and carries on; an assembly file that cannot be loaded gets one warning line
the same way. `[Startup] JoltAutoDropTest = true`, which drops three boxes in every region at load,
is honoured only when `TestCommands` is on and the assembly is loaded.

## Physics harness

`Tests/JoltPhysicsHarness` is a console tool that runs physics scenarios through the module's own
per-heartbeat step (`Simulate`: the vehicle controllers, the backend step with the avatar step
inside it, and the drains) with no region and no viewer. It needs only a built tree and the native
joltc, so it runs anywhere the Jolt tests run (Windows x64 and Linux x64). Use it to see how a vehicle or
an avatar behaves at a given heartbeat rate, or what a changed parameter does, before trying it in
a region.

Build it, then run it from the repository root:

```
dotnet build Tests/JoltPhysicsHarness -c Release
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --scenario testcar --rate 11,45 --slope 15 --out harness-out
```

| Argument | Meaning |
|---|---|
| `--list` | List the scenarios and the slopes each one uses |
| `--scenario NAME[,NAME..]` or `all` | Scenarios to run (default: all) |
| `--rate HZ[,HZ..]` or `all` | Heartbeat rates; `all` is 11, 22.5, 45 and 90 (default: 11) |
| `--physics-rate HZ[,HZ..]` | `[Jolt] PhysicsStepRate`: physics steps per second inside each heartbeat; 0 is one step per heartbeat and is set as `PhysicsStepRate = 0`, not left to the module's default of 45 (default: 0, or the `JOLT_HARNESS_PHYSICS_RATE` environment variable) |
| `--slope DEG[,DEG..]` | Ramp angles for the scenarios that use one (default: each scenario's own list) |
| `--duration S`, `--hold S` | Seconds simulated, and seconds the drive key is held |
| `--keyrepeat S` | How often a held key re-sends the motor, as a script's control event does (default 0.1) |
| `--startspeed V` | A car's forward speed (m/s) just before its key goes down, which then goes down one heartbeat later |
| `--jolt KEY=VALUE` | A `[Jolt]` setting, as in the region's ini (repeatable) |
| `--vparam NAME=V` or `NAME=X,Y,Z` | A vehicle parameter by its LSL name, applied after the scenario's own, e.g. `LINEAR_FRICTION_TIMESCALE=1,1,1000` (repeatable) |
| `--vflag NAME` or `-NAME` | A vehicle flag set (or removed) after the scenario's own, e.g. `HOVER_UP_ONLY` (repeatable) |
| `--ball M[,M..]`, `--shot-speed V[,V..]` | The `tunnel-` scenarios: the ball's diameter (m, default 0.2) and the speed it is shot at (m/s, default 25); lists run every combination |
| `--no-sim-defaults`, `--no-sim-prims`, `--no-sim-avatars`, `--no-region-mesher`, `--no-warm-up-step`, `--no-shipped-config`, `--legacy-defaults` | Build a run without the simulator's values in one respect, or in all of them. Every run is built as a simulator builds it unless one of these is given (see "How the harness builds a run", below) |
| `--vehicle-restore ROUTE` | Vehicle scenarios: when the setup is done, hand the vehicle back from its saved settings as the simulator does: `rez`, `region-start`, `copy`, `crossing`, `detach` or `physics-off-on` (see "Vehicles handed back from their saved settings", above) |
| `--vehicle-script-again` | After `--vehicle-restore`, the scenario's script makes its vehicle calls again: the same vehicle set up by its script, on a body with the route's history |
| `--out DIR` | Also write a CSV per run (`<scenario>-s<slope>-r<rate>.csv`, with `-p<rate>` added when the physics rate is on) and `summary.csv` there |

Scenarios: `car` (the car type's presets, motor `<8,0,0>` while a key is held, then released),
`testcar` (the same with linear friction `<1,1,1000>`, motor timescale 1 and decay 0.5),
`carturn` (the car with angular motor `<0,0,1>` held with the forward key),
`sled`, `boat`, `airplane` and `balloon` (each type's presets in one basic motion),
`avatar-stand`, `avatar-walk`, `avatar-run`, `avatar-fly` (level, 30 m up), `avatar-jump`, `avatar-platform` (an avatar arriving on a fixed platform
3 m above the ground) and `avatar-platform-drop` (arriving 3 m above it, landing on it), and `drop`
(a 1 m box from 5 m);
`testcar-down` and `car-down` (key held down the ramp), `hover`, `attract-roll` and `attract-pitch`
(one behaviour alone), `park-new`, `park-faded`, `park-drive`, `park-car` and `park-wake` (sleeping), and
`crash-wall`, `crash-box`, `crash-headon` and `crash-drop`, `rollonly` (a car rolled and pitched with
`VEHICLE_FLAG_LIMIT_ROLL_ONLY`) and `motor-offset` (a floating box pushed below its centre of mass),
`bank-turn` (the airplane rolled 20 degrees and pitched 30 degrees nose up, banking into a right turn),
`mouselook-hold` (the same car driving straight with the camera on its heading; `--camera-noise DEG --camera-rate HZ` shakes the camera),
`mouselook-turn` (a car with `VEHICLE_FLAG_MOUSELOOK_STEER` turning to a driver's camera looking north)
and `mouselook-bank` (the airplane with `VEHICLE_FLAG_MOUSELOOK_BANK` rolling and banking toward a camera
60 degrees to its right), and
the phantom and volume-detect scenarios: `vd-walk` and `phantom-walk` (an avatar walking through a fixed
volume-detect or phantom box), `vd-drop`, `phantom-drop` and `phantom-physical-drop` (a box falling through a
slab), `phantom-physical-walk`, the `-on-walk` and `-off-walk` scenarios (the flag switched before the avatar
arrives, on one box or a three-box linkset) and `phantom-physical-toggle` (a resting box or linkset made phantom
and solid again), and the resting-contact scenarios: `rest-platform`, `rest-ground` and `rest-vd` (a box comes to
rest on a platform, on the ground or inside a volume-detect box, falls asleep and is thrown off at 5 s),
`tower-10` (ten stacked boxes) and `rest-no-bounce` (a box of restitution 0 dropped 2 m), and the avatar
scenarios: `avatar-hit-1kg`, `avatar-hit-100kg` and `avatar-hit-10ms` (a box thrown at a standing
avatar), `avatar-walk-1kg` and `avatar-walk-1000kg` (an avatar walking into a box), `avatar-on-box`,
`avatar-box-drop` (a box dropped 5 m onto an avatar's head), `avatar-fall-20m`, `avatar-fly-hit-1kg`,
`avatar-fly-hit-100kg` and `avatar-fly-hit-20ms` (a box thrown at a flying avatar), `avatar-fly-wall` and
`avatar-fly-post` (flying into a fixed wall or a 1 kg post), `avatar-stand-up-inside` (an avatar put at the
middle of a fixed cube), `avatar-sit` (an avatar leaves physics on a physical seat and a box is dropped
where it was), `avatar-linkset-20ms` and `avatar-fly-linkset-20ms` (a 100 kg linkset thrown at 20 m/s at a
standing or flying avatar), and the attachment
scenarios: `avatar-buoyancy-1`, `avatar-buoyancy-half`, `avatar-buoyancy-0` and `avatar-ledge` (an avatar
walking off a 3 m platform with that buoyancy, or none set), `avatar-hover` (hover 3 m set, walked across
level ground or up the ramp, then stopped), `avatar-wall` (walking into a fixed wall) and
`avatar-moving-platform` (standing on a platform kept moving at 2 m/s), and `avatar-force-weight` and
`avatar-force-across` (an avatar in the air under an upward force of its weight, or 2 m/s2 east). These print each
watched part's collision events under their summary line.
The shot scenarios fire a physical ball (`--ball`, `--shot-speed`) at something: `tunnel-wall-1cm` and
`tunnel-wall-10cm` (a fixed wall), `tunnel-box` (a 0.5 m box resting on the ground), `tunnel-ground`
(straight down from 3 m), `tunnel-vd-wall` (through a 1 m volume-detect slab, then at a wall), `tunnel-vd-slab` (through a 0.1 m slab),
`tunnel-phantom-box` and `tunnel-phantom-ball` (a physical phantom box, or a phantom ball at a wall);
`tunneled` is 1 when the ball went through. The summary's extra columns give when
the engine last had a body awake and, for the crashes, the impact, arrival and leaving speeds,
overlap and a pass-through check. The ground is
level at 25 m with water at 20 m; with a slope it rises northward at that angle from y 40 to y 100.

Each run prints one summary line: top speed, the release time and speed, the steady speed, the
distance before and after the release, the time to come to rest (under 0.1 m/s for 1 s), the peak
height above the ground, the largest tilt and the end position. The CSV has one line per heartbeat:
time, position, velocity, speed, tilt and height above the ground. Time is simulated, so a run takes
a fraction of real time, and the same arguments always give the same output.

`--rate` is the heartbeat: how often the module's step is called, as a region calls it every
`[Startup] FrameTime` (11 Hz by default). `--physics-rate` runs the same scenario with physics steps
inside each heartbeat, so the two can be compared, e.g.
`--scenario testcar --slope 15 --rate 11 --physics-rate 0,45`; the summary marks those rows
`<scenario>/p45`. The harness writes nothing unless `--out` is given. The Jolt tests are in `Tests/`, one project per part:
`OpenSim.Region.PhysicsModules.Jolt.Tests` (the module), `.Backend.Tests`, `.Vehicles.Tests`, `.Diagnostics.Tests`,
`.Harness.Tests` (the harness itself, `HarnessTests`) and `.Scenarios.Avatar.Tests`, `.Scenarios.Prim.Tests` and
`.Scenarios.Vehicle.Tests`, which run the harness's scenarios as regression tests. Helpers more than one of them uses are in
`Tests/JoltTestShared` and are compiled into each project that needs them; only the harness and the three scenario
projects depend on the harness.

### How the harness builds a run

A run is built the way a simulator builds a region, so that a run that passes in the harness is not one a
region would fail. Each way below has a switch that turns it off, for comparing a run with and without it.

- **Prims and avatars get the values the simulator hands the engine** (`--no-sim-prims`,
  `--no-sim-avatars`, `--no-sim-defaults` for both). `SceneObjectPart.AddToPhysics` gives every new actor
  its material (a new prim is wood: friction 0.6, restitution 0.5), density 1000 on every prim, fixed or
  not, gravity multiplier, friction, restitution and buoyancy, and a root no rotation locks. An avatar is
  0.45 x 0.6 x 2.1 m, the box the simulator hands `AddAvatar` for the default appearance
  (`AvatarAppearance.SetSize` adds 0.2 m to the height), subscribed to collisions every 100 ms. A prim
  that is phantom from the start is passed to `AddPrimShape` as phantom, as `AddToPhysics` does. Two
  restitutions in a contact multiply and the terrain's is 0, so a bounce shows only between two prims: a
  ball shot at a fixed wall comes back off it. A vehicle sets its own contact friction and restitution 0
  while it is one, so no vehicle scenario changes. An avatar stands 0.1 m higher.
- **The scene has the region's mesher** (`--no-region-mesher`). A region server loads each module in an
  assembly load context of its own, so the `Meshmerizer` it hands the physics module is a second copy of the
  class the module was built against, and a cast to that class is false there. The harness loads the
  Meshmerizer assembly in a load context of its own, as a region does, and every other assembly (`SharedBase`,
  where `IMesher` and `IMesherShapeData` live) comes from the default context. A test that makes its mesher
  with `new Meshmerizer()` has the module's own class and cannot show that difference.
- **The setup runs after the first step** (`--no-warm-up-step`). The harness steps the empty scene once
  before a scenario's setup. The region-load window is the time before the first step (`IsRegionLoading`): a
  prim loaded from storage is added in it, a rezzed prim and an arriving avatar after it. In the window a
  body does not wake, a restored body's horizontal velocity is dropped, and a vehicle restored from its saved
  settings keeps zero velocity for its first frames. The region-start vehicle restore
  (`--vehicle-restore region-start`) is the load, so it keeps the window.
- **The `[Jolt]` settings start from `RegionServerDefaults.ini`** (`--no-shipped-config`), which every
  simulator reads before its own ini. The harness project copies the file next to the harness. A test
  holds its `[Jolt]` section equal to the code's own defaults, so the section and the code cannot drift apart
  unseen. `--jolt` keys are applied after it.

`--legacy-defaults` turns all of the above off.

What the harness still builds differently, and why:

- **The heartbeat and the step rate.** The harness calls `Simulate` at exactly the rate it is asked for
  (`--rate`, the scan axis), with one physics step per heartbeat unless `--physics-rate` is given. A region
  calls it at `[Startup] FrameTime` with the time that really passed, and the module's own step rate default is
  45 Hz. `--rate 11 --physics-rate 45` is the region's setting. The vehicle controller's clock is the harness's
  own simulated time, and so is the ray cast clock when a test passes one, so a run gives the same rows on
  every machine.
- **Assets.** The scene has no asset service (`InitialiseWithoutScene` passes no asset request), so a mesh or
  sculpt prim must carry its data in the shape, as the scenarios and tests that use one do.
- **Terrain.** The same `SetTerrain` and water-level calls a region makes, from a flat or ramped heightmap
  of a 256 m region; terrain edits while a region runs are not modelled.
- **Threads.** A region adds, removes and changes prims from script and network threads while the heartbeat
  runs. The harness does everything on one thread.
- A vehicle is made as a script makes one (`llSetVehicleType`, then its parameters).
  `--vehicle-restore` hands it back from its saved settings instead, through `PhysicsActor.SetVehicle`
  as the simulator does. Every route but `physics-off-on` removes the actor and builds a new one, before
  the first step, so a route compares with a vehicle set up by its script on a body with the same
  history (`--vehicle-script-again`): the engine moves a body it is given after another was removed
  slightly differently once it slows to rest, whatever the body is (the test car on 5 degrees is up to
  0.14 m from where the first body is at the same time).

### The regression check

The regression check covers the runs listed in `Tests/JoltPhysicsHarness/ci/runs.txt`: each is run
with an `--out` folder of its own, and each run's `summary.csv` is then compared with a recorded
baseline. The runs in `ci/traced-runs.txt` (the ten-box tower at three solver settings) are run the
same way and their traces are kept, under `traced/`, without being compared. The repository's own
workflows do not run this check; it is run by hand:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --check-baseline <folder of the runs> --baselines Tests/JoltPhysicsHarness/ci/baselines
```

The baseline depends on the joltc build that was loaded, not on the operating system: the stock
files are built without Jolt's `CROSS_PLATFORM_DETERMINISTIC` option, so the stock Windows x64 file
gives different results from the stock Linux x64 file. Each `--out` folder holds `native.txt`,
naming the build the run loaded, and the check reads `ci/baselines/stock-<version>-<rid>/` for that
file. It leaves out `ray_us_per_cast`, which is timed on the wall clock. A build with
no baseline (the stock Arm64 and macOS files, a file the module has no record of, or a new package
version not yet recorded) is reported as `SKIPPED: no baseline for this native` with exit code 3,
neither passed nor failed. Exit 0 is passed and 1 is failed. To record a build's baseline from a set
of runs:

```
dotnet Tests/JoltPhysicsHarness/bin/Release/net10.0/JoltPhysicsHarness.dll --record-baseline <folder of the runs> --baselines Tests/JoltPhysicsHarness/ci/baselines
```

Record only runs that give the same output run after run on one machine; `--leave-out RUN[,RUN..]`
skips the others, and the check then reports those runs as skipped.

`--pool-bench` runs the job pool benchmark instead of the scenarios: a heavy scene (a pile of
`--heavy-boxes` boxes, default 300, kept moving, or with `--heavy-car` the test car driving with its
key held) and `--light` light scenes (bare ground, default 2) in one process, each heartbeat on its
own thread in real time at the first `--rate`, with `--physics-rate` physics steps per second
(default 45 here). It runs each combination of `--threads N[,N..]` (`ThreadCount`, default 0: the
key left unset), `--pools N[,N..]` (`JobPools`, default 1) and `--handoff off|on[,..]`
(`JobPoolFairHandoff`, default off) for `--seconds` (default 20, after a 2 s warm-up) and prints one
line each: the light scenes' pool waits (count, average, longest, and per heartbeat), the heavy
scene's heartbeat time (average and longest, its own pool waits included) and pool wait per
heartbeat, physics steps per second over all scenes, heartbeats that started more than a heartbeat
late, the job threads and threads per pool, and the heavy scene's average active bodies.
`--heavy-boxes 0 --light 1` times one bare scene on its own. `--heavy-scenes N` runs N heavy scenes
(default 1); the heavy columns then cover them together, and with `--out` the file
`pool-bench-scenes.csv` gives each scene's pool, heartbeat time and pool waits. `--unpaced` runs the
heartbeats back to back. Unlike the scenarios its figures are timings, so they depend on the machine
and its load; with `--out` it writes `pool-bench.csv`.
