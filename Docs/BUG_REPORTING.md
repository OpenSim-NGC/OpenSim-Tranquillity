# Reporting a bug

Bugs are tracked in the project's
[issues](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/issues). A good report lets someone else see the
problem on their own machine. This page says what to include and what to leave out.

**Security problems are not reported here.** If the problem lets someone do what they should not (read or change
other people's data, take over an account, crash or take control of a server), report it privately as
[SECURITY.md](SECURITY.md) explains.

## Before you report

1. Search the [open and closed issues](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/issues?q=is%3Aissue).
   If the bug is already reported, add what you know to that issue.
2. Check that it happens on the latest release, or on current `develop` if you build from source.
3. For a question about setup or configuration rather than a bug, use
   [Discussions](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/discussions).

## What to include

- **What happened and what you expected**, in a few sentences.
- **Steps to reproduce**, numbered, starting from a fresh login or a server start where you can.
- **Version**: the `show version` console command prints it. If you build from source, the commit.
- **Setup**: standalone or grid; Hypergrid on or off; operating system; database (SQLite, MySQL/MariaDB,
  PostgreSQL); asset service (plain or FSAssets); script engine (YEngine or Phlox); physics engine (ubODE, BulletS,
  Jolt…).
- **Viewer** and its version, for anything seen in-world.
- **Log lines** around the time of the problem, including any stack trace. Logs are written to the folder named
  by `LOGDIR`, or to the folder the server runs in, as `OpenSim.Server.RegionServer<date>.log`,
  `OpenSim.Server.GridServer<date>.log` and so on. Attach long logs as files.
- **The relevant configuration sections**, not whole files.
- **Screenshots or a short video**, for anything visual.
- **How often it happens**: every time, sometimes, once.

## What to leave out

Issues are public. Before you attach anything, remove:

- passwords, database connection strings, API keys, tokens and `api_secret` values;
- capability URLs (they contain a key that grants access), session and secure session IDs;
- public IP addresses and host names you do not want public;
- names of other people's avatars, and the content of private chats or IMs;
- assets or archives (OAR, IAR) you do not have the right to share.

Replace a removed value with a placeholder such as `<password>` or `<region A>` so the report stays readable.

## After you report

Maintainers and contributors are volunteers. They may ask for more detail, or ask you to test a fix. A report
with clear steps and logs is the one that gets fixed first.
