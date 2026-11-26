#!/usr/bin/env bash
# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

# Rebuild native/linux-x64/libjoltc.so from the pinned joltc and Jolt commits.
#
#   build-deterministic.sh <scratch-dir> [joltc-src] [jolt-src]
#
# The scratch directory is created if needed and holds the CMake build (and, when the
# source paths are omitted, the two checkouts). Nothing under it is part of this
# repository. The stripped library is written to <scratch-dir>/libjoltc.so and its
# SHA-256 is printed. The committed file is that output; PROVENANCE.txt records the
# toolchain that produced its bytes.
#
# joltc's CMakeLists.txt caches CMAKE_OSX_ARCHITECTURES as "x86_64;arm64". That value
# matches "arm64", and Jolt then skips every x86 instruction-set flag. The configure
# step below forces the cache empty so the SSE2 flags are actually applied. A stale
# build directory is removed first, because a cached architecture is not overwritten.

set -euo pipefail

ROOT=$(cd "$(dirname "$0")" && pwd)
SYMVER="$ROOT/glibc-fmodf-symver.h"
JOLTC_COMMIT=886e088675bae3a086f8318c7803f8ee962c2f2c
JOLT_COMMIT=e77f175595e64cb44218cc9d9d56fc365ad0e36a

if [[ $# -lt 1 || $# -gt 3 ]]; then
    echo "usage: $0 <scratch-dir> [joltc-src] [jolt-src]" >&2
    exit 2
fi

SCRATCH=$(mkdir -p "$1" && cd "$1" && pwd)
JOLTC_SRC=${2:-}
JOLT_SRC=${3:-}

if [[ ! -f "$SYMVER" ]]; then
    echo "missing symver header: $SYMVER" >&2
    exit 1
fi

clone() {
    local url=$1 dest=$2 commit=$3
    if [[ ! -d "$dest/.git" ]]; then
        git clone "$url" "$dest"
    fi
    git -C "$dest" fetch --depth 1 origin "$commit"
    git -C "$dest" checkout --detach "$commit"
}

if [[ -z "$JOLTC_SRC" ]]; then
    JOLTC_SRC="$SCRATCH/joltc"
    clone https://github.com/amerkoleci/joltc.git "$JOLTC_SRC" "$JOLTC_COMMIT"
fi
if [[ -z "$JOLT_SRC" ]]; then
    JOLT_SRC="$SCRATCH/JoltPhysics"
    clone https://github.com/jrouwe/JoltPhysics.git "$JOLT_SRC" "$JOLT_COMMIT"
fi

got_joltc=$(git -C "$JOLTC_SRC" rev-parse HEAD)
got_jolt=$(git -C "$JOLT_SRC" rev-parse HEAD)
if [[ "$got_joltc" != "$JOLTC_COMMIT" || "$got_jolt" != "$JOLT_COMMIT" ]]; then
    echo "source commits are $got_joltc and $got_jolt; this file is built from $JOLTC_COMMIT and $JOLT_COMMIT" >&2
    exit 1
fi

echo "compiler: $(gcc --version | head -1)"
echo "cmake: $(cmake --version | head -1)"
echo "strip: $(strip --version | head -1)"

# -include needs an absolute path. The path is not in the stripped file.
FLAGS="-include $SYMVER"
BUILD="$SCRATCH/cmake-build"
rm -rf "$BUILD"

cmake -S "$JOLTC_SRC" -B "$BUILD" -G "Unix Makefiles" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="" \
    -DJOLT_PHYSICS_ROOT="$JOLT_SRC" \
    -DCROSS_PLATFORM_DETERMINISTIC=ON \
    -DDOUBLE_PRECISION=OFF \
    -DUSE_ASSERTS=OFF \
    -DDEBUG_RENDERER_IN_DEBUG_AND_RELEASE=OFF \
    -DDEBUG_RENDERER_IN_DISTRIBUTION=OFF \
    -DPROFILER_IN_DEBUG_AND_RELEASE=OFF \
    -DPROFILER_IN_DISTRIBUTION=OFF \
    -DFLOATING_POINT_EXCEPTIONS_ENABLED=OFF \
    -DGENERATE_DEBUG_SYMBOLS=OFF \
    -DINTERPROCEDURAL_OPTIMIZATION=OFF \
    -DENABLE_ALL_WARNINGS=OFF \
    -DJPH_USE_DX12=OFF \
    -DJPH_USE_VK=OFF \
    -DJPH_USE_MTL=OFF \
    -DUSE_SSE4_1=OFF \
    -DUSE_SSE4_2=OFF \
    -DUSE_AVX=OFF \
    -DUSE_AVX2=OFF \
    -DUSE_AVX512=OFF \
    -DUSE_LZCNT=OFF \
    -DUSE_TZCNT=OFF \
    -DUSE_F16C=OFF \
    -DUSE_FMADD=OFF \
    -DJPH_SAMPLES=OFF \
    -DJPH_TESTS=OFF \
    -DCMAKE_C_FLAGS="$FLAGS" \
    -DCMAKE_CXX_FLAGS="$FLAGS"

cache() {
    awk -F= -v k="$1" '
        index($1, k) == 1 && substr($1, length(k) + 1, 1) == ":" {
            print substr($0, index($0, "=") + 1)
            found = 1
        }
        END { if (!found) exit 1 }
    ' "$BUILD/CMakeCache.txt"
}
arch=$(cache CMAKE_OSX_ARCHITECTURES || true)
if [[ -n "$arch" ]]; then
    echo "CMAKE_OSX_ARCHITECTURES is '$arch'; the x86 flags were not applied" >&2
    exit 1
fi
for opt in USE_SSE4_1 USE_SSE4_2 USE_AVX USE_AVX2 USE_AVX512 USE_LZCNT USE_TZCNT USE_F16C USE_FMADD \
           DEBUG_RENDERER_IN_DEBUG_AND_RELEASE DEBUG_RENDERER_IN_DISTRIBUTION \
           PROFILER_IN_DEBUG_AND_RELEASE PROFILER_IN_DISTRIBUTION JPH_USE_DX12 JPH_USE_VK JPH_USE_MTL; do
    val=$(cache "$opt")
    if [[ "$val" != "OFF" ]]; then
        echo "$opt is '$val', expected OFF" >&2
        exit 1
    fi
done
if [[ $(cache CROSS_PLATFORM_DETERMINISTIC) != "ON" ]]; then
    echo "CROSS_PLATFORM_DETERMINISTIC is not ON" >&2
    exit 1
fi

cmake --build "$BUILD" --target joltc --parallel "$(nproc)"

unstripped="$BUILD/lib/libjoltc.so"
if [[ ! -f "$unstripped" ]]; then
    echo "build did not produce $unstripped" >&2
    exit 1
fi
out="$SCRATCH/libjoltc.so"
strip -o "$out" "$unstripped"

# SSE2 only, the same floor as the stock package files. The CMake checks above are the first
# line. This is the second: the stripped file must not contain a VEX mnemonic (anything starting
# with v: AVX, FMA, F16C), an ymm or zmm operand, lzcnt, or an SSE3 through SSE4.2 mnemonic.
# objdump prints GCC's rep bsf (f3 0f bc) as tzcnt. That encoding is generic x86-64 and is allowed.
if objdump -d "$out" | grep -E -q \
    -e '[[:space:]](ymm|zmm|lzcnt)' \
    -e '[[:space:]]v[a-z]' \
    -e '[[:space:]](addsubp[ds]|haddp[ds]|hsubp[ds]|lddqu|movddup|movshdup|movsldup|fisttp)' \
    -e '[[:space:]](pabs[bwd]|palignr|phadd(sw|[dw])|phsub(sw|[dw])|pmaddubsw|pmulhrsw|pshufb|psign[bwd])' \
    -e '[[:space:]](blendv?p[ds]|dpp[ds]|extractps|insertps|movntdqa|mpsadbw|packusdw|pblend(vb|w)|pcmpeqq)' \
    -e '[[:space:]](pextr[bdq]|phminposuw|pinsr[bdq]|pmax(s[bd]|u[dw])|pmin(s[bd]|u[dw]))' \
    -e '[[:space:]](pmov(sx|zx)|pmuldq|pmulld|ptest|round(p[ds]|s[ds]))' \
    -e '[[:space:]](crc32|pcmp(estri|estrm|istri|istrm)|pcmpgtq|popcnt)'; then
    echo "the stripped library has an instruction above SSE2 (VEX, SSE3-SSE4.2, lzcnt, ymm, or zmm)" >&2
    exit 1
fi

python3 - "$out" <<'PY'
import re, subprocess, sys
dump = subprocess.check_output(["objdump", "-T", sys.argv[1]], text=True, errors="replace")

def versions(prefix):
    found = []
    for line in dump.splitlines():
        m = re.search(prefix + r"_([0-9.]+)", line)
        if m:
            found.append(tuple(int(p) for p in m.group(1).split(".")))
    return found

def too_new(found, limit):
    return any(v > limit for v in found)

if too_new(versions("GLIBC"), (2, 34)) or too_new(versions("GLIBCXX"), (3, 4, 22)):
    sys.exit("symbol versions exceed GLIBC_2.34 or GLIBCXX_3.4.22")
fmod = [line.split() for line in dump.splitlines() if "GLIBC_2.2.5" in line and line.split()]
if not any(parts[-1] == "fmodf" for parts in fmod):
    sys.exit("fmodf is not bound to GLIBC_2.2.5")
print("symbol floor ok")
PY

expected=27efc48c813e75294da11eaf22618bc1e0a5426a50edde7870ef4a60c8e4288b
got=$(sha256sum "$out" | awk '{ print $1 }')
echo "$got  $out"
if [[ "$got" != "$expected" ]]; then
    echo "sha256 does not match the committed library ($expected). The bytes depend on the toolchain in PROVENANCE.txt." >&2
    exit 1
fi
echo "wrote $out"
