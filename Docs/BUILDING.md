# git clone

get or update source from git

 `git clone https://github.com/OpenSim-NGC/OpenSim-Tranquillity.git`
  
### Building
 Prebuild is no longer used.  There is a top level Solution (sln) and csproj files for each
 of the projects in the solution.  The projects are designed to be publishable which 
 optimizes the build for a specific platform.  Future versions will support building into a '
 container and AOT compilation:
 
 To run a build from a CLI run:

 dotnet publish --configuration Debug -r linux-x64
 dotnet publish --configuration Release -r linux-x64

 dotnet publish --configuration Debug -r win-x64
 dotnet publish --configuration Release -r win-x64

If no configuration is specified the default is a release build. If no platform is specified default 
is the platform being used for the compilation.

The output from the publish will be in build/<Configuration>/net8.0/<platform>/

Where Configuration is either Debug or Release and Platform is either linux-x64 or win-x64 as shown above.

Either configuration will do a NuGet restore (dotnet restore) to restore any required NuGet package references prior to
kicking off a build using a current version of msbuild.  The Csproj and SLN files are all designed to use the new
format for Msbuild which is simplified and really directly replaces what prebuild provided.

Configure. See below

For rebuilding and debugging use the dotnet command options
  *  clean:  `dotnet clean
  *  restore: dotnet restore
  *  debug:   dotnet publish --configuration Debug
  *  release: dotnet publish --configuration Release

# Configure #
## Standalone mode ##
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case.

On `[Architecture]` section uncomment only the line with Standalone.ini if you do now want HG, or the line with StandaloneHypergrid.ini if you do

copy the `StandaloneCommon.ini.example` to `StandaloneCommon.ini` in the `bin/config-include` directory.

The StandaloneCommon.ini file describes the database and backend services that OpenSim will use, and is set to use sqlite by default, which requires no setup.


## Grid mode ##
Each grid may have its own requirements, so FOLLOW your Grid instructions!
in general:
Copy `OpenSim.ini.example` to `OpenSim.ini` in the `bin/` directory, and verify the `[Const]` section, correcting for your case
 
On `[Architecture]` section uncomment only the line with Grid.ini if you do not want HG, or the line with GridHypergrid.ini if you do

and copy the `GridCommon.ini.example` file to `GridCommon.ini` inside the `bin/config-include` directory and edit as necessary
