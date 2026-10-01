# iwStringCodec golden vectors

Phlox ports Halcyon's iwStringCodec, and the port requires byte-for-byte proof. Every file here was produced by
**Halcyon's own code or by .NET Framework itself** on .NET Framework 4.8.1 (Windows 11, 2026-09-30). Halcyon targets
.NET Framework 4.7.1, and the generator's entry assembly declares 4.7.1 so the runtime takes the same compatibility
paths. The tests: `IwStringCodecGoldenTests` and `IwStringCodecTests`.

| File | What it is | Where it came from |
|---|---|---|
| `iwStringCodec-vectors.jsonl` | 1529 calls: every codec, encode/decode/validate, empty input, 999-65536 chars (the sleep steps and Framework's Uri limit), non-ASCII and lone surrogates, bad input and bad parameters, deflate inputs | Halcyon `InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs:16053-16955` (CodecUtil + iwStringCodec) compiled unchanged with Halcyon's own `bin/InWorldz.Phlox.dll` (its LSLList) and `lib/LibreMetaverse.Types.dll`: `generator/HalcyonCodec.cs`, `Harness.cs`, `Cases.cs` |
| `gzip-decoder-vectors.jsonl` | 6431 gzip streams: every truncation and single-bit flip of 4 small members, truncations of a 16947-byte member, 3000 random blocks | streams: `generator/fuzz.py` (Python's zlib, RFC 1951/1952); answers: Halcyon's gzipDecompress body (`GZipStream` + `CopyTo`, LSLSystemAPI.cs:16416-16428) on Framework, `generator/GzProbe.cs`; packed by `generator/compact_fuzz.py` |
| `uri-escape-vectors.jsonl` | 3441 strings for `Uri.EscapeDataString`, which Halcyon's base4096 decode calls | Framework's `Uri.EscapeDataString`, `generator/Harness.cs` UriVectors |
| `framework-char-classes.txt` | For each UTF-16 unit 0-65535: `Char.IsLetterOrDigit` (1) + `Char.IsDigit` (2) | Framework, `generator/CharProbe.cs` |

Each vector records the result (its length and SHA-256 when over 400 characters), the exception type and message if
one left the call, every `LSL Runtime Error` Halcyon said, and every ScriptSleep in order. Halcyon's LSLError shouts
through SimChat, which sleeps 15 ms (LSLSystemAPI.cs:14510, 14483-14486, 1041-1045), and a later ScriptSleep replaces
an earlier one, so the last one counts.

The hashes of plain text also equal the published MD5/SHA-1/SHA-2 values (RFC 1321, FIPS 180-4) of its UTF-8 bytes,
e.g. md5("Hello, World!") = 65a8e27d8879283831b664bd8b7f0ad4. The gzip bodies equal stock zlib 1.2.13 deflate at level 6
(checked with Python's zlib 1.2.13 on all 90 gzip encodes written as base16 or base64, up to 131072 bytes).

## Regenerating (Windows, .NET Framework 4.8 installed with Windows; no SDK needed)

Needs a Halcyon checkout (`H`), with `bin/InWorldz.Phlox.dll`, its dependencies (`C5.dll`, `protobuf-net.dll`,
`Antlr3.Runtime.dll`, `Antlr3.StringTemplate.dll`) and `lib/LibreMetaverse.Types.dll` copied next to the output.
`HalcyonCodec.cs` is lines 16053-16955 of Halcyon's LSLSystemAPI.cs with a short header; only `private static class
CodecUtil` became `public`.

    csc -out:gen.exe -r:System.Core.dll -r:LibreMetaverse.Types.dll -r:InWorldz.Phlox.dll HalcyonCodec.cs Harness.cs Cases.cs
    gen.exe iwStringCodec-vectors.jsonl gzip-crafted.txt uri-escape-vectors.jsonl
    python fuzz.py gzip-fuzz.txt
    csc -out:gzprobe.exe GzProbe.cs && gzprobe.exe gzip-fuzz.txt gzip-fuzz-framework.txt
    python compact_fuzz.py gzip-fuzz.txt gzip-fuzz-framework.txt gzip-decoder-vectors.jsonl
    csc -out:charprobe.exe CharProbe.cs && charprobe.exe framework-char-classes.txt

(`csc` is `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.) `craft.py` wrote `gzip-crafted.txt`, the
hand-made gzip streams that `Cases.cs` feeds through Halcyon's decoder.
