using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Data.Model.Economy;

public class OpenSimEconomyContextFactory : IDesignTimeDbContextFactory<OpenSimEconomyContext>
{
    public OpenSimEconomyContextFactory() { }

    OpenSimEconomyContext IDesignTimeDbContextFactory<OpenSimEconomyContext>.CreateDbContext(string[] args)
    {
        // Build configuration to load connection string from appsettings.json
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddUserSecrets<OpenSimEconomyContext>(optional: true)
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<OpenSimEconomyContext>();
        var connectionString = configuration.GetConnectionString("OpenSimEconomyConnection") ??
            throw new InvalidOperationException("Connection string 'OpenSimEconomyConnection' not found.");

        // Configure DbContext to use MySQL with Pomelo provider
        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
            mySqlOptions => mySqlOptions.MigrationsAssembly(typeof(OpenSimEconomyContext).Assembly.FullName));

        return new OpenSimEconomyContext(optionsBuilder.Options);
    }
}
