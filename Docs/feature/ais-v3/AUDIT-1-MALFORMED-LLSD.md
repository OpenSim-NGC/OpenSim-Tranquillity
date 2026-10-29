# Malformed LLSD: how the parser fails, and the safe pattern

A note for anyone writing a handler that parses an LLSD request body. Code comments cite its section numbers.

## 1. What the parser returns for a bad body

The OSD types in this tree come from the `UtopiaSkye.OpenMetaverse.StructuredData` package
(`Directory.Build.props`).

### 1a. `DeserializeLLSDXml` does not throw

For a truncated body such as `<llsd><array><map>`, `OSDParser.DeserializeLLSDXml` returns a non-null `OSD`
whose `Type` is `OSDType.Unknown`. It does not throw and does not return null.
`Tests/OpenSim.Region.CoreModules.Tests/World/LightShare/Tests/ViewerEnvironmentWLOSDTests.cs` pins this.

A valid empty map or array parses with its proper type. **`Type == OSDType.Unknown` is therefore the signal
that a body could not be read.** The same value also means "key not present" when a missing key is read from
an `OSDMap`, so it is easy to overlook.

### 1b. The entry points differ

The `OSDParser` entry points do not fail the same way. For some inputs some of them return `Unknown`, and for
others they throw. Do not assume a `try`/`catch` around a parse covers a malformed body. Check the type of the
result as well. The auto-detecting `OSDParser.Deserialize` needs the same check as `DeserializeLLSDXml`.

### 1c. What a degenerate value does downstream

- A hard cast such as `(OSDMap)osd` throws, which fails loudly.
- A soft cast such as `osd as OSDMap` yields null, which fails loudly on first use.
- The risk is holding the bare `OSD` and calling accessors on it (`AsString()`, `AsUUID()` and so on). That
  goes on quietly with empty or zero values, and it is the only path that can write wrong data without anyone
  noticing.

## 2. The anti-pattern

```csharp
catch { return new OSDMap(); }
```

This turns a failure into a plausible, empty, correctly typed value. Downstream code then reads it as "the
client asked for nothing". On a route that replaces something, such as an outfit slam, that empties it. Never
substitute a default `OSD` / `OSDMap` / `OSDArray` for a body that could not be parsed.

## 3. The safe pattern

Refuse at the boundary, before anything is built or stored:

```csharp
OSD parsed = OSDParser.DeserializeLLSDXml(bytes);
if (parsed is null || parsed.Type == OSDType.Unknown)
    // answer 400 and write nothing
```

or, where a particular shape is required:

```csharp
if (parsed is not OSDMap map)
    // refuse
```

Examples in the tree:
- `AisHandler.ReadBody` (`Source/OpenSim.Region.ClientStack.LindenCaps/AIS/AisHandler.cs`) checks `Type` and
  answers 400 `malformed LLSD body` on any mutating AIS route.
- The LLSD dispatcher in `BaseHttpServer` refuses a body that is not an `OSDMap` before calling the handler.

## 4. Hard-cast handlers

Many existing handlers parse with a hard cast, `(OSDMap)OSDParser.DeserializeLLSDXml(...)`. On a malformed body
the cast throws before the handler reaches any backend call. The request then fails with a logged exception
and a 500 rather than a clean 400. That is a quality gap, not a data-loss risk, and it is best fixed handler by
handler when each one is touched for another reason.

## 5. A guard that tested the wrong variable (fixed)

`ViewerEnvironment.FromWLOSD` (`Source/OpenSim.Framework/ViewerEnvironment.cs`) used to cast with
`osd as OSDArray` and then test `osd` for null instead of the cast result. A degenerate body is non-null, so
null reached `DayCycle.FromWLOSD`, which threw. In the legacy WindLight setter
(`EnvironmentModule.SetEnvironmentSettings`), that throw happened to land before `StoreOnRegion`. It was the
only thing stopping a truncated request from overwriting the region's environment with a default one.

Both sites are now fixed:
- the guard tests the cast result;
- the setter refuses a body that is not an `OSDArray` before building anything, with a WARN naming the body's
  type and a `fail_reason` in the response.

The lesson: when a throw happens to protect a write, fixing the throw alone arms the write. Add the check at
the boundary as well.
