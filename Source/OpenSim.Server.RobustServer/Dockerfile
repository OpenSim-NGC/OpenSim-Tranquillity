FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
<<<<<<< HEAD:Source/OpenSim.Server.RobustServer/Dockerfile
COPY ["Source/OpenSim.Server.RobustServer/OpenSim.Server.RobustServer.csproj", "Source/OpenSim.Server.RobustServer/"]
RUN dotnet restore "Source/OpenSim.Server.RobustServer/OpenSim.Server.RobustServer.csproj"
COPY . .
WORKDIR "/src/Source/OpenSim.Server.RobustServer"
RUN dotnet build "OpenSim.Server.RobustServer.csproj" -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "OpenSim.Server.RobustServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false
=======
COPY ["OpenSim/Server/Robust.csproj", "OpenSim/Server/"]
RUN dotnet restore "OpenSim/Server/Robust.csproj"
COPY . .
WORKDIR "/src/OpenSim/Server"
RUN dotnet build "Robust.csproj" -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Robust.csproj" -c $configuration -o /app/publish /p:UseAppHost=false
>>>>>>> origin/develop:OpenSim/Server/Dockerfile

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
<<<<<<< HEAD:Source/OpenSim.Server.RobustServer/Dockerfile
ENTRYPOINT ["dotnet", "OpenSim.Server.RobustServer.dll"]
=======
ENTRYPOINT ["dotnet", "Robust.dll"]
>>>>>>> origin/develop:OpenSim/Server/Dockerfile
