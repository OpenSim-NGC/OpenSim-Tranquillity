using System;
using System.Reflection;
using System.Runtime.Loader;

namespace OpenSim.Framework.AssemblyLoader;

public class OpenSimAssemblyLoadContext : AssemblyLoadContext
{
    public OpenSimAssemblyLoadContext() : base(isCollectible: true)
    {
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        return null; // Defer loading to the default context
    }
}
