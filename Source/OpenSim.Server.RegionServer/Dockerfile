FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app
RUN mkdir -p /app/config /app/data /app/logdata && chown app:app /app/logdata
ENV LOGDIR=/app/logdata

USER app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG configuration=Release
WORKDIR /src
COPY . .

RUN dotnet restore "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj"

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.RegionServer/OpenSim.Server.RegionServer.csproj" --no-restore -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.regionserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["/app/Scripts/region-start.sh"]