FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
RUN mkdir -p /app/config /app/data /app/logdata && chown app:app /app/logdata
ENV LOGDIR=/app/logdata

USER app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG configuration=Release
WORKDIR /src
COPY . .

RUN dotnet restore "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj"

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "Source/OpenSim.Server.GridServer/OpenSim.Server.GridServer.csproj" --no-restore -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS opensim.server.gridserver
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["/app/Scripts/grid-start.sh"]

