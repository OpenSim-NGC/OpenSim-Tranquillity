FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app
RUN mkdir -p /app/config
RUN mkdir -p /app/data

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
COPY ["Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj", "Source/OpenSim.Server.MoneyServer/"]
COPY . .

RUN dotnet restore "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj"
RUN dotnet build "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" --no-restore -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.MoneyServer/OpenSim.Server.MoneyServer.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.moneyserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Server.MoneyServer.dll"]
