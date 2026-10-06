# Trusted Hypergrid

Trusted Hypergrid gives a grid a signed identity on the Hypergrid. It also lets the operator keep a registry of
the other grids it has seen, each marked trusted, open or blocked. It implements the NGC *Trusted HyperGrid*
charter (ADR-001). The design decisions behind it are in `ADR-trusted-hypergrid.md`.

It provides attribution and revocation, not copy prevention (§9, ADR-008).

Section numbers are stable: code comments cite them as "Design Brief §n".

## 1. What it does today

- Signs the gatekeeper's outbound `link_region` and `get_region` XML-RPC calls with the grid's Ed25519 key.
- Verifies the same two calls inbound and classifies the caller as Trusted, Open or Blocked.
- Records each signed caller in a trust registry the first time it is seen (trust on first use), as Open and
  pending approval.
- Gives the operator `hgtrust` console commands to approve, block and forget grids.
- Optionally refuses `link_region` / `get_region` from a grid the operator has blocked. Nothing else is ever
  refused.

With `[TrustedHypergrid] Enabled = false` (the default), none of this runs. No key is read or written, nothing
is signed or verified, and no registry is created.

## 2. Compatibility rules

- **Unsigned or unverifiable callers are Open, never refused** (ADR-005). All of these classify as Open and the
  request proceeds as it would without this code: a stock OpenSim grid, a Tranquillity grid with the feature
  off, a bad signature, a stale timestamp, a replayed nonce.
- **Open loses nothing** (ADR-011). Trusted and Open get identical access. The only refusal is of a Blocked
  grid, and only when the operator arms it (§8).
- **No new endpoints or transports.** Signatures ride as extra parameters on the existing calls (ADR-004).

## 3. Trust model

Three tiers, per remote grid:

| Tier | How a grid gets it |
|---|---|
| **Trusted** | Only `hgtrust approve <uri>`. Nothing promotes a grid automatically. |
| **Open** | Every unknown grid and every unverifiable request. A newly recorded grid is Open. |
| **Blocked** | Only `hgtrust block <uri>`. |

**Identity is the public key, not the URI** (ADR-003). The URI a grid sends is only a label. Several URIs can
map to one key (aliases). `hgtrust show` finds a grid by home URI, alias URI or key fingerprint. A verified
caller is resolved by its key fingerprint, so a blocked grid stays blocked under any URI it claims. A grid that
generates a new key is a new, Open grid.

**First contact.** A request may verify and carry `tg_uri`. If so, the registry records that URI with the
presented key and fingerprint, at tier Open, state pending. A repeat contact with the same key only updates
`last_seen`.

**Key change.** If a known URI presents a different key, the row is flagged state 2 (key changed, pending
re-approval). The original key and tier are kept. The new key resolves to nothing, so calls signed with it
classify Open. To accept the new key, run `hgtrust forget <uri>`, let the grid reconnect, then run
`hgtrust approve <uri>`.

**Where `tg_uri` comes from.** Each grid sends its own `GatekeeperURI`, normalised. It is read at startup from
`[Startup]`, `[Hypergrid]`, `[GatekeeperService]` or `[UserAgentService]`. A grid with no `GatekeeperURI` sends
no `tg_uri`. Its calls are still classified, but it cannot be recorded.

## 4. Trust registry

The registry has two tables. A migration creates them the first time the service starts with the feature
enabled. MySQL and SQLite stores exist.

`hg_trusted_grids`:

| Column | Type | Notes |
|---|---|---|
| `id` | CHAR(36) | primary key |
| `home_uri` | VARCHAR(255) | normalised, unique |
| `public_key` | VARBINARY(32) | raw Ed25519 public key |
| `key_fingerprint` | CHAR(64) | SHA-256 hex of the public key; the identifier operators see |
| `tier` | TINYINT | 0 = Blocked, 1 = Open, 2 = Trusted; default 1 |
| `state` | TINYINT | 0 = pending, 1 = approved, 2 = key changed, pending re-approval; default 0 |
| `first_seen`, `last_seen` | DATETIME | |
| `approved_by` | VARCHAR(64) | free text from `hgtrust approve` (default `console`) |
| `approved_at` | DATETIME | null until approved |
| `notes` | TEXT | |

`hg_grid_aliases` has two columns, `grid_id` and `alias_uri` (normalised). One grid can have many aliases.
There is no console command to add an alias; code can call `AddAlias` on the registry service.

**URI normalisation** is done by one shared function, `HGUriNormalizer`, at every write and lookup. It
lowercases the scheme and host, makes the port explicit and adds a trailing slash.

**The grid's private key** is never stored in the database. It lives in the file named by `PrivateKeyFile`
(§8), as `[TrustedHypergrid] PrivateKey = <hex>`. If the file is absent on first run, it is generated. The
fingerprint is logged at INFO whenever the key is generated or loaded. `TrustedHypergridSecret.ini` is listed
in `.gitignore`.

## 5. Signature envelope

The signed payload (`HGSignatureEnvelope.BuildCanonicalPayload`) is these five fields joined by newlines:

1. the method name;
2. the sender's key fingerprint;
3. the UTC timestamp (`yyyy-MM-ddTHH:mm:ssZ`);
4. a nonce (16 random bytes, base64);
5. a parameter digest.

The signature is Ed25519 over that payload, base64-encoded. Signer and verifier share the one canonicaliser.

**Parameter digest** (`HGSignatureEnvelope.ParametersDigest`): the call's own parameters are written as
`key=value` lines in sorted order, leaving out every `tg_*` / `X-TG-*` entry. The digest is the lowercase
SHA-256 hex of those lines. The sender's URI is then added once, as `tg_uri=<uri>`. When there is no sender
URI, nothing is added. So rewriting `tg_uri` in transit breaks the signature.

**Replay defence.**
- A timestamp more than 300 s from the receiver's clock is unverified (`TimestampTolerance`).
- Nonces are remembered for 600 s (`NonceWindow`), and a repeated nonce is unverified.
- A nonce is recorded only after the rest of the signature checks out, so a bad request cannot use one up.

Unverified always means Open (§2).

**XML-RPC carriage.** Five keys go in the existing parameter table:
- `tg_key` (base64 public key), `tg_ts`, `tg_nonce`, `tg_sig`: all four must be present for a signature to be
  checked.
- `tg_uri`: omitted when the sender has no `GatekeeperURI`.

Stock grids ignore keys they don't recognise.

**HTTP carriage.** `SignatureMaterial` defines the headers `X-TG-Key`, `X-TG-Timestamp`, `X-TG-Nonce`,
`X-TG-Signature` and `X-TG-Uri`, and `TrustedGridAuthentication.AddAuthorization` can write them. No HTTP call
site is wired yet (§7).

### 5.1 Version compatibility

There is no wire version field.

| Signer | Verifier | Result |
|---|---|---|
| sends no `tg_uri` | current | Verified (an absent URI adds nothing to the digest) |
| current (sends `tg_uri`) | older verifier that excludes every `tg_*` key from the digest | Open, never refused; nothing recorded |
| current | current | Verified; first contact recorded |
| stock grid (no signature) | any | Open |

When upgrading a pair of grids, upgrade the verifying side first.

## 6. Verification and enforcement are separate

- **`GridSignatureVerifier`** classifies a request and returns a `GridTrustContext` (grid id, tier, outcome).
  It never rejects.
- **`TrustedHypergridHooks.Classify`** publishes that context as `GridTrustContext.Current` for the length of
  one request. It clears the context on every exit path, so the context cannot leak into the next request.
- **`TrustedGridAuthentication`** (an `IServiceAuth`) is the only component that refuses. It refuses only when
  the current context's tier is Blocked. No context means Open.

## 7. What is wired, and what is not

| Path | Status |
|---|---|
| Gatekeeper `link_region`, `get_region`, outbound (`GatekeeperServiceConnector`) | signed |
| Gatekeeper `link_region`, `get_region`, inbound (`HypergridHandlers`) | verified, classified, first contact recorded; Blocked refused when armed |
| UserAgent and HGFriends XML-RPC calls | not wired |
| `HGAssetService`, `HGInventoryService` HTTP calls | not wired |
| Export-bit and asset-service enforcement | not built (ADR-006, ADR-007) |

**Process scope.** Only the inbound gatekeeper connector initialises the runtime. That is the Robust process
serving the Hypergrid gatekeeper. A region simulator does not initialise it, so gatekeeper calls made by a
simulator itself go out unsigned.

## 8. Configuration and console

All keys are in `[TrustedHypergrid]` in `Robust.HG.ini`:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turns on signing, verification, the registry and the `hgtrust` commands. |
| `PrivateKeyFile` | `TrustedHypergridSecret.ini` | File holding this grid's private key. A relative path is resolved against the process working directory. Created on first run. |
| `StorageProvider` | value from `[DatabaseService]` | Data plugin for the registry, if it should differ from the main database. |
| `ConnectionString` | value from `[DatabaseService]` | Connection string for the registry, if it should differ. |

To load the key file through the config include system instead, add
`Include-TrustedHypergrid = "TrustedHypergridSecret.ini"`.

**Refusing blocked grids** is a separate switch, in `[GatekeeperService]`:

```ini
[GatekeeperService]
    AuthType = "TrustedGridAuthentication"
```

The switch takes effect only when `Enabled = true`. With both set, `link_region` and `get_region` refuse a grid
whose verified key is Blocked. The response is `result = False` with the message "Refused: this grid's
operator has blocked your grid."

- An unsigned request is never refused, even from a blocked URI: a block needs a verifying signature.
- The setting is also read from `[Network] AuthType`.
- The shipped ini has the line commented out. In that state, nothing on the Hypergrid can be refused by this
  feature.

**Console commands** (Robust console, Hypergrid group):

| Command | Effect |
|---|---|
| `hgtrust list` | Table of every registry row. |
| `hgtrust show <uri\|fingerprint>` | One row, found by home URI, alias URI or fingerprint, with its aliases and any key-change warning. |
| `hgtrust approve <uri> [approved-by]` | Sets the grid Trusted and approved. The only way to make a grid Trusted. |
| `hgtrust block <uri>` | Sets the grid Blocked. |
| `hgtrust forget <uri>` | Removes the row and its aliases. The grid's next contact is a first contact. |
| `hgtrust key show` | Shows this grid's own fingerprint. |

**Logs.** The tag is `[TRUSTED HG]`. Generating or loading the key logs at INFO, inbound classification logs at
DEBUG, and refusals log at INFO.

## 9. Threat model

**Defends against:**
- a third party on the wire rewriting a legitimate grid's `tg_uri`: the request fails verification and
  classifies Open, and no registry row changes;
- replay of a captured signed request (nonce cache and timestamp tolerance, §5);
- a blocked grid getting round the block by claiming a different URI, because the block follows its key (§3).

**Does not defend against** (state this wherever the feature is described):
- a modified viewer copying anything rendered on screen;
- a trusted grid's operator misusing content that legitimately reaches them;
- anything once content has left the grid;
- a blocked grid that generates a new key.

**Accepted limitation.** A grid holding any valid keypair can sign a request that claims another grid's URI.
The protocol cannot tell that from a genuine key change, so the claimed URI's row is flagged state 2. Its
original key and tier are kept, and the flag shows in `hgtrust show`. An operator resolves it out of band.
Nothing enforces on `state` today; this must be weighed before anything does.

## 10. Not in scope

- gating the presence endpoints (`locate_user`, `get_uui`, `get_uuid`, `get_server_urls`,
  `status_notification`, `get_online_friends`);
- a signed community roster;
- any certificate authority;
- TLS changes;
- any new Hypergrid endpoint or transport;
- any change to landmark handling.

## 11. Behaviour guarantees

Each of these is covered by tests in `Tests/OpenSim.TrustedHypergrid.Tests/`.

1. A signed call between two enabled grids verifies, and the caller is recorded with the right fingerprint.
2. An unsigned call classifies Open and the handler proceeds.
3. A tampered signature, tampered parameters, a stale timestamp or a replayed nonce classifies Open without an
   exception.
4. Only `hgtrust approve` makes a grid Trusted.
5. A different key for a known URI is flagged state 2. The original key is kept and the new key does not resolve.
6. A Blocked grid is refused only when `TrustedGridAuthentication` is configured. Trusted and Open are handled
   identically.
7. With `Enabled = false`, nothing is published and nothing is refused, even with `AuthType` set and a Blocked
   row in the registry. No key file is written.

## 12. Where the code is

| Area | Path |
|---|---|
| Identity, signing, verification, hooks | `Source/OpenSim.Framework/TrustedHypergrid/` |
| Refusal component | `Source/OpenSim.Framework/ServiceAuth/TrustedGridAuthentication.cs`, registered in `ServiceAuth.cs` |
| Registry service and console | `Source/OpenSim.Services.HypergridService/TrustedGridRegistryService.cs`, `TrustedGridServiceBase.cs` |
| Data stores and migrations | `Source/OpenSim.Data/ITrustedGridData.cs`, `Source/OpenSim.Data.MySQL/MySQLTrustedGridData.cs`, `Source/OpenSim.Data.SQLite/SQLiteTrustedGridData.cs`, and `Resources/TrustedGrid.migrations` in each of the two store projects |
| Call sites | `Source/OpenSim.Server.Handlers/Hypergrid/GatekeeperServerConnector.cs`, `HypergridHandlers.cs`; `Source/OpenSim.Services.Connectors/Hypergrid/GatekeeperServiceConnector.cs` |
| Shipped config | `Source/OpenSim.Server.GridServer/AppData/Robust.HG.ini.example`, sections `[TrustedHypergrid]` and `[GatekeeperService]` |
| Tests | `Tests/OpenSim.TrustedHypergrid.Tests/` |
