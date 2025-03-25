using System;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;

using Microsoft.Extensions.Configuration;
using Nini.Config;

namespace OpenSim.Services.Connectors;

public class BaseServiceConnector
{
    protected string ServiceURI(IConfiguration configuration, string sectionName, string serviceURI)
    {   
        var section = configuration.GetSection(sectionName);
        if (section.Exists() is false)
        {
            throw new Exception($"{sectionName} missing from configuration");
        }

        // Assume serviceURI is a key and look it up.  If not found use the value directly.
        var uri = configuration.GetValue(serviceURI, string.Empty);
        if (uri == String.Empty)
        {
            throw new Exception($"serviceURI {serviceURI} missing from {sectionName}");
        }

        return uri;
    }
    
    protected string ServiceURI(string serverURI)
    {
        return serverURI.TrimEnd('/') + "/experience"; /// XXXX
    }

    protected IServiceAuth AuthType(IConfiguration config, string section)
    {
        IServiceAuth m_Auth = null;
        var authType = Util.GetConfigVarFromSections<string>(config, "AuthType", new string[] { "Network", section }, "None");

        switch (authType)
        {
            case "BasicHttpAuthentication":
                m_Auth = new BasicHttpAuthentication(config, section);
                break;
        }

        return m_Auth;
    }
}

