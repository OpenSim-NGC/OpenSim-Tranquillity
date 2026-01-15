#!/bin/sh

mkdir -p publish

dotnet publish -o publish/OpenSim.Server.RegionServer Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
dotnet publish -o publish/OpenSim.Server.GridtServer Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj
dotnet publish -o publish/OpenSim.Server.MoneyServer  Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj
