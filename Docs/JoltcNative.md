<!--
  Copyright (c) 2026 Legion Builds

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
-->

# joltc - the Jolt module's native

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) runs on joltc, the C API over
Jolt Physics that JoltPhysicsSharp binds to. The binding is JoltPhysicsSharp 2.22.0, which depends
on `JoltPhysics.Native` 1.1.0 (Jolt 5.6). Linux x64 loads a deterministic SSE2 rebuild of joltc
886e0886 and Jolt e77f1755 (`CROSS_PLATFORM_DETERMINISTIC`), copied over the package's linux-x64
file. That rebuild is its own origin, not the package file. The other platforms load the package's
own files, which are not built with that option. Windows is one of those package files: this
project does not test it, and a deterministic Windows build is a follow-up, not part of this
record. A build puts
the file for each platform in the output under `runtimes/<rid>/native/`, and the module checks the
file's hash against its record (`JoltNative.Known`) before loading it.

All regions in a process share one job pool on this native, and `[Jolt] JobPools` above 1 is
reduced to one pool at start with a warning. The physics step uses an allocator of its own.
`CharacterVirtual`'s scratch calls still share one process-wide `TempAllocatorImpl`, a LIFO stack
that is not thread-safe: two steps at once on two pools would interleave their frames on it and
Jolt would stop the process with `TempAllocator: Freeing in the wrong order`. On one pool that
cannot happen, because every use of the shared allocator is inside a step, under the pool's gate
(`ShapeAndAllocatorRuleTests` checks this). More pools need the character calls to take an
allocator too.

## Files

| File | What |
|---|---|
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.cs` | Picks the file for the running platform, checks its hash against the table below and loads it |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.targets` | Keeps the package's unused files (`joltc_double.dll`, Android) out of an application's output, then copies the deterministic linux-x64 library over the package file |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/native/linux-x64/libjoltc.so` | That deterministic library. `build-deterministic.sh` and `glibc-fmodf-symver.h` beside it rebuild it; `PROVENANCE.txt` records the toolchain and the hash |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/assert-joltc-native.ps1` | Checks that an output or publish directory holds recorded joltc builds under `runtimes/<rid>/native/` and no other joltc |

The `JoltPhysics.Native` package reaches every application that carries the module (the region
server, the Jolt tests, the Jolt harness) through the backend's package reference. A build with
no runtime identifier puts each platform's file under `runtimes/<rid>/native/` (macOS:
`runtimes/osx/native/`, one file for both architectures); a build or publish for one runtime
identifier puts that platform's file beside the application's assemblies. At start the module
loads the file for the platform it runs on (`JoltNative`), from `runtimes/` first, so a portable
build works on each supported platform.

## Package files

These are the bytes of `JoltPhysics.Native` 1.1.0, including that package's own linux-x64 file.
`JoltNativePackageTests` hashes each of them in the restored package. None of these is the file
linux-x64 loads.

| Package | File | SHA-256 |
|---|---|---|
| `JoltPhysics.Native 1.1.0` | `win-x64/joltc.dll` | `79125ab6882a1aeeecd5509bfe9edf383e8e48a34d9a8b642a6b9a197d44df74` |
| `JoltPhysics.Native 1.1.0` | `linux-x64/libjoltc.so` | `3800cc6fff9e5470c524b15e8895599f90e9b96129b3f5cf9f76504beda178a3` |
| `JoltPhysics.Native 1.1.0` | `win-arm64/joltc.dll` | `d32b29b9aadfd7887e4681302a766c5476a196c6ad77ff1f86473bbfb47325e0` |
| `JoltPhysics.Native 1.1.0` | `linux-arm64/libjoltc.so` | `daf06442fdf25bd2fc4234c252e3862ca3a94e32792a4466a9d93723c8bb03cb` |
| `JoltPhysics.Native 1.1.0` | `osx/libjoltc.dylib` | `386dbeec5e6892c218f9692572528ee3af748184c9298861c90aac1e3443c2ec` |

## The loaded files

The hash the module checks is the file it loads, not a version number. linux-x64 is the
deterministic SSE2 rebuild (single precision, asserts off, debug renderer off, floating-point
exceptions off, profiler off, no GPU compute). The build copies it over the package file. The
other four rows are the package files above. None is safe for more than one job pool. This
project's tests and harness run on the linux-x64 rebuild. The other platforms are recorded and not
tested here, and the module logs a notice saying so when it starts on one. Windows x64 stays the
package's non-deterministic file; a deterministic Windows build is a follow-up and is not in this
change.

The rebuild is compiled with the debug renderer off, so it does not export the 35
`JPH_DebugRenderer_*`, `JPH_BodyDrawFilter_*`, `JPH_PhysicsSystem_Draw*` and `JPH_Shape_Draw`
entry points that the package file has. The module does not call them. A binding call to one of
them on this file throws `EntryPointNotFoundException`.

| What loads | File | SHA-256 |
|---|---|---|
| `deterministic rebuild, joltc 886e0886, Jolt e77f1755, SSE2` | `linux-x64/libjoltc.so` | `27efc48c813e75294da11eaf22618bc1e0a5426a50edde7870ef4a60c8e4288b` |
| `JoltPhysics.Native 1.1.0 package file, not tested here` | `win-x64/joltc.dll` | `79125ab6882a1aeeecd5509bfe9edf383e8e48a34d9a8b642a6b9a197d44df74` |
| `JoltPhysics.Native 1.1.0 package file, not tested here` | `win-arm64/joltc.dll` | `d32b29b9aadfd7887e4681302a766c5476a196c6ad77ff1f86473bbfb47325e0` |
| `JoltPhysics.Native 1.1.0 package file, not tested here` | `linux-arm64/libjoltc.so` | `daf06442fdf25bd2fc4234c252e3862ca3a94e32792a4466a9d93723c8bb03cb` |
| `JoltPhysics.Native 1.1.0 package file, not tested here` | `osx/libjoltc.dylib` | `386dbeec5e6892c218f9692572528ee3af748184c9298861c90aac1e3443c2ec` |

## Check a file

Against the table above:

```
sha256sum joltc.dll
```

PowerShell: `Get-FileHash joltc.dll -Algorithm SHA256`.

The module loads a file with another hash only with `[Jolt] AllowUnrecordedNative = true`, and runs
one job pool on it, since it cannot tell from the hash that the file has an allocator per physics
system.

## Licences

- joltc: MIT, Copyright (c) Amer Koleci and Contributors. `ThirdPartyLicenses/joltc.txt`.
- Jolt Physics (compiled into the native): MIT, Copyright 2021 Jorrit Rouwe.
  `ThirdPartyLicenses/JoltPhysics.txt`.
- JoltPhysicsSharp, the managed binding: MIT, Copyright (c) Amer Koleci and Contributors.
  `ThirdPartyLicenses/JoltPhysicsSharp.txt`.
