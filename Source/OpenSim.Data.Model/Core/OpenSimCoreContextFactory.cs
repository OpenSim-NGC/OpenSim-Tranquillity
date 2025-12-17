/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Data.Model.Core;

public class OpenSimCoreContextFactory : IDesignTimeDbContextFactory<OpenSimCoreContext>
{
    public OpenSimCoreContextFactory() { }

    OpenSimCoreContext IDesignTimeDbContextFactory<OpenSimCoreContext>.CreateDbContext(string[] args)
    {
        // Build configuration to load connection string from appsettings.json
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddUserSecrets<OpenSimCoreContext>(optional: true)
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<OpenSimCoreContext>();
        var connectionString = configuration.GetConnectionString("OpenSimCoreConnection") ??
            throw new InvalidOperationException("Connection string 'OpenSimCoreConnection' not found.");

        // Configure DbContext to use MySQL with Pomelo provider
        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
            mySqlOptions => mySqlOptions.MigrationsAssembly(typeof(OpenSimCoreContext).Assembly.FullName));

        return new OpenSimCoreContext(optionsBuilder.Options);
    }
}
