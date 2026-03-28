#!/bin/sh

# dotnet publish Tranquillity.sln -c Release -r linux-x64 --self-contained false -o ./publish/linux-x64
# dotnet publish Tranquillity.sln -c Release -r win-x64 --self-contained false -o ./publish/win-x64
dotnet publish "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" -c Release -r linux-x64 -o ./publish/OpenSim.Server.GridServer
dotnet publish "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" -c Release -r linux-x64 -o ./publish/OpenSim.Server.RegionServer
dotnet publish "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" -c Release -r linux-x64 -o ./publish/OpenSim.Server.MoneyServer