# Tranquillity 1.0 — Release Notes (Release Candidate)

**Previous release:** `tranquillity-0.9.3.9333` (2025-07-28)
**This release:** `1.0` (Release Candidate)

This is a large modernization release. The bulk of the work moves Tranquillity
onto current .NET/C# platform patterns: a new hosted-service startup model,
SkiaSharp-based imaging, a plugin architecture, NuGet-hosted LibOMV, and a fully
reorganized solution layout. Alongside the platform work are new scripting
functions, updated constants, and a number of stability fixes.

> Note: An intermediate tag `tranquillity-0.9.3.9441` (2025-12-16) exists between
> the previous release and this candidate. These notes cover the full range of
> changes since `9333`.

---

## Highlights

- **Hosted-service startup model** — servers now bootstrap through the .NET
  Generic Host / ASP.NET Core controller pipeline (MoneyService is the first
  service fully migrated).
- **SkiaSharp imaging** — `System.Drawing`/OpenJPEG removed in favor of SkiaSharp
  and CoreJ2K for all texture, sculpt, map, TIFF, and JPEG2000 handling.
- **Plugin architecture** — region and application modules load through the
  updated DotNetCorePlugins model. Mono.Addins is no longer supported.
- **LibOMV via NuGet** — OpenMetaverse libraries are now consumed from a GitHub
  NuGet feed and referenced once at the solution level.
- **Solution restructure** — every project now lives under `Source/`, `Tests/`,
  or `Addons/` with folder names matching their namespaces.
- **Experimental WebRTC voice** — the `os-webrtc-janus` module (contributed by
  Robert Adams) plus STUN-server advertisement to viewers.
- **New scripting functions & 2025 constants** — see the API section below.

---

## Platform & Architecture

- Introduced the hosted-services architecture and startup pipeline (#176); ported
  the MoneyService to ASP.NET Core controllers (#178 region-server load fix).
- Restructured the solution so all projects live under `Source/`, `Tests/`, or
  `Addons/`, with directories matching namespaces (#143).
- Added plugin support and the removed the Mono.Addins integration (#172, #145).
- Adopted Nerdbank.GitVersioning (NBGV) for versioning; bumped version to `1.0`
  and simplified CI to build-and-test on push.
- Removed SmartThreadPool; threading now defaults to the system thread pool
  (INI threading defaults updated accordingly).
- Simplified `DoubleDictionaryThreadAbortSafe` / `ReaderWriterLockSlim` usage now
  that `Thread.Abort` no longer exists on .NET.
- Added Docker support (initial `Dockerfile`, `compose` files) and per-platform
  publish output (linux-x64 / win-x64).
- Added startup script templates and platform-tagged publish directories so
  native SkiaSharp/SQLite dependencies are copied correctly.

## Imaging & Graphics (SkiaSharp / CoreJ2K)

- Reworked imaging onto SkiaSharp, replacing `System.Drawing` (#130, #167).
- Replaced OpenJPEG (not .NET-compatible) with CoreJ2K for JPEG2000 decode/encode
  across VectorRender, MapImageModule, and sculpt handling.
- Updated CSJ2K/CoreJ2K to 2.1.0; VectorRender and MapImageModule now generate
  J2K output.
- Cleaned up sculpt-map decode in both BulletSim and ubODE meshmerizers to use
  the CoreJ2K + Skia APIs correctly (`SKImage` instead of `SKBitmap`).
- Added defensive texture handling (validate asset/stream before conversion).
- Extended the TIFF loader up to 32-bit float formats.
- Fixed a MapImageService startup crash when decoding the default water image.

## Physics

- Standardized PhysicsModule naming (case-sensitive on Linux); ODE is now
  consistently `ubODE` across the mesher and physics module.
- Fixed BulletSim and ubODE addin discovery/loading (`OpenSim.Framework` dependency, 
  `ubOdeMeshing` reference).

## Data & Persistence

- Updated the EF Core data-model classes and added default initialization to the
  core data-model types (#132, #133).
- Updated SQLite to `System.Data.Sqlite` 2.0.2 (native 3.50.4.5); removed the
  obsolete `Mono.Data.SqliteClient`.
- Fixed SQLite migration typos and profile migration numbering (the migration
  adding the `usersettings` table).
- Several PostgreSQL (PGSQL) adjustments.
- Removed remaining `BinaryFormatter` usage.

## Networking & Services

- Honor HTTP client stream timeouts during XML deserialization to avoid buffering
  entire responses into memory.
- Split user-profile request queues into local vs. HG; added a
  `profiles status` console command to inspect queue sizes.
- Set TCP `NoDelay` inside a guarded try/catch.
- Added extra validation around `ServiceURLs` / `AssetServiceURI`.

## Voice / WebRTC (experimental)

- Added the experimental `os-webrtc-janus` module (BSD-licensed, contributed by
  Robert Adams).
- Added a configurable STUN-server list in `OpenSim.ini`, advertised to viewers
  via the Simulator Features cap.
- The WebRTC INI file ships as `*.example` (limited-audience feature).

## Dependencies

- LibOMV/OpenMetaverse now consumed from a GitHub NuGet feed and referenced once
  at the solution level; per-project references removed.
- Multiple NuGet dependency refreshes, including a MimeKit security update.
- Direct assembly references de-privatized so they propagate through the
  build/publish output path.

## Bug Fixes & Stability

- Fixed a double newline in region-server console output (log4net template vs.
  `ConsoleAppender`).
- Fixed the experience-permission blocked check in the LSL API (#149).
- Bounds fix in `llInsertString`; null-ref hardening in `parseString2List`.
- Restored a needed `ParentGroup` null check on load; avoided unnecessary SOG
  saves; fixed inventory-save flagging on additional unlink cases (mantis 9230).
- Additional mantis fixes: 9218, 9219.
- Added YEngine state-load failure instrumentation to inform the phase-2 state
  migration work.

---

## API Changes

### New LSL functions

#### `llSetRenderMaterial(string material, integer face)`
Assigns a render material (GLTF PBR material asset, referenced by name or key) to
a face of the prim, or to all faces with `ALL_SIDES`. Passing the null key
(`NULL_KEY` / `"00000000-0000-0000-0000-000000000000"`) clears any material
override on the given face. Requires the materials module; if the material name
cannot be resolved a script error is raised.

### New `llTransferOwnership` constants

Constants added to support `llTransferOwnership` scripting:

| Constant | Value | Meaning |
| --- | --- | --- |
| `TRANSFER_FLAG_RESERVED` | 1 | Reserved flag |
| `TRANSFER_FLAG_TAKE` | 2 | Transfer via take |
| `TRANSFER_FLAG_COPY` | 4 | Transfer via copy |
| `TRANSFER_OK` | 0 | Transfer succeeded |
| `TRANSFER_BAD_OPTS` | -1 | Invalid options |
| `TRANSFER_NO_TARGET` | -2 | No target avatar/agent |
| `TRANSFER_THROTTLE` | -3 | Rate limited |
| `TRANSFER_NO_ITEMS` | -4 | Nothing to transfer |
| `TRANSFER_BAD_ROOT` | -5 | Invalid root |
| `TRANSFER_NO_PERMS` | -6 | Insufficient permissions |
| `TRANSFER_NO_ATTACHMENT` | -7 | Item is not an attachment |

### New click-action constant

- `CLICK_ACTION_IGNORE = 9` — companion to the existing `CLICK_ACTION_*` set
  (e.g. `CLICK_ACTION_ZOOM = 7`, `CLICK_ACTION_DISABLED = 8`).

### Updated constant set

- LSL/OSSL constants refreshed to the 2025 set to match current viewer/protocol
  definitions.

### New OSSL functions

#### `float osPerlinNoise2D(float x, float y, integer octaves, float persistence)`
Returns 2D Perlin noise sampled at `(x, y)` using the given number of `octaves`
and `persistence`, delegating to the same terrain noise generator used by the
terrain subsystem. No special OSSL threat level is required.

#### `osTriggerSoundAtPos(string sound, vector position, float gain)`
Triggers a non-looped sound (by inventory name or asset key) at an absolute
region `position` with the given `gain`, without requiring the sound to be
attached to the emitting prim. No special OSSL threat level is required.

### OSSL NPC behavior change

- `osNpcPlayAnimation` can now play **any** animation from the asset service when
  a UUID is supplied, instead of being limited to the default built-in
  animations.

### Internal scripting refactors (no script-visible signature change)

- The shared ScriptEngine modules were reorganized into namespace-matching
  directories, fixing a startup issue in the prior release-in-prep (#134).
- The LSL API was reorganized and the API Manager now builds its list of valid
  APIs with LINQ instead of by hand (#129, #125, #126).

---

## New Features (Documentation)

### Hosted-service startup

Services are transitioning to the .NET Generic Host with ASP.NET Core
controllers. MoneyService is the first fully migrated example. See
`Docs/HostedServiceArchitecture.md`,
`Docs/HostedServiceStartupShutdownSequence.md`, and
`Docs/HostedServiceMigrationPlan.md` for the architecture, sequencing, and the
remaining migration backlog.

### Plugin model

Region and application modules load through the updated DotNetCorePlugins 
pipeline. Root nodes under `/OpenSim/Startup` were updated to use the new 
assembly names. See `Docs/PLUGIN_DEVELOPMENT.md` and
`Docs/PLUGIN_MIGRATION_PLAN.md` for authoring and registration steps.

### WebRTC voice (experimental)

To try the experimental Janus-backed WebRTC voice module:

1. Enable the `os-webrtc-janus` addon module.
2. Copy the shipped WebRTC `*.example` INI to an active INI and configure your
   Janus endpoint.
3. Add a STUN-server list to `OpenSim.ini`; the simulator advertises it to
   viewers through the Simulator Features cap.

This feature is experimental and intended for a limited audience at this time.

### Docker / container builds

Initial container support ships via a `Dockerfile` and `compose` files. Images
can be built through the provided VS Code Docker tasks or `docker compose build`.
Container packaging will move to the standard `dotnet publish` container flow as
the repo layout stabilizes.

---

## Upgrade Notes

- **Publish is now platform-specific.** Use the platform-tagged publish output so
  native SkiaSharp and SQLite libraries are copied for the target OS
  (linux-x64 / win-x64 / linux-arm64).
- **Threading defaults changed.** SmartThreadPool has been removed; verify any
  custom threading INI settings against the new system-thread-pool defaults.
- **LibOMV comes from NuGet.** Building the develop branch requires access to the
  private/GitHub NuGet feed — see `Docs/BUILDING.md` for feed setup.
- **PhysicsModule names are case-sensitive on Linux.** Ensure INI references use
  the standardized `ubODE` naming.
