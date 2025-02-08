using System;

namespace OpenSim.Framework.AssemblyLoader;

public interface IAssemblyLoader
{
    void LoadDll(string dllPath);
}
