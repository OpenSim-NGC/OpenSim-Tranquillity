# Instructions for coding agents

The rules for AI coding agents working on this repository are in [AGENTS.md](../AGENTS.md). Read it in full before
making changes, then [ARCHITECTURE.md](../ARCHITECTURE.md) for the map of the code.

In short:

- C# on .NET 10 (`global.json`); the solution is `Tranquillity.sln`, with code under `Source/`, tests under
  `Tests/` and optional modules under `Addons/`.
- Build with `dotnet build Tranquillity.sln -c Release`; test as [Docs/TESTING.md](../Docs/TESTING.md) describes.
- Pull requests target `develop`. A fix comes with a test that fails without it.
- No secrets or personal data in the repository. Security problems are reported privately
  ([Docs/SECURITY.md](../Docs/SECURITY.md)).
