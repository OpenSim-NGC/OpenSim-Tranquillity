# Turns fuzz.py's corpus (one hex stream per line) and GzProbe's Framework results (one line each) into
# gzip-decoder-vectors.jsonl: the base streams once, each case as a truncation or bit flip of a base or as raw hex,
# each result as the output bytes (hex, when short) or their length and SHA-256, or the exception "Type: message".
# usage: python compact_fuzz.py gzip-fuzz.txt gzip-fuzz-framework.txt gzip-decoder-vectors.jsonl
import sys, json, hashlib

lines = open(sys.argv[1]).read().split('\n')[:-1]
fw = open(sys.argv[2]).read().split('\n')[:-1]
assert len(lines) == len(fw)


def res(r):
    if r.startswith('ok '):
        h = r[3:]
        if len(h) <= 64:
            return {"ok": h}
        b = bytes.fromhex(h)
        return {"okLen": len(b), "okSha256": hashlib.sha256(b).hexdigest()}
    return {"ex": r[3:]}


bases, out, k = [], [], 0
# fuzz.py: for each of 4 small streams, every truncation (0..len bytes), then every single-bit flip
while len(bases) < 4:
    assert lines[k] == '', k
    n = 0
    while k + n + 1 < len(lines) and len(lines[k + n + 1]) == 2 * (n + 1) and lines[k + n + 1].startswith(lines[k + n]):
        n += 1
    s = lines[k + n]
    bid = len(bases)
    bases.append(s)
    for j in range(n + 1):
        out.append({"of": bid, "trunc": j, **res(fw[k + j])})
    k += n + 1
    for bi in range(len(s) // 2):
        for bit in range(8):
            t = bytearray(bytes.fromhex(s))
            t[bi] ^= 1 << bit
            assert lines[k] == t.hex(), (k, bi, bit)
            out.append({"of": bid, "flip": [bi, bit], **res(fw[k])})
            k += 1
# then truncations of one large stream, then random streams
big = max(lines[k:], key=len)
bid = len(bases)
bases.append(big)
while k < len(lines) and big.startswith(lines[k]):
    out.append({"of": bid, "trunc": len(lines[k]) // 2, **res(fw[k])})
    k += 1
for j in range(k, len(lines)):
    out.append({"hex": lines[j], **res(fw[j])})
with open(sys.argv[3], 'w', newline='\n') as f:
    for bi, b in enumerate(bases):
        f.write(json.dumps({"base": bi, "hex": b}) + '\n')
    for o in out:
        f.write(json.dumps(o) + '\n')
