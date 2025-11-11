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
