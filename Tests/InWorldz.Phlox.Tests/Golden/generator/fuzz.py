# Fuzz corpus for the gzip decoder: truncations, bit flips and random bodies of RFC 1952 members.
import zlib, struct, random, sys
def raw(b, lvl=6):
    c=zlib.compressobj(lvl,zlib.DEFLATED,-15,8); return c.compress(b)+c.flush()
def tr(b): return struct.pack('<II', zlib.crc32(b)&0xffffffff, len(b)&0xffffffff)
HDR=bytes.fromhex('1f8b0800000000000400')
def member(b): return HDR+raw(b)+tr(b)
words="the quick brown fox jumps over the lazy dog ete nihon ".split()
rng=random.Random(20260930)
s3=' '.join(rng.choice(words) for _ in range(150)).encode('utf-16-le')
first='first'.encode('utf-16-le')
S=[member(first), member('Hello, World!'.encode('utf-16-le')), member(s3),
   HDR+b'\x01'+struct.pack('<HH',len(first),len(first)^0xffff)+first+tr(first)]
bigsrc=''.join(chr(0x20+rng.randrange(0x3000)) for _ in range(9000)).encode('utf-16-le')
big=member(bigsrc)
out=[]
for s in S:
    for n in range(len(s)+1): out.append(s[:n])
    for i in range(len(s)):
        for b in range(8):
            t=bytearray(s); t[i]^=(1<<b); out.append(bytes(t))
for n in list(range(0,len(big),97))+[k*4096+d for k in range(1,len(big)//4096+1) for d in (-3,-1,0,1,3)]+[len(big)]:
    if 0<=n<=len(big): out.append(big[:n])
for _ in range(1500):
    out.append(HDR+bytes(rng.randrange(256) for _ in range(rng.randrange(1,60))))
for _ in range(1500):
    # dynamic-block headers: BFINAL=1, BTYPE=2 then random bits
    out.append(HDR+bytes([0x05|(rng.randrange(32)<<3)])+bytes(rng.randrange(256) for _ in range(rng.randrange(1,80))))
with open(sys.argv[1],'w') as f:
    for o in out: f.write(o.hex()+'\n')
print(len(out), 'big', len(big))
