Welcome to Tranquillity !

# Overview

Tranquillity is an OpenSimulator derivitive forked to development using up to date C# and dotnet architecural
patterns and technology.  Licensing from the original OpenSimulator (which is a BSD Licensed Open Source project)
has been retained in files directly derived from OpenSimulator.  Some new elements are licensed MPLv2 and are
licensed by their respective contributor.   Current License information is found in the LICENSE.txt file at
the top of the project. 

Tranquillity is a project that endeavours to develop a functioning virtual worlds server platform capable of
supporting multiple clients and servers in a heterogeneous grid structure while leveraging the architectural
patterns provided by the .NET and C# platform. Tranquillity is written in C# and targets .NET 10. Building from
source requires the .NET 10 SDK; running the published servers requires the .NET 10 runtime.

# Compiling Tranquillity

For build instructions, see Docs/BUILDING.md.

# Testing

For running and writing tests, see [Docs/TESTING.md](Docs/TESTING.md).

# Plugin Development

For plugin authoring and registration steps in the DotNetCorePlugins model,
see Docs/PLUGIN_DEVELOPMENT.md.

# Running Tranquillity

Install the [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for RegionServer. GridServer and MoneyServer also require the **ASP.NET Core 10 Runtime**, available on the same download page.
For source builds, install the .NET 10 SDK version selected by `global.json`.

Tranquillity now publishes separate server executables. From the directory containing
the published server files, start a region server with:

```sh
dotnet OpenSim.Server.RegionServer.dll
```

For a grid deployment, start the grid services with:

```sh
dotnet OpenSim.Server.GridServer.dll
```

If using the optional money server, start it with:

```sh
dotnet OpenSim.Server.MoneyServer.dll
```

Configure the server `.ini` files for your deployment before starting the servers;
see the "Configuring Tranquillity" section and [Docs/BUILDING.md](Docs/BUILDING.md).

# Configuring Tranquillity

When Tranquillity starts for the first time, you will be prompted with a
series of questions that look something like:

	[09-17 03:54:40] DEFAULT REGION CONFIG: Simulator Name [Tranquillity Test]:

For all the options except simulator name, you can safely hit enter to accept
the default if you want to connect using a client on the same machine or over
your local network.

You will then be asked "Do you wish to join an existing estate?".  If you're
starting Tranquillity for the first time then answer no (which is the default) and
provide an estate name.

Shortly afterwards, you will then be asked to enter an estate owner first name,
last name, password and e-mail (which can be left blank).  Do not forget these
details, since initially only this account will be able to manage your region
in-world.  You can also use these details to perform your first login.

Once you are presented with a prompt that looks like:

	Region (My region name) #

You have successfully started Tranquillity.

If you want to create another user account to login rather than the estate
account, then type "create user" on the Tranquillity console and follow the prompts.

Helpful resources:
 * http://opensimulator.org/wiki/Configuration
 * http://opensimulator.org/wiki/Configuring_Regions

# Connecting to your Tranquillity

By default your sim will be available for login on port 9000.  You can login by
adding -loginuri http://127.0.0.1:9000 to the command that starts Second Life
(e.g. in the Target: box of the client icon properties on Windows).  You can
also login using the network IP address of the machine running Tranquillity (e.g.
http://192.168.1.2:9000)

To login, use the avatar details that you gave for your estate ownership or the
one you set up using the "create user" command.

# Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md) and [HUMANS.md](HUMANS.md). The code is mapped in
[ARCHITECTURE.md](ARCHITECTURE.md), and AI coding agents follow [AGENTS.md](AGENTS.md). Security problems are
reported privately, as [Docs/SECURITY.md](Docs/SECURITY.md) explains.

# Bug reports

In the very likely event of bugs biting you we encourage you to see whether the problem 
has already been reported on the [Tranquillity Issue Tracking System](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/issues).

If your bug has already been reported, you might want to add to the
bug description and supply additional information.

If your bug has not been reported yet, file an issue report. [Docs/BUG_REPORTING.md](Docs/BUG_REPORTING.md)
lists what to include (version, setup, steps, log lines) and what to remove first (passwords, connection
strings, capability URLs).


# More Information on Tranquillity

More extensive information on building, running, and configuring
Tranquillity, as well as how to report bugs, and participate in the Tranquillity
project can always be found at https://github.com/OpenSim-NGC/OpenSim-Tranquillity.

Thanks for trying Tranquillity, we hope it is a pleasant experience.

