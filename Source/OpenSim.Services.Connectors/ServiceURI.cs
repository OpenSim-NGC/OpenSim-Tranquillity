using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;

using Microsoft.Extensions.Configuration;

namespace OpenSim.Services.Connectors;

public static class ServiceURI
{
    public static string LookupServiceURI(IConfiguration configuration, string sectionName, string serviceURI)
    {
        var section = configuration.GetSection(sectionName);
        return LookupServiceURI(section, serviceURI);
    }

    public static string LookupServiceURI(
        IConfigurationSection section, 
        string serviceURI)
    { 
        if (section.Exists() is false)
        {
            throw new Exception($"{section} missing from configuration");
        }

        var uri = section.GetValue(serviceURI, string.Empty);
        if (string.IsNullOrEmpty(uri))
        {
            throw new Exception($"No ServerURI {serviceURI} in section {section.Path}");
        }

        return uri.TrimEnd('/');
    }
}

