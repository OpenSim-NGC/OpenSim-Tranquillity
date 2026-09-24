using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace OpenSim.Data.Model.Identity;

public class UserIdentityContextFactory : IDesignTimeDbContextFactory<IdentityContext>
{
    public UserIdentityContextFactory() { }

    IdentityContext IDesignTimeDbContextFactory<IdentityContext>.CreateDbContext(string[] args)
    {
        // Build configuration to load connection string from appsettings.json
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .AddUserSecrets<IdentityContext>(optional: true)
            .Build();
        var optionsBuilder = new DbContextOptionsBuilder<IdentityContext>();
        var connectionString = configuration.GetConnectionString("IdentityConnection") ??
            throw new InvalidOperationException("Connection string 'IdentityConnection' not found.");

        // Configure DbContext to use MySQL with Microting provider
        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
            mySqlOptions => mySqlOptions.MigrationsAssembly(typeof(IdentityContext).Assembly.FullName));

        return new IdentityContext(optionsBuilder.Options);
    }
}
