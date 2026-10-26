namespace RunnerRunner.Server.Services.HostWorkers;

// A dedicated worker listener must not also expose the first-run administrator form.
public sealed class HostWorkerEndpointIsolationMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly int _workerPort = configuration.GetValue<int>("HostWorker:GrpcOnlyPort");

    public async Task InvokeAsync(HttpContext context)
    {
        if (_workerPort != 0 && context.Connection.LocalPort == _workerPort &&
            (!HttpMethods.IsPost(context.Request.Method) ||
             context.Request.Path != "/runnerrunner.hostworker.v1.HostWorkerControl/Connect"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next(context);
    }
}
