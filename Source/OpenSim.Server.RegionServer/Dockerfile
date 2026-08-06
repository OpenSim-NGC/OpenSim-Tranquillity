FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app
RUN mkdir -p /app/config
RUN mkdir -p /app/data

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
COPY ["Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj", "Source/OpenSim.Server.RegionServer/"]
COPY . .

# Mount the secrets to supply the GitHub credentials referenced by nuget.config
# (%GITHUB_ACTOR% / %GITHUB_TOKEN%) and restore packages in one layer.
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    export GITHUB_ACTOR=$(cat /run/secrets/github_actor) && \
    export GITHUB_TOKEN=$(cat /run/secrets/github_token) && \
    dotnet restore "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj"
RUN dotnet build "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" --skip-restore -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.regionserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Server.RegionServer.dll"]