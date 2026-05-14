# Plugin Development Guide

This guide explains how to add new plugins after the DotNetCorePlugins migration.
The supported registration model is provider-based code registration using
`IPluginRegistryProvider` and `PluginDescriptor`.

## Quick Start Checklist

- Create or choose a project under `Source/` or `Addons/`.
- Implement the plugin interface for your target extension point.
- Add a `PluginRegistration` class that implements `IPluginRegistryProvider`.
- Register your plugin with the correct extension path.
- Build and verify discovery with a normal debug build.

## 1) Choose the Extension Point

Use the extension path that matches your plugin interface.

| Extension path | Interface |
|---|---|
| `/OpenSim/Startup` | `IApplicationPlugin` |
| `/OpenSim/RegionModules` | `IRegionModuleBase` (`ISharedRegionModule` or `INonSharedRegionModule`) |
| `/OpenSim/WindModule` | `IWindModelPlugin` |
| `/Robust/Connector` | `IRobustConnector` |

Reference interfaces:
- `Source/OpenSim.Region.Framework/Interfaces/IApplicationPlugin.cs`
- `Source/OpenSim.Region.Framework/Interfaces/IRegionModuleBase.cs`
- `Source/OpenSim.Region.Framework/Interfaces/ISharedRegionModule.cs`
- `Source/OpenSim.Region.Framework/Interfaces/INonSharedRegionModule.cs`
- `Source/OpenSim.Region.Framework/Interfaces/IWindModelPlugin.cs`
- `Source/OpenSim.Server.Base/ServerUtils.cs` (`IRobustConnector`)

## 2) Implement the Plugin Class

Implement the interface for your chosen extension point.

For region modules, prefer `ISharedRegionModule` unless you specifically need
per-scene instances via `INonSharedRegionModule`.

### Example: minimal shared region module

```csharp
using System;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace Example.Modules
{
    public class ExampleRegionModule : ISharedRegionModule
    {
        public string Name => "ExampleRegionModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
        }
    }
}
```

## 3) Add Provider-Based Registration

Create `PluginRegistration.cs` in the same assembly and implement
`IPluginRegistryProvider`.

### Example: direct type registration

```csharp
using OpenSim.Framework;

namespace Example.Modules
{
    public class PluginRegistration : IPluginRegistryProvider
    {
        public void RegisterPlugins(PluginRegistry registry)
        {
            registry.Register(
                "/OpenSim/RegionModules",
                new PluginDescriptor(
                    "ExampleRegionModule",
                    typeof(ExampleRegionModule),
                    "ExampleRegionModule",
                    "1.0"));
        }
    }
}
```

### Optional pattern: registration by type name

Use this when you need resilient registration for optional or variant types.

```csharp
using System;
using System.Reflection;
using OpenSim.Framework;

namespace Example.Modules
{
    public class PluginRegistration : IPluginRegistryProvider
    {
        public void RegisterPlugins(PluginRegistry registry)
        {
            RegisterByName(
                registry,
                "/OpenSim/RegionModules",
                "ExampleRegionModule",
                "Example.Modules.ExampleRegionModule",
                "ExampleRegionModule");
        }

        private static void RegisterByName(
            PluginRegistry registry,
            string extensionPath,
            string id,
            string typeName,
            string displayName)
        {
            Assembly assembly = typeof(PluginRegistration).Assembly;
            Type type = assembly.GetType(typeName, false);
            if (type == null)
                return;

            registry.Register(
                extensionPath,
                new PluginDescriptor(id, type, displayName, "1.0"));
        }
    }
}
```

## 4) Project Setup Notes

- Do not add Mono.Addins package references.
- Do not add `.addin.xml` manifests.
- Keep plugin implementation and `PluginRegistration` in the same assembly.
- Make sure the plugin assembly is built into the server deployment output where
  discovery scans for assemblies.

Reference types used for registration:
- `Source/OpenSim.Framework/PluginRegistry.cs`

## 5) New Plugin Project Skeletons

Use these skeletons when creating a new plugin project.

### Option A: Source tree plugin

Recommended for core/runtime plugins maintained with the main server code.

Suggested layout:

```text
Source/
  OpenSim.Region.ExampleModule/
    OpenSim.Region.ExampleModule.csproj
    ExampleRegionModule.cs
    PluginRegistration.cs
```

Minimal `csproj` pattern:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyTitle>OpenSim.Region.ExampleModule</AssemblyTitle>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="log4net" Version="3.3.0" />
    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.5" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Nini" HintPath="../../Library/Nini.dll" />
    <Reference Include="OpenMetaverse" HintPath="../../Library/OpenMetaverse.dll" />
    <Reference Include="OpenMetaverseTypes" HintPath="../../Library/OpenMetaverse.Types.dll" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../Source/OpenSim.Framework/OpenSim.Framework.csproj" />
    <ProjectReference Include="../../Source/OpenSim.Region.Framework/OpenSim.Region.Framework.csproj" />
  </ItemGroup>
</Project>
```

### Option B: Addons tree plugin

Recommended for optional/distribution-specific plugins.

Suggested layout:

```text
Addons/
  OpenSim.Addons.ExampleModule/
    OpenSim.Addons.ExampleModule.csproj
    ExampleRegionModule.cs
    PluginRegistration.cs
```

Minimal `csproj` pattern:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyTitle>OpenSim.Addons.ExampleModule</AssemblyTitle>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="log4net" Version="3.3.0" />
    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.5" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Nini" HintPath="..\..\Library\Nini.dll" />
    <Reference Include="OpenMetaverse" HintPath="..\..\Library\OpenMetaverse.dll" />
    <Reference Include="OpenMetaverseTypes" HintPath="..\..\Library\OpenMetaverse.Types.dll" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Source\OpenSim.Framework\OpenSim.Framework.csproj" />
    <ProjectReference Include="..\..\Source\OpenSim.Region.Framework\OpenSim.Region.Framework.csproj" />
  </ItemGroup>
</Project>
```

Notes for both options:

- Add only references your plugin actually needs.
- If your plugin uses scene/runtime APIs, include `OpenSim.Region.Framework`.
- If your plugin only registers startup behavior, follow startup plugin examples and
  include the corresponding server/framework references.

## 6) First-Run INI Toggles

No discovery backend toggle is required. Runtime discovery always uses
DotNetCorePlugins.

Module activation is still controlled by module-specific configuration.
Common patterns in this repository include:

- Dedicated section presence/values (for example, module-specific sections used by
  optional modules).
- Service connector wiring with `LocalServiceModule = Assembly.dll:TypeName`
  in service config sections.

When validating a new plugin, check both discovery and module-specific config
conditions before assuming discovery failed.

## 7) Build and Validate

From repository root:

```bash
dotnet build --configuration Debug
```

For focused validation, build the plugin project directly and then run a server
profile that should load it.

## 8) Troubleshooting

- Plugin not discovered:
  - Confirm `PluginRegistration` exists and implements `IPluginRegistryProvider`.
  - Confirm registration uses the correct extension path.
  - Confirm plugin type implements the expected interface.
  - Confirm assembly is present in the scanned plugin directory.
- Plugin discovered but not active:
  - Check module-specific configuration in INI files.
  - Check startup logs for discovery summary and module initialization errors.

## 9) Existing In-Repo Examples

- Startup plugin registration:
  - `Source/OpenSim.ApplicationPlugins.LoadRegions/PluginRegistration.cs`
- Region modules registration using direct type descriptors:
  - `Source/OpenSim.Region.ClientStack.LindenCaps/PluginRegistration.cs`
- Region modules registration using assembly-local type-name resolution:
  - `Source/OpenSim.Region.OptionalModules/PluginRegistration.cs`

## Policy

New plugins must use provider-based registration.
Mono.Addins manifests are historical and not supported for new development.
