FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app
RUN mkdir -p /app/config
RUN mkdir -p /app/data

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
#COPY ["Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj", "Source/OpenSim.Server.GridServer/"]
COPY . .

# Mount the secrets to supply the GitHub credentials referenced by nuget.config
# (%GITHUB_ACTOR% / %GITHUB_TOKEN%) and restore packages in one layer.
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    export GITHUB_ACTOR=$(cat /run/secrets/github_actor) && \
    export GITHUB_TOKEN=$(cat /run/secrets/github_token) && \
    dotnet restore "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj"
#COPY . .
# WORKDIR "/src/Source/OpenSim.Server.GridServer"
RUN dotnet build "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" --skip-restore -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.gridserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Server.GridServer.dll"]

