# OpenMetaverse NuGet Migration Patch Report

Generated: 2026-05-18 21:38:30 UTC

## Scope

- Changed csproj files: 93
- Diff stat (csproj only): 93 files changed, 697 insertions(+), 622 deletions(-)
- Folder breakdown:
  - Addons: 9
  - Source: 63
  - Tests: 20
  - ThirdParty: 1

## Validation

- Build command: dotnet build Tranquillity.sln
- Build result: succeeded (warnings only, no errors)
- Legacy reference scan result:
  - OpenMetaverse/OpenMetaverseTypes/OpenMetaverse.StructuredData/log4net Reference entries remaining: none

## Representative Patch Hunks

### Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj

```diff
diff --git a/Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj b/Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj
index be91b3a6a2..c790446653 100644
--- a/Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj
+++ b/Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj
@@ -8,7 +8,7 @@
   </PropertyGroup>
   <ItemGroup>
     <Reference Include="Nini" HintPath="../../Library/Nini.dll" />
-    <Reference Include="OpenMetaverseTypes" HintPath="../../Library/OpenMetaverse.Types.dll" />
+    
   </ItemGroup>
   <ItemGroup>
     <ProjectReference Include="../../Source/OpenSim.Framework/OpenSim.Framework.csproj" />
@@ -18,7 +18,10 @@
     <Folder Include="Properties/" />
   </ItemGroup>
   <ItemGroup>
-    <PackageReference Include="log4net" Version="3.3.0" />
-    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.5" />
+    <PackageReference Include="log4net" Version="3.3.1" />
+    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.8" />
+  </ItemGroup>
+  <ItemGroup>
+    <PackageReference Include="OpenMetaverse.Types" Version="1.2.8-beta" />
   </ItemGroup>
 </Project>
```

### Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj

```diff
diff --git a/Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj b/Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj
index f4c0a63e7a..6e680aee3b 100644
--- a/Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj
+++ b/Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj
@@ -7,11 +7,11 @@
     <Copyright>OpenSimulator developers</Copyright>
   </PropertyGroup>
   <ItemGroup>
-    <Reference Include="log4net" HintPath="../../Library/log4net.dll" />
+    
     <Reference Include="Nini" HintPath="../../Library/Nini.dll" />
-    <Reference Include="OpenMetaverse" HintPath="../../Library/OpenMetaverse.dll" />
-    <Reference Include="OpenMetaverse.StructuredData" HintPath="../../Library/OpenMetaverse.StructuredData.dll" />
-    <Reference Include="OpenMetaverseTypes" HintPath="../../Library/OpenMetaverse.Types.dll" />
+    
+    
+    
     <Reference Include="xmlrpc" HintPath="../../Library/xmlrpc.dll" />
   </ItemGroup>
   <ItemGroup>
@@ -22,7 +22,12 @@
     <ProjectReference Include="../../Source/OpenSim.Framework.Servers.HttpServer/OpenSim.Framework.Servers.HttpServer.csproj" />
   </ItemGroup>
   <ItemGroup>
-    <PackageReference Include="log4net" Version="3.3.0" />
-    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.5" />
+    <PackageReference Include="log4net" Version="3.3.1" />
+    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.8" />
+  </ItemGroup>
+  <ItemGroup>
+    <PackageReference Include="OpenMetaverse" Version="1.2.8-beta" />
+    <PackageReference Include="OpenMetaverse.Types" Version="1.2.8-beta" />
+    <PackageReference Include="OpenMetaverse.StructuredData" Version="1.2.8-beta" />
   </ItemGroup>
 </Project>
```

### Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj

```diff
diff --git a/Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj b/Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
index 62a311f155..a527b2bc85 100644
--- a/Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
+++ b/Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
@@ -11,11 +11,11 @@
   
   <ItemGroup>
     <Reference Include="NDesk.Options" HintPath="../../Library/NDesk.Options.dll" />
-    <Reference Include="log4net" HintPath="../../Library/log4net.dll" />
+    
     <Reference Include="Nini" HintPath="../../Library/Nini.dll" />
-    <Reference Include="OpenMetaverse" HintPath="../../Library/OpenMetaverse.dll" />
-    <Reference Include="OpenMetaverse.StructuredData" HintPath="../../Library/OpenMetaverse.StructuredData.dll" />
-    <Reference Include="OpenMetaverseTypes" HintPath="../../Library/OpenMetaverse.Types.dll" />
+    
+    
+    
     <Reference Include="xmlrpc" HintPath="../../Library/xmlrpc.dll" />
   </ItemGroup>
   
@@ -85,8 +85,8 @@
     <PackageReference Include="SkiaSharp" Version="3.119.2" />
     <PackageReference Include="SkiaSharp.NativeAssets.Win32" Version="3.119.2" />
     <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="3.119.2" />
-    <PackageReference Include="log4net" Version="3.3.0" />
-    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.5" />
+    <PackageReference Include="log4net" Version="3.3.1" />
+    <PackageReference Include="System.Configuration.ConfigurationManager" Version="10.0.8" />
   </ItemGroup>
 
   <ItemGroup>
@@ -107,4 +107,9 @@
     </Content>
   </ItemGroup>
   
+  <ItemGroup>
+    <PackageReference Include="OpenMetaverse" Version="1.2.8-beta" />
+    <PackageReference Include="OpenMetaverse.Types" Version="1.2.8-beta" />
+    <PackageReference Include="OpenMetaverse.StructuredData" Version="1.2.8-beta" />
+  </ItemGroup>
 </Project>
```

## Full Changed File List

- Addons/Gloebit.GloebitMoneyModule/Gloebit.GloebitMoneyModule.csproj
- Addons/OpenSim.Addons.Groups/OpenSim.Addons.Groups.csproj
- Addons/OpenSim.Addons.OfflineIM/OpenSim.Addons.OfflineIM.csproj
- Addons/OpenSimMutelist/OpenSimMutelist.Modules.csproj
- Addons/OpenSimSearch/OpenSimSearch.Modules.csproj
- Addons/os-webrtc-janus/Janus/WebRtcJanusService.csproj
- Addons/os-webrtc-janus/WebRtcVoice/WebRtcVoice.csproj
- Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.csproj
- Addons/os-webrtc-janus/WebRtcVoiceServiceModule/WebRtcVoiceServiceModule.csproj
- Source/OpenSim.ApplicationPlugins.LoadRegions/OpenSim.ApplicationPlugins.LoadRegions.csproj
- Source/OpenSim.ApplicationPlugins.RegionModulesController/OpenSim.ApplicationPlugins.RegionModulesController.csproj
- Source/OpenSim.ApplicationPlugins.RemoteController/OpenSim.ApplicationPlugins.RemoteController.csproj
- Source/OpenSim.Capabilities.Handlers/OpenSim.Capabilities.Handlers.csproj
- Source/OpenSim.Capabilities/OpenSim.Capabilities.csproj
- Source/OpenSim.ConsoleClient/OpenSim.ConsoleClient.csproj
- Source/OpenSim.Data.Model/OpenSim.Data.Model.csproj
- Source/OpenSim.Data.MySQL.MoneyData/OpenSim.Data.MySQL.MoneyData.csproj
- Source/OpenSim.Data.MySQL/OpenSim.Data.MySQL.csproj
- Source/OpenSim.Data.Null/OpenSim.Data.Null.csproj
- Source/OpenSim.Data.PGSQL/OpenSim.Data.PGSQL.csproj
- Source/OpenSim.Data.SQLite/OpenSim.Data.SQLite.csproj
- Source/OpenSim.Data/OpenSim.Data.csproj
- Source/OpenSim.Framework.AssetLoader.Filesystem/OpenSim.Framework.AssetLoader.Filesystem.csproj
- Source/OpenSim.Framework.Console/OpenSim.Framework.Console.csproj
- Source/OpenSim.Framework.Monitoring/OpenSim.Framework.Monitoring.csproj
- Source/OpenSim.Framework.Serialization/OpenSim.Framework.Serialization.csproj
- Source/OpenSim.Framework.Servers.HttpServer/OpenSim.Framework.Servers.HttpServer.csproj
- Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj
- Source/OpenSim.Framework/OpenSim.Framework.csproj
- Source/OpenSim.Region.ClientStack.LindenCaps/OpenSim.Region.ClientStack.LindenCaps.csproj
- Source/OpenSim.Region.ClientStack.LindenUDP/OpenSim.Region.ClientStack.LindenUDP.csproj
- Source/OpenSim.Region.CoreModules/OpenSim.Region.CoreModules.csproj
- Source/OpenSim.Region.Framework/OpenSim.Region.Framework.csproj
- Source/OpenSim.Region.OptionalModules/OpenSim.Region.OptionalModules.csproj
- Source/OpenSim.Region.PhysicsModules.BasicPhysics/OpenSim.Region.PhysicsModules.BasicPhysics.csproj
- Source/OpenSim.Region.PhysicsModules.BulletS/OpenSim.Region.PhysicsModules.BulletS.csproj
- Source/OpenSim.Region.PhysicsModules.ConvexDecompositionDotNet/OpenSim.Region.PhysicsModules.ConvexDecompositionDotNet.csproj
- Source/OpenSim.Region.PhysicsModules.Meshing/OpenSim.Region.PhysicsModules.Meshing.csproj
- Source/OpenSim.Region.PhysicsModules.POS/OpenSim.Region.PhysicsModules.POS.csproj
- Source/OpenSim.Region.PhysicsModules.SharedBase/OpenSim.Region.PhysicsModules.SharedBase.csproj
- Source/OpenSim.Region.PhysicsModules.ubODE/OpenSim.Region.PhysicsModules.ubODE.csproj
- Source/OpenSim.Region.PhysicsModules.ubODEMeshing/OpenSim.Region.PhysicsModules.ubODEMeshing.csproj
- Source/OpenSim.Region.ScriptEngine.Shared/OpenSim.Region.ScriptEngine.Shared.csproj
- Source/OpenSim.Region.ScriptEngine.YEngine/OpenSim.Region.ScriptEngine.YEngine.csproj
- Source/OpenSim.Server.Base/OpenSim.Server.Base.csproj
- Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj
- Source/OpenSim.Server.Handlers/OpenSim.Server.Handlers.csproj
- Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj
- Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
- Source/OpenSim.Services.AssetService/OpenSim.Services.AssetService.csproj
- Source/OpenSim.Services.AuthenticationService/OpenSim.Services.AuthenticationService.csproj
- Source/OpenSim.Services.AuthorizationService/OpenSim.Services.AuthorizationService.csproj
- Source/OpenSim.Services.AvatarService/OpenSim.Services.AvatarService.csproj
- Source/OpenSim.Services.Base/OpenSim.Services.Base.csproj
- Source/OpenSim.Services.Connectors/OpenSim.Services.Connectors.csproj
- Source/OpenSim.Services.EstateService/OpenSim.Services.EstateService.csproj
- Source/OpenSim.Services.ExperienceService/OpenSim.Services.ExperienceService.csproj
- Source/OpenSim.Services.FSAssetService/OpenSim.Services.FSAssetService.csproj
- Source/OpenSim.Services.FreeswitchService/OpenSim.Services.FreeswitchService.csproj
- Source/OpenSim.Services.Friends/OpenSim.Services.FriendsService.csproj
- Source/OpenSim.Services.GridService/OpenSim.Services.GridService.csproj
- Source/OpenSim.Services.HypergridService/OpenSim.Services.HypergridService.csproj
- Source/OpenSim.Services.Interfaces/OpenSim.Services.Interfaces.csproj
- Source/OpenSim.Services.InventoryService/OpenSim.Services.InventoryService.csproj
- Source/OpenSim.Services.LLLoginService/OpenSim.Services.LLLoginService.csproj
- Source/OpenSim.Services.MapImageService/OpenSim.Services.MapImageService.csproj
- Source/OpenSim.Services.MuteListService/OpenSim.Services.MuteListService.csproj
- Source/OpenSim.Services.PresenceService/OpenSim.Services.PresenceService.csproj
- Source/OpenSim.Services.SimulationService/OpenSim.Services.SimulationService.csproj
- Source/OpenSim.Services.UserAccountService/OpenSim.Services.UserAccountService.csproj
- Source/OpenSim.Services.UserProfilesService/OpenSim.Services.UserProfilesService.csproj
- Source/Warp3D/Warp3D.csproj
- Tests/OpenSim.Capabilities.Handlers.Tests/OpenSim.Capabilities.Handlers.Tests.csproj
- Tests/OpenSim.Clients.Assets.Tests/OpenSim.Tests.Clients.AssetClient.csproj
- Tests/OpenSim.Data.Tests/OpenSim.Data.Tests.csproj
- Tests/OpenSim.Framework.PluginMigration.Tests/OpenSim.Framework.PluginMigration.Tests.csproj
- Tests/OpenSim.Framework.Serialization.Tests/OpenSim.Framework.Serialization.Tests.csproj
- Tests/OpenSim.Framework.Tests/OpenSim.Framework.Tests.csproj
- Tests/OpenSim.Performance.Tests/OpenSim.Tests.Performance.csproj
- Tests/OpenSim.Permissions.Tests/OpenSim.Tests.Permissions.csproj
- Tests/OpenSim.Region.ClientStack.LindenCaps.Tests/OpenSim.Region.ClientStack.LindenCaps.Tests.csproj
- Tests/OpenSim.Region.ClientStack.LindenUDP.Tests/OpenSim.Region.ClientStack.LindenUDP.Tests.csproj
- Tests/OpenSim.Region.CoreModules.Tests/OpenSim.Region.CoreModules.Tests.csproj
- Tests/OpenSim.Region.Framework.Tests/OpenSim.Region.Framework.Tests.csproj
- Tests/OpenSim.Region.PhysicsModules.BulletS.Tests/OpenSim.Region.PhysicsModule.BulletS.Tests.csproj
- Tests/OpenSim.Region.ScriptEngine.Tests/OpenSim.Region.ScriptEngine.Tests.csproj
- Tests/OpenSim.Robust.Tests/Robust.Tests.csproj
- Tests/OpenSim.Server.Handlers.Tests/OpenSim.Server.Handlers.Tests.csproj
- Tests/OpenSim.Services.InventoryService.Tests/OpenSim.Services.InventoryService.Tests.csproj
- Tests/OpenSim.Stress.Tests/OpenSim.Tests.Stress.csproj
- Tests/OpenSim.Tests.Common/OpenSim.Tests.Common.csproj
- Tests/Warp3D.Tests/Warp3D.Tests.csproj
- ThirdParty/SmartThreadPool/SmartThreadPool.csproj
