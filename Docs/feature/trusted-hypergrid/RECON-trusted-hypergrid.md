# Home-URI matching in the gatekeeper

A note for maintainers. `HGUriNormalizer` and `TrustedGridRegistryService` cite it as "Recon R6".

## R6. The gatekeeper's exception lists match home URIs as plain strings

`[GatekeeperService] AllowExcept` and `DisallowExcept` list home URIs that are exceptions to
`ForeignAgentsAllowed` (`Source/OpenSim.Services.HypergridService/GatekeeperService.cs`).

- **Loading the lists.** `LoadDomainExceptionsFromConfig` splits each list on commas, trims each entry and adds a
  trailing slash. Nothing else is normalised.
- **Matching a visitor.** `IsException` takes the visitor's `HomeURI` from its agent circuit, trims it, adds a
  trailing slash, and compares it with each entry using `string.Equals`. That comparison is ordinal and
  case-sensitive.
- **Where it is used.** `LoginAgent` uses `IsException` to apply both lists.

As a result, two spellings of the same grid do not match. `http://Grid.Example:8002/` and
`http://grid.example:8002/` are different entries, and so are a URI with the default port written out and the
same URI without it. The `HomeURI` is also whatever the visitor's own grid declares. A grid that presents a
different URI, such as one of its configured aliases, is not matched by an entry for its main URI.

The trust registry does not repeat this. Every URI it stores or looks up goes through the one shared
`HGUriNormalizer`, which lowercases the scheme and host, makes the port explicit and adds a trailing slash. Its
identity is the grid's key rather than the URI (`DESIGN-trusted-hypergrid.md` §3, `ADR-trusted-hypergrid.md`
ADR-003).
