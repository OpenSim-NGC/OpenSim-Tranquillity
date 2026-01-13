#!/bin/sh

mkdir -p publish

dotnet publish -o publish/OpenSim.Server.RegionServer Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj
dotnet publish -o publish/OpenSim.Server.RobustServer Source/OpenSim.Server.RobustServer/OpenSim.Server.RobustServer.csproj
dotnet publish -o publish/OpenSim.Server.MoneyServer  Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj
