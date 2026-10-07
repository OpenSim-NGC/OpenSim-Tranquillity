using System.Reflection;
using Phlox.ScriptEngine;
using Phlox.ScriptEngine.AsyncCommand;
using Phlox.ScriptEngine.AsyncCommand.Plugins;
using Xunit;

namespace InWorldz.Phlox.Tests;

public class AsyncCommandIdentityTests
{
    [Fact]
    public void PhloxEngineUsesItsOwnAsyncManagerAndPlugins()
    {
        var assembly = typeof(PhloxEngine).Assembly;
        var manager = typeof(AsyncCommandManager);
        var property = typeof(PhloxEngine).GetProperty("AsyncCommands",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(manager, property.PropertyType);
        Assert.Equal(assembly, manager.Assembly);
        Assert.Equal(typeof(SensorRepeat), manager.GetProperty("SensorRepeatPlugin")!.PropertyType);
        Assert.Equal(typeof(HttpRequest), manager.GetProperty("HttpRequestPlugin")!.PropertyType);
        Assert.Equal(typeof(XmlRequest), manager.GetProperty("XmlRequestPlugin")!.PropertyType);
        Assert.Equal(assembly, typeof(SensorRepeat).Assembly);
        Assert.Equal(assembly, typeof(HttpRequest).Assembly);
        Assert.Equal(assembly, typeof(XmlRequest).Assembly);
    }

    [Fact]
    public void PhloxDoesNotExportSharedEngineTypeNames()
    {
        var sharedNames = typeof(OpenSim.Region.ScriptEngine.Shared.Api.AsyncCommandManager)
            .Assembly.GetExportedTypes().Select(type => type.FullName).ToHashSet();

        foreach (var type in typeof(PhloxEngine).Assembly.GetExportedTypes())
            Assert.DoesNotContain(type.FullName, sharedNames);
    }
}
