Welcome to Tranquillity !

# Overview

Tranquillity is an OpenSimulator derivitive forked to development using up to date C# and dotnet architecural
patterns and technology.  Licensing from the original OpenSimulator (which is a BSD Licensed Open Source project)
has been retained in files directly derived from OpenSimulator.  Some new elements are licensed MPLv2 and are
licensed by their respective contributor.   Current License information is found in the LICENSE.txt file at
the top of the project. 

Tranquillity is a project that endeavours to develop a functioning virtual worlds server platform capable of 
supporting multiple clients and servers in a heterogeneous grid structure while leveraging the architectural
patterns provided by the dotnet and C# platform. Tranquillity is written in C#, and requires a dotnet SDK to compile
and runtime to execute compiled binaries.

# Compiling Tranquillity

Please see BUILDING.md

# Running Tranquillity on Windows

You will need dotnet 8.0 runtime (https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

To run Tranquillity from a command prompt

 * cd to the directory where you unpacked Tranquillity.
 * Review and change configuration files (.ini) for your needs. see the "Configuring Tranquillity" section
 * run "dotnet Tranquillity.dll"


# Running Tranquillity on Linux/Mac

You will need

 * [dotnet 8.0 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
 
To run Tranquillity, from the unpacked distribution type:

 * cd to the directory where you unpackrf Tranquillity
 * review and change configuration files (.ini) for your needs. see the "Configuring Tranquillity" section
 * run "dotnet Tranquillity.dll"
 

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

# Bug reports

In the very likely event of bugs biting you we encourage you to see whether the problem 
has already been reported on the [Tranquillity Issue Tracking System](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/issues).

If your bug has already been reported, you might want to add to the
bug description and supply additional information.

If your bug has not been reported yet, file an issue report. 
Useful information to include:
 * description of what went wrong
 * stack trace
 * Tranquillity.log (attach as file)
 * Tranquillity.ini (attach as file)


# More Information on Tranquillity

More extensive information on building, running, and configuring
Tranquillity, as well as how to report bugs, and participate in the Tranquillity
project can always be found at https://github.com/OpenSim-NGC/OpenSim-Tranquillity.

Thanks for trying Tranquillity, we hope it is a pleasant experience.

