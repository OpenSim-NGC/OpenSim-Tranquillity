using System;
using System.Reflection;
using System.Runtime.Loader;

namespace OpenSim.Framework.AssemblyLoader;

public interface IAssemblyLoadContextFactory
{
    AssemblyLoadContext CreateLoadContext();
}
