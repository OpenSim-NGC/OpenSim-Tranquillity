#!/bin/sh
dotnet publish "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" -c Release -r linux-x64 -o ./publish/linux-x64/OpenSim.Server.GridServer
dotnet publish "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" -c Release -r linux-x64 -o ./publish/linux-x64/OpenSim.Server.RegionServer
dotnet publish "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" -c Release -r linux-x64 -o ./publish/linux-x64/OpenSim.Server.MoneyServer

dotnet publish "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" -c Release -r win-x64 -o ./publish/win-x64/OpenSim.Server.GridServer
dotnet publish "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" -c Release -r win-x64 -o ./publish/win-x64/OpenSim.Server.RegionServer
dotnet publish "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" -c Release -r win-x64 -o ./publish/win-x64/OpenSim.Server.MoneyServer

