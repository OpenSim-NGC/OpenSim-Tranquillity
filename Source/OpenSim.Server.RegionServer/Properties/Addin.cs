using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AddinRoot("OpenSim", OpenSim.VersionInfo.VersionNumber)]
[assembly: ImportAddinAssembly("OpenSim.Framework.dll")]
