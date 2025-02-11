using System.Reflection;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.AssemblyLoader;

public class AssemblyLoader : IAssemblyLoader
{
    private readonly IAssemblyLoadContextFactory _loadContextFactory;
    private readonly ILogger _logger;

    public AssemblyLoader(
        IAssemblyLoadContextFactory loadContextFactory,
        ILogger<AssemblyLoader> logger
        )
    {
        _loadContextFactory = loadContextFactory;
        _logger = logger;
    }

    public void LoadDll(string dllPath)
    {
        var assemblyLoadContext = _loadContextFactory.CreateLoadContext();
        Assembly assembly = assemblyLoadContext.LoadFromAssemblyPath(dllPath);

        if (assembly != null)
        {
            _logger.LogInformation($"Loaded assembly: {assembly.FullName}");
        }
        else
        {
            _logger.LogError($"Failed to load an assembly from {dllPath}");
        }
    }
}
