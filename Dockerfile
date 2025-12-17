FROM mcr.microsoft.com/dotnet/sdk:8.0 as build
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/sdk:8.0 
WORKDIR /App
COPY --from=build /App/out .
VOLUME ["/config", "/data"]
ENTRYPOINT ["dotnet", "OpenSim.dll"]
