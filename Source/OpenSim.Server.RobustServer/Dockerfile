FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
COPY ["Source/OpenSim.Server.RobustServer/OpenSim.Server.RobustServer.csproj", "Source/OpenSim.Server.RobustServer/"]
RUN dotnet restore "Source/OpenSim.Server.RobustServer/OpenSim.Server.RobustServer.csproj"
COPY . .
WORKDIR "/src/Source/OpenSim.Server.RobustServer"
RUN dotnet build "OpenSim.Server.RobustServer.csproj" -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "OpenSim.Server.RobustServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Server.RobustServer.dll"]
