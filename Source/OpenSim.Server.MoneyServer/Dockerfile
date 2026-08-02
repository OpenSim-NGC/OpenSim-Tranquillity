FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8008
RUN mkdir -p /app/config
RUN mkdir -p /app/data

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src

# Copy the top-level nuget.config so the container uses the same package sources
# and credentials mechanism as regular (host) builds.
COPY ["nuget.config", "./"]

# Copy only the project files to leverage Docker layer caching for dependencies
COPY ["Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj", "Source/OpenSim.Server.MoneyServer/"]
COPY ["Source/OpenSim.Data/OpenSim.Data.csproj", "Source/OpenSim.Data/"]
COPY ["Source/OpenSim.Data.MySQL/OpenSim.Data.MySQL.csproj", "Source/OpenSim.Data.MySQL/"]
COPY ["Source/OpenSim.Data.MySQL.MoneyData/OpenSim.Data.MySQL.MoneyData.csproj", "Source/OpenSim.Data.MySQL.MoneyData/"]
COPY ["Source/OpenSim.Framework/OpenSim.Framework.csproj", "Source/OpenSim.Framework/"]
COPY ["Source/OpenSim.Framework.Console/OpenSim.Framework.Console.csproj", "Source/OpenSim.Framework.Console/"]
COPY ["Source/OpenSim.Framework.Servers/OpenSim.Framework.Servers.csproj", "Source/OpenSim.Framework.Servers/"]
COPY ["Source/OpenSim.Framework.Servers.HttpServer/OpenSim.Framework.Servers.HttpServer.csproj", "Source/OpenSim.Framework.Servers.HttpServer/"]
COPY ["Source/OpenSim.Region.Framework/OpenSim.Region.Framework.csproj", "Source/OpenSim.Region.Framework/"]
COPY ["Source/OpenSim.Region.OptionalModules/OpenSim.Region.OptionalModules.csproj", "Source/OpenSim.Region.OptionalModules/"]
COPY ["Source/OpenSim.Server.Base/OpenSim.Server.Base.csproj", "Source/OpenSim.Server.Base/"]
COPY ["Source/OpenSim.Server.Handlers/OpenSim.Server.Handlers.csproj", "Source/OpenSim.Server.Handlers/"]
COPY ["Source/OpenSim.Services.Interfaces/OpenSim.Services.Interfaces.csproj", "Source/OpenSim.Services.Interfaces/"]

# Mount the secrets to supply the GitHub credentials referenced by nuget.config
# (%GITHUB_ACTOR% / %GITHUB_TOKEN%) and restore packages in one layer.
RUN --mount=type=secret,id=github_actor \
    --mount=type=secret,id=github_token \
    export GITHUB_ACTOR=$(cat /run/secrets/github_actor) && \
    export GITHUB_TOKEN=$(cat /run/secrets/github_token) && \
    dotnet restore "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj"

# Build the project after copying all source files
COPY . .
RUN dotnet build "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" --no-restore -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.moneyserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Server.MoneyServer.dll"]
