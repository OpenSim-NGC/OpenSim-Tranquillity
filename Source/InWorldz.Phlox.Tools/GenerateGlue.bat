bin\Debug\InWorldz.Phlox.Tools.exe defgen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Types\Defaults.cs
bin\Debug\InWorldz.Phlox.Tools.exe shimgen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Glue\SyscallShim.cs
bin\Debug\InWorldz.Phlox.Tools.exe apigen ..\..\grammar\funcs.txt ..\..\grammar\Shim.stg > ..\InWorldz.Phlox\Glue\ISystemAPI.cs