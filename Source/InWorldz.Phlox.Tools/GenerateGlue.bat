REM From HalcyonGrid/phlox (Source/Halcyon.Phlox.Tools/GenerateGlue.bat, commit e805a56).
REM Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
REM Modified 2026 by Legion Builds.
bin\Debug\InWorldz.Phlox.Tools.exe defgen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Types\Defaults.cs
bin\Debug\InWorldz.Phlox.Tools.exe shimgen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Glue\SyscallShim.cs
bin\Debug\InWorldz.Phlox.Tools.exe apigen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Glue\ISystemAPI.cs