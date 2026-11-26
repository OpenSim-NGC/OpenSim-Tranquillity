<!--
  Copyright (c) 2026 Legion Builds

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
-->

# joltc - the Jolt module's native

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) runs on joltc, the C API over
Jolt Physics that JoltPhysicsSharp binds to. The binding is JoltPhysicsSharp 2.22.0, which depends
on `JoltPhysics.Native` 1.1.0 (Jolt 5.6). Linux x64 loads a joltc built from commit 886e0886 and
Jolt e77f1755 with `CROSS_PLATFORM_DETERMINISTIC`, copied over the package's linux-x64 file. The
other platforms load that package's own files, which are not built with that option. A build puts
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
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/native/linux-x64/libjoltc.so` | That deterministic library, with `native/linux-x64/PROVENANCE.txt` |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/assert-joltc-native.ps1` | Checks that an output or publish directory holds recorded joltc builds under `runtimes/<rid>/native/` and no other joltc |

The `JoltPhysics.Native` package reaches every application that carries the module (the region
server, the Jolt tests, the Jolt harness) through the backend's package reference. A build with
no runtime identifier puts each platform's file under `runtimes/<rid>/native/` (macOS:
`runtimes/osx/native/`, one file for both architectures); a build or publish for one runtime
identifier puts that platform's file beside the application's assemblies. At start the module
loads the file for the platform it runs on (`JoltNative`), from `runtimes/` first, so a portable
build works on each supported platform.

## The loaded files

The hash the module checks is the file it loads, not a version number. linux-x64 is the
deterministic rebuild described above (single precision, asserts off, debug renderer off,
floating-point exceptions off, profiler off, no GPU compute). Its package file is still restored,
and the build replaces it in the output. The other four rows are the unmodified files of
`JoltPhysics.Native` 1.1.0. None is safe for more than one job pool. This project's tests and
harness run on linux-x64; the other platforms are recorded but not tested here, and the module
logs a notice saying so when it starts on one. `JoltNativePackageTests` reads the restored package
and the linux-x64 replacement, and fails when they differ from this record.

| Package | File | SHA-256 |
|---|---|---|
| `JoltPhysics.Native 1.1.0` | `win-x64/joltc.dll` | `79125ab6882a1aeeecd5509bfe9edf383e8e48a34d9a8b642a6b9a197d44df74` |
| `JoltPhysics.Native 1.1.0` | `linux-x64/libjoltc.so` | `ed9b88b77c8f27ef035bd9fd809af7a425757e5b8c37d11ec4527374a06dba59` |
| `JoltPhysics.Native 1.1.0` | `win-arm64/joltc.dll` | `d32b29b9aadfd7887e4681302a766c5476a196c6ad77ff1f86473bbfb47325e0` |
| `JoltPhysics.Native 1.1.0` | `linux-arm64/libjoltc.so` | `daf06442fdf25bd2fc4234c252e3862ca3a94e32792a4466a9d93723c8bb03cb` |
| `JoltPhysics.Native 1.1.0` | `osx/libjoltc.dylib` | `386dbeec5e6892c218f9692572528ee3af748184c9298861c90aac1e3443c2ec` |

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
