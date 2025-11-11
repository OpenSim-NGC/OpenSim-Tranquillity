using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Data.Model.Search;

public class OpenSimSearchContextFactory : IDesignTimeDbContextFactory<OpenSimSearchContext>
{
    public OpenSimSearchContextFactory() { }

    OpenSimSearchContext IDesignTimeDbContextFactory<OpenSimSearchContext>.CreateDbContext(string[] args)
    {
        // Build configuration to load connection string from appsettings.json
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddUserSecrets<OpenSimSearchContext>(optional: true)
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<OpenSimSearchContext>();
        var connectionString = configuration.GetConnectionString("OpenSimSearchConnection") ??
            throw new InvalidOperationException("Connection string 'OpenSimSearchConnection' not found.");
            
        // Configure DbContext to use MySQL with Pomelo provider
        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
            mySqlOptions => mySqlOptions.MigrationsAssembly(typeof(OpenSimSearchContext).Assembly.FullName));

        return new OpenSimSearchContext(optionsBuilder.Options);
    }
}
