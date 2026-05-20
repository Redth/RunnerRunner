using Microsoft.EntityFrameworkCore;
using RunnerRunner.Server.Data.Auth;

namespace RunnerRunner.Server.Authentication;

public class FirstRunSetupMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RunnerRunnerAuthDbContext authDb)
    {
        if (IsSetupBypassPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var hasEnabledUsers = await authDb.Users.AnyAsync(x => x.Enabled);
        if (!hasEnabledUsers)
        {
            context.Response.Redirect("/setup");
            return;
        }

        await next(context);
    }

    private static bool IsSetupBypassPath(PathString path)
    {
        return path.StartsWithSegments("/setup")
            || path.StartsWithSegments("/auth")
            || path.StartsWithSegments("/api/webhooks")
            || path.StartsWithSegments("/api/hostworker-updates")
            || path.StartsWithSegments("/hubs/agent")
            || path.StartsWithSegments("/runnerrunner.hostworker.v1.HostWorkerControl")
            || path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/_content")
            || path.StartsWithSegments("/lib")
            || path.StartsWithSegments("/js")
            || path.StartsWithSegments("/favicon")
            || path.StartsWithSegments("/icon")
            || path.StartsWithSegments("/app.css")
            || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/alive");
    }
}
