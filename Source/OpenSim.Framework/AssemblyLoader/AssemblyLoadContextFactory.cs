using System;
using System.Runtime.Loader;

namespace OpenSim.Framework.AssemblyLoader;

public class AssemblyLoadContextFactory : IAssemblyLoadContextFactory
{
    public AssemblyLoadContext CreateLoadContext()
    {
        return new OpenSimAssemblyLoadContext();
    }
}