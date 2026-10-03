# Crafted gzip streams (hex) for Halcyon's gzip decoder, per RFC 1952 / RFC 1951.
import zlib, struct
def raw(b, lvl=6):
    c=zlib.compressobj(lvl,zlib.DEFLATED,-15,8); return c.compress(b)+c.flush()
def trailer(b): return struct.pack('<II', zlib.crc32(b)&0xffffffff, len(b)&0xffffffff)
HDR=bytes.fromhex('1f8b0800000000000400')
first='first'.encode('utf-16-le'); second='second'.encode('utf-16-le')
m1=HDR+raw(first)+trailer(first)
body=raw(first)
out=[]
def add(label, b): out.append((label, b.hex()))
add('bad-magic', b'\x1f\x8c'+m1[2:])
add('cm7', m1[:2]+b'\x07'+m1[3:])
add('reserved-flag', m1[:3]+b'\x20'+m1[4:])
add('ftext', m1[:3]+b'\x01'+m1[4:])
add('fextra', m1[:3]+b'\x04'+m1[4:10]+b'\x04\x00abcd'+body+trailer(first))
add('fextra-short', m1[:3]+b'\x04'+m1[4:10]+b'\xff\x00ab')
add('fcomment', m1[:3]+b'\x10'+m1[4:10]+b'hi\x00'+body+trailer(first))
h=m1[:3]+b'\x02'+m1[4:10]
add('fhcrc-good', h+struct.pack('<H', zlib.crc32(h)&0xffff)+body+trailer(first))
add('fhcrc-bad', h+b'\x00\x00'+body+trailer(first))
h=m1[:3]+b'\x1e'+m1[4:10]+b'\x02\x00xyname\x00comment\x00'
add('all-flags', h+struct.pack('<H', zlib.crc32(h)&0xffff)+body+trailer(first))
add('fname-unterminated', m1[:3]+b'\x08'+m1[4:10]+b'abc')
add('truncated-body', HDR+body[:len(body)//2])
add('btype3', HDR+b'\x07\x00'+trailer(b''))
add('stored', HDR+b'\x01'+struct.pack('<HH',len(first),len(first)^0xffff)+first+trailer(first))
add('stored-bad-nlen', HDR+b'\x01'+struct.pack('<HH',len(first),0)+first+trailer(first))
add('stored-then-final', HDR+b'\x00'+struct.pack('<HH',4,4^0xffff)+first[:4]+b'\x01'+struct.pack('<HH',len(first)-4,(len(first)-4)^0xffff)+first[4:]+trailer(first))
add('no-final-block', HDR+b'\x00'+struct.pack('<HH',len(first),len(first)^0xffff)+first+trailer(first))
add('magic-only', b'\x1f\x8b')
add('header-9', HDR[:9])
three=b'abc'
add('odd-bytes', HDR+raw(three)+trailer(three))
add('empty-stored', HDR+b'\x03\x00'+trailer(b''))
add('dist-too-far', HDR+bytes.fromhex('630000')+b'\x00'*8)  # static block: literal then a length/distance past the start
add('garbage-after-magic', m1+b'\x1f\x8b\x08')
add('second-bad-crc', m1+HDR+raw(second)+b'\x00\x00\x00\x00'+struct.pack('<I',len(second)))
add('first-bad-crc-second-good', HDR+raw(first)+b'\x00\x00\x00\x00'+struct.pack('<I',len(first))+HDR+raw(second)+trailer(second))
net10hdr=bytes.fromhex('1f8b080000000000000a')
add('net10-header', net10hdr+body+trailer(first))
add('size-mod', HDR+body+struct.pack('<II', zlib.crc32(first)&0xffffffff, len(first)+2**32 - 2**32))
big=('x'*70000).encode('utf-16-le')
add('big-member', HDR+raw(big)+trailer(big))
add('crc-only', HDR+body+struct.pack('<I', zlib.crc32(first)&0xffffffff))
add('trailer-plus-one', HDR+body+trailer(first)+b'\x00')
import sys
with open(sys.argv[1],'w') as f:
    for l,x in out: f.write(l+' '+x+'\n')
print(len(out))
