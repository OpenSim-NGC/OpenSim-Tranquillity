# OpenSim.Data.Model

`OpenSim.Data.Model` provides the Entity Framework Core model layer for OpenSim NGC data packages.

This package contains:

- Entity classes mapped to existing OpenSim/MySQL schemas.
- `DbContext` implementations for the major OpenSim data domains.
- Design-time `DbContext` factories for EF Core tooling (for core, region, economy, and search contexts).

It is intended to be the shared data-model foundation used by the rest of the NGC packaging stack.

## Package Summary

- Package ID: `OpenSim.Data.Model`
- Target framework: `.NET 8` (`net10.0`)
- Database provider: Microting MySQL (`Microting.EntityFrameworkCore.MySql`, a maintained Pomelo fork)
- EF Core: `Microsoft.EntityFrameworkCore`

## Included Contexts

The package includes these primary contexts:

- `OpenSim.Data.Model.Core.OpenSimCoreContext`
	- Core grid/user/inventory/asset and related tables.
- `OpenSim.Data.Model.Region.OpenSimRegionContext`
	- Region scene/object/land/terrain tables.
- `OpenSim.Data.Model.Economy.OpenSimEconomyContext`
	- Economy balances, transactions, and sales tables.
- `OpenSim.Data.Model.Search.OpenSimSearchContext`
	- Search/classifieds/events/parcel discovery tables.
- `OpenSim.Data.Model.Identity.IdentityContext`
	- ASP.NET Identity tables used by NGC identity/auth workflows.

## Install

```bash
dotnet add package OpenSim.Data.Model
```

## Runtime Configuration

Register the required contexts in your application startup:

```csharp
using Microsoft.EntityFrameworkCore;
using OpenSim.Data.Model.Core;
using OpenSim.Data.Model.Economy;
using OpenSim.Data.Model.Region;
using OpenSim.Data.Model.Search;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OpenSimCoreContext>(options =>
		options.UseMySql(
				builder.Configuration.GetConnectionString("OpenSimCoreConnection"),
				ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("OpenSimCoreConnection"))));

builder.Services.AddDbContext<OpenSimRegionContext>(options =>
		options.UseMySql(
				builder.Configuration.GetConnectionString("OpenSimRegionConnection"),
				ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("OpenSimRegionConnection"))));

builder.Services.AddDbContext<OpenSimEconomyContext>(options =>
		options.UseMySql(
				builder.Configuration.GetConnectionString("OpenSimEconomyConnection"),
				ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("OpenSimEconomyConnection"))));

builder.Services.AddDbContext<OpenSimSearchContext>(options =>
		options.UseMySql(
				builder.Configuration.GetConnectionString("OpenSimSearchConnection"),
				ServerVersion.AutoDetect(builder.Configuration.GetConnectionString("OpenSimSearchConnection"))));
```

Example `appsettings.json` section:

```json
{
	"ConnectionStrings": {
		"OpenSimCoreConnection": "Server=localhost;Database=opensim;User=...;Password=...;",
		"OpenSimRegionConnection": "Server=localhost;Database=opensim_region;User=...;Password=...;",
		"OpenSimEconomyConnection": "Server=localhost;Database=opensim_money;User=...;Password=...;",
		"OpenSimSearchConnection": "Server=localhost;Database=opensim_search;User=...;Password=...;"
	}
}
```

## EF Core Tooling

Design-time factories are included for:

- `OpenSimCoreContext`
- `OpenSimRegionContext`
- `OpenSimEconomyContext`
- `OpenSimSearchContext`

This allows standard EF Core commands from the project directory:

```bash
dotnet ef migrations add Core_Initial --context OpenSimCoreContext
dotnet ef database update --context OpenSimCoreContext
```

Repeat with the corresponding context type for other domains.

## Notes

- This package models existing OpenSim schemas and is primarily intended for reuse by OpenSim NGC data/infrastructure packages.
- MySQL collation/charset/table naming choices are part of the model mapping and should be preserved unless a coordinated schema migration is planned.

## License

This project is licensed under MPL 2.0.
