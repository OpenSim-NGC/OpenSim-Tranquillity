#!/usr/bin/env bash
# Populate <set>/fixtures/ (gitignored) with one avatar's current wearables, the textures they
# reference, and the reference bakes (LL compositor output, captured via the client-bake path
# named in that set's manifest.json).
#
# Usage:  ./fetch-fixtures.sh [set-name]        (default: truly-stock)
#
# A "set" is a subdirectory here holding manifest.json (committed) and fixtures/ (not committed).
# The avatar's name comes from the manifest's "avatar" field; the reference bake UUIDs from its
# "goldens" map.
#
# ---------------------------------------------------------------------------------------------
# REQUIRED ENVIRONMENT
#
# This script reads from a running OpenSim grid. It has no defaults: every variable below must be
# exported for your own grid, and the script stops with a message naming any that is missing.
#
#   GOLDEN_DB_CONTAINER   Name of the running Docker container hosting the grid's MySQL/MariaDB
#                         server. The script runs `docker exec <container> mysql ...` against it.
#   GOLDEN_DB_NAME        The grid's database, the one holding UserAccounts and Avatars.
#   GOLDEN_DB_ENV_FILE    Path to a file holding the database root password as KEY=value on its
#                         own line. The password is read from this file and is never written
#                         anywhere by this script.
#   GOLDEN_DB_PW_KEY      The key name to read out of GOLDEN_DB_ENV_FILE.
#   GOLDEN_ROBUST_ASSETS  Base URL of the grid's Robust asset service, without a trailing slash.
#                         The script GETs <base>/<uuid> and expects AssetBase XML.
#   GOLDEN_REGION_CACHE   Root of a simulator's Flotsam asset cache (the directory holding the
#                         three-character shard subdirectories). Bakes are temporary assets that
#                         Robust does not retain, so they are read from here instead.
#
# Worked example, with placeholder values only:
#
#   export GOLDEN_DB_CONTAINER=mygrid_mysql
#   export GOLDEN_DB_NAME=mygrid
#   export GOLDEN_DB_ENV_FILE=/srv/mygrid/.env
#   export GOLDEN_DB_PW_KEY=MYSQL_ROOT_PASSWORD
#   export GOLDEN_ROBUST_ASSETS=http://robust.internal:8003/assets
#   export GOLDEN_REGION_CACHE=/srv/mygrid/regionserver/assetcache
#   ./fetch-fixtures.sh truly-stock
#
# ---------------------------------------------------------------------------------------------
# Steps:
#
#   1. The avatar's PrincipalID from the grid database named by GOLDEN_DB_NAME.
#   2. Their Avatars rows ('Wearable <type>:<index>' = itemID:assetID, and VisualParams) -> fixtures/avatar.json
#   3. Every wearable asset, every texture those wearables reference, and the reference bakes, from
#      the Robust asset service (AssetBase XML with base64 Data) -> fixtures/<uuid>.<ext>.
#      Bakes are temporary assets and Robust does not hold them; for those the simulator's Flotsam
#      asset cache (same AssetBase XML on disk) is read instead, and the source column says so.
#
# Nothing is fabricated: any UUID that cannot be fetched from either source stops the script (exit 1).
#
# Requires: bash, docker, curl, python 3 ("python3" or "python" on PATH) for XML + base64.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SET="${1:-truly-stock}"
SET_DIR="$HERE/$SET"
MANIFEST="$SET_DIR/manifest.json"
OUT="$SET_DIR/fixtures"
ENV_FILE="${GOLDEN_DB_ENV_FILE:?set GOLDEN_DB_ENV_FILE to the file holding the grid database root password (KEY=value); see the header}"
PW_KEY="${GOLDEN_DB_PW_KEY:?set GOLDEN_DB_PW_KEY to the key name to read out of GOLDEN_DB_ENV_FILE; see the header}"
ROBUST="${GOLDEN_ROBUST_ASSETS:?set GOLDEN_ROBUST_ASSETS to the grid Robust asset service base URL, no trailing slash; see the header}"
REGION_CACHE="${GOLDEN_REGION_CACHE:?set GOLDEN_REGION_CACHE to a simulator Flotsam asset cache root; see the header}"
DB_CONTAINER="${GOLDEN_DB_CONTAINER:?set GOLDEN_DB_CONTAINER to the Docker container running the grid database; see the header}"
DB_NAME="${GOLDEN_DB_NAME:?set GOLDEN_DB_NAME to the grid database holding UserAccounts and Avatars; see the header}"

die() { echo "FETCH FAILED: $*" >&2; exit 1; }

if command -v python3 >/dev/null 2>&1 && python3 -c "" >/dev/null 2>&1; then PY=python3
elif command -v python >/dev/null 2>&1; then PY=python
else die "python 3 not found on PATH"; fi

[ -d "$SET_DIR" ] || die "no such set '$SET' ($SET_DIR). Sets here: $(ls -d "$HERE"/*/ 2>/dev/null | xargs -n1 basename 2>/dev/null | tr '\n' ' ')"
[ -f "$MANIFEST" ] || die "$MANIFEST not found"

# the avatar's name from the manifest
read -r FIRST LAST <<EOF
$("$PY" -c "import json,sys; a=json.load(open(sys.argv[1]))['avatar'].split(); print(a[0], ' '.join(a[1:]))" "$MANIFEST" | tr -d '\r')
EOF
[ -n "$FIRST" ] && [ -n "$LAST" ] || die "manifest 'avatar' field is not a 'First Last' name"

echo "set        $SET"
echo "manifest   $MANIFEST"
echo "avatar     $FIRST $LAST"

[ -f "$ENV_FILE" ] || die "env file $ENV_FILE not found"
PW="$(grep -E "^${PW_KEY}=" "$ENV_FILE" | cut -d= -f2- | tr -d '"\r')"
[ -n "$PW" ] || die "$PW_KEY not set in $ENV_FILE"

sql() { docker exec "$DB_CONTAINER" mysql -uroot -p"$PW" "$DB_NAME" -N -B -e "$1" 2>/dev/null | tr -d '\r'; }

mkdir -p "$OUT"

# ---------------------------------------------------------------- 1. principal
PID="$(sql "SELECT PrincipalID FROM UserAccounts WHERE FirstName='$FIRST' AND LastName='$LAST'" | head -1)"
[ -n "$PID" ] || die "no UserAccounts row for $FIRST $LAST (is $DB_CONTAINER up?)"
echo "principal  $PID"

# ---------------------------------------------------------------- 2. avatar rows -> avatar.json
sql "SELECT Name, Value FROM Avatars WHERE PrincipalID='$PID' AND (Name LIKE 'Wearable %' OR Name='VisualParams') ORDER BY Name" > "$OUT/avatar.tsv"
[ -s "$OUT/avatar.tsv" ] || die "no Avatars rows for $PID"
"$PY" - "$OUT/avatar.tsv" "$OUT/avatar.json" "$PID" <<'PY'
import json, sys
rows = [l.rstrip('\r\n').split('\t', 1) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
wearables, vp = [], []
for name, value in rows:
    if name == 'VisualParams':
        vp = [int(x) for x in value.split(',') if x.strip() != '']
    elif name.startswith('Wearable '):
        t, i = name[len('Wearable '):].split(':')
        item, asset = value.split(':')
        wearables.append({'name': name, 'type': int(t), 'index': int(i), 'itemId': item, 'assetId': asset})
wearables.sort(key=lambda w: (w['type'], w['index']))
json.dump({'principalId': sys.argv[3], 'wearables': wearables, 'visualParams': vp}, open(sys.argv[2], 'w'), indent=2)
print(f"avatar.json: {len(wearables)} wearables, {len(vp)} visual-param bytes")
PY
rm -f "$OUT/avatar.tsv"

# ---------------------------------------------------------------- helpers (python on Windows writes CRLF: strip it)
list_wearables() {
  "$PY" - "$OUT/avatar.json" <<'PY' | tr -d '\r'
import json, sys
for w in json.load(open(sys.argv[1]))['wearables']:
    print(w['type'], w['assetId'])
PY
}

list_goldens() {
  "$PY" - "$MANIFEST" <<'PY' | tr -d '\r'
import json, sys
for k, v in json.load(open(sys.argv[1]))['goldens'].items():
    print(k, v)
PY
}

# texture ids named by a wearable file: the lines after "textures N"
list_textures() {
  "$PY" - "$1" <<'PY' | tr -d '\r'
import sys, re
lines = open(sys.argv[1], encoding='utf-8', errors='replace').read().replace('\r\n', '\n').split('\n')
i = 0
while i < len(lines):
    p = lines[i].split()
    if len(p) >= 2 and p[0] == 'textures' and p[1].isdigit():
        n = int(p[1]); i += 1
        while n > 0 and i < len(lines):
            q = lines[i].split(); i += 1
            if len(q) >= 2 and re.fullmatch(r'[0-9a-fA-F-]{36}', q[1]):
                print(q[1]); n -= 1
        break
    i += 1
PY
}

# unwrap an AssetBase XML into fixtures/<uuid>.<ext>; prints the table row
unwrap() {
  "$PY" - "$1" "$OUT" "$2" "$3" "$4" <<'PY'
import base64, sys, xml.etree.ElementTree as ET
xml, out, uuid, kind, source = sys.argv[1:6]
root = ET.parse(xml).getroot()
data = root.findtext('Data') or ''
atype = root.findtext('Type') or '?'
raw = base64.b64decode(data) if data.strip() else b''
if not raw:
    print(f"FETCH FAILED: {kind} {uuid}: empty Data in AssetBase from {source}", file=sys.stderr); sys.exit(1)
ext = {'0': 'j2c', '5': 'clothing', '13': 'bodypart'}.get(atype, 'type' + atype)
open(f"{out}/{uuid}.{ext}", 'wb').write(raw)
print(f"{uuid}  {kind:<10} {source:<12} type={atype:<3} {len(raw):>8} bytes -> {uuid}.{ext}")
PY
}

# fetch one asset: Robust, else the region cache
fetch() {
  local uuid="$1" kind="$2"
  local source="robust"
  local xml="$OUT/$uuid.xml"
  if ! curl -sf -o "$xml" "$ROBUST/$uuid" || [ ! -s "$xml" ]; then
    rm -f "$xml"
    local cached="$REGION_CACHE/${uuid:0:3}/$uuid"
    if [ -s "$cached" ]; then cp "$cached" "$xml"; source="region-cache"
    else die "$kind $uuid: not on Robust ($ROBUST) and not in $REGION_CACHE"; fi
  fi
  unwrap "$xml" "$uuid" "$kind" "$source"
  rm -f "$xml"
}

# ---------------------------------------------------------------- 3. assets
echo
echo "UUID                                  kind       source       type       bytes"
TEXTURES=()
while read -r t a; do
  [ -n "$a" ] || continue
  # A worn slot may carry the null asset id (the row exists, nothing is in it). The orchestrator skips
  # those (BakeOrchestrator.Resolve: assetId.IsZero()); so does this, and says so.
  if [ "$a" = "00000000-0000-0000-0000-000000000000" ]; then
    printf '%s  %-10s %-12s %s\n' "$a" "wearable:$t" "skipped" "null asset id: slot worn but empty"
    continue
  fi
  fetch "$a" "wearable:$t"
  f="$(ls "$OUT/$a".* | head -1)"
  while read -r id; do
    case "$id" in ""|00000000-0000-0000-0000-000000000000|c228d1cf-4b5d-4ba8-84f4-899a0796aa97) continue;; esac
    TEXTURES+=("$id")
  done < <(list_textures "$f")
done < <(list_wearables)

if [ "${#TEXTURES[@]}" -gt 0 ]; then
  while read -r id; do
    [ -n "$id" ] && fetch "$id" "texture"
  done < <(printf '%s\n' "${TEXTURES[@]}" | sort -u)
fi

while read -r k v; do
  [ -n "$v" ] && fetch "$v" "bake:$k"
done < <(list_goldens)

echo
echo "fixtures in $OUT: $(ls "$OUT" | wc -l) files"
