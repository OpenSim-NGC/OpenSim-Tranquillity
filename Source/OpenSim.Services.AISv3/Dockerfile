FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5126

ENV ASPNETCORE_URLS=http://+:5126

USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG configuration=Release
WORKDIR /src
COPY ["Source/OpenSim.Services.AISv3/OpenSim.Services.AISv3.csproj", "Source/OpenSim.Services.AISv3/"]
RUN dotnet restore "Source/OpenSim.Services.AISv3/OpenSim.Services.AISv3.csproj"
COPY . .
WORKDIR "/src/Source/OpenSim.Services.AISv3"
RUN dotnet build "OpenSim.Services.AISv3.csproj" -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "OpenSim.Services.AISv3.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OpenSim.Services.AISv3.dll"]
