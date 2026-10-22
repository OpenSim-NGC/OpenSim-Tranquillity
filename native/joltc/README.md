<!--
  Copyright (c) 2026 Legion Builds

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at https://mozilla.org/MPL/2.0/.
-->

# joltc - the Jolt module's native

The Jolt physics module (`Source/OpenSim.Region.PhysicsModules.Jolt`) runs on joltc, the C API over
Jolt Physics that JoltPhysicsSharp binds to. It uses the stock joltc of the `JoltPhysics.Native`
package that JoltPhysicsSharp 2.19.1 depends on, on Windows x64 and Linux x64. A build puts the
package's files in the output under `runtimes/<rid>/native/`, and the module checks the file's hash
against its record (`JoltNative.Known`) before loading it.

All regions in a process share one job pool on this native, and `[Jolt] JobPools` above 1 is
reduced to one pool at start with a warning. Joltc gives every physics system the same
process-wide `TempAllocatorImpl`, a LIFO stack that is not thread-safe: two physics updates at once
on two pools would interleave their frames on it and Jolt would stop the process with
`TempAllocator: Freeing in the wrong order`. On one pool that cannot happen, because every use of
the allocator is inside a physics update, under the pool's gate (`ShapeAndAllocatorRuleTests`
checks this). More pools need a change in joltc itself.

## Files

| File | What |
|---|---|
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.cs` | Picks the file for the running platform, checks its hash against the table below and loads it |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.targets` | Keeps the package's unused files (`joltc_double.dll`, Android) out of an application's output |
| `Source/OpenSim.Region.PhysicsModules.Jolt.Backend/assert-joltc-native.ps1` | Checks that an output or publish directory holds recorded joltc builds under `runtimes/<rid>/native/` and no other joltc |

The `JoltPhysics.Native` package reaches every application that carries the module (the region
server, the Jolt tests, the Jolt harness) through the backend's package reference. A build with
no runtime identifier puts each platform's file under `runtimes/<rid>/native/` (macOS:
`runtimes/osx/native/`, one file for both architectures); a build or publish for one runtime
identifier puts that platform's file beside the application's assemblies. At start the module
loads the file for the platform it runs on (`JoltNative`), from `runtimes/` first, so a portable
build works on each supported platform.

## The stock files

The files of the `JoltPhysics.Native` package the projects use, read from the package. All are
recorded as not safe for more than one job pool. This project's tests and harness run on win-x64
and linux-x64; the other platforms are recorded but not tested here, and the module logs a notice
saying so when it starts on one. `JoltNativePackageTests` reads the restored package and fails
when its files differ from this record, for example after a version bump, listing what to check
before the new hashes are recorded.

| Package | File | SHA-256 |
|---|---|---|
| `JoltPhysics.Native 1.0.4` | `win-x64/joltc.dll` | `67becfc70cfbda643ab9b75aba895042900c3e339b001080ba4107e4929b0910` |
| `JoltPhysics.Native 1.0.4` | `linux-x64/libjoltc.so` | `5fc051708bdd05031a816796612a2f87e17ed32cc198f195a43b2305b3d990fd` |
| `JoltPhysics.Native 1.0.4` | `win-arm64/joltc.dll` | `b0a7d05151a8e504765a39e23b2ec196b88b3bf2ce3e8b884161e6925e7578e4` |
| `JoltPhysics.Native 1.0.4` | `linux-arm64/libjoltc.so` | `fde70508c826370b5cf6bb54c6ea9ceeab0c37c826bcd71579ebb9b4ac3e0d61` |
| `JoltPhysics.Native 1.0.4` | `osx/libjoltc.dylib` | `39e4a8728307e48026d965d8ab661348a8808a9a2afddde6ce58272b37d83e91` |

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
