/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Data.Model.Region;

public class OpenSimRegionContextFactory : IDesignTimeDbContextFactory<OpenSimRegionContext>
{
    public OpenSimRegionContextFactory() { }

    OpenSimRegionContext IDesignTimeDbContextFactory<OpenSimRegionContext>.CreateDbContext(string[] args)
    {
        // Build configuration to load connection string from appsettings.json
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddUserSecrets<OpenSimRegionContext>(optional: true)
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<OpenSimRegionContext>();
        var regionConnectionString = configuration.GetConnectionString("OpenSimRegionConnection") ??
            throw new InvalidOperationException("Connection string 'OpenSimRegionConnection' not found.");

        // Configure DbContext to use MySQL with Pomelo provider
        optionsBuilder.UseMySql(regionConnectionString, ServerVersion.AutoDetect(regionConnectionString),
            mySqlOptions => mySqlOptions.MigrationsAssembly(typeof(OpenSimRegionContext).Assembly.FullName));                

        return new OpenSimRegionContext(optionsBuilder.Options);
    }
}
