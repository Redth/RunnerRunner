using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerEndpointIsolationTests
{
    [Theory]
    [InlineData(4780, "GET", "/setup", false)]
    [InlineData(4780, "POST", "/auth/setup", false)]
    [InlineData(4780, "GET", "/orleans", false)]
    [InlineData(4780, "POST", "/runnerrunner.hostworker.v1.HostWorkerControl/Connect", true)]
    [InlineData(4780, "GET", "/runnerrunner.hostworker.v1.HostWorkerControl/Connect", false)]
    [InlineData(4779, "GET", "/setup", true)]
    public async Task DedicatedWorkerPort_OnlyAllowsGrpcConnect(int port, string method, string path, bool allowed)
    {
        var called = false;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostWorker:GrpcOnlyPort"] = "4780"
        }).Build();
        var middleware = new HostWorkerEndpointIsolationMiddleware(_ => { called = true; return Task.CompletedTask; }, configuration);
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = port;
        context.Request.Method = method;
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal(404, context.Response.StatusCode);
    }
}
