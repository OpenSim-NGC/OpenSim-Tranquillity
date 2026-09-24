MIGRATION_NAME="InitialCreate"

if [ $# -eq 0 ]; then
    MIGRATION_NAME="InitialCreate"
elif [ $# -eq 1 ]; then
    MIGRATION_NAME="$1"
elif [ $# -gt 1 ]; then
    echo "Usage: $0 migrationName" >&2
    exit 1
fi

dotnet ef migrations add ${MIGRATION_NAME}_Identity --context IdentityContext -o Migrations/Identity 
dotnet ef migrations add ${MIGRATION_NAME}_Core --context OpenSimCoreContext -o Migrations/Core
dotnet ef migrations add ${MIGRATION_NAME}_Region --context OpenSimRegionContext -o Migrations/Region
dotnet ef migrations add ${MIGRATION_NAME}_Economy --context OpenSimEconomyContext -o Migrations/Economy
dotnet ef migrations add ${MIGRATION_NAME}_Search --context OpenSimSearchContext -o Migrations/Search

# dotnet ef migrations add InitialCreate_Marketplace -o Models/Marketplace/Migrations --context OpenSimMarketplaceContext

exit 0
