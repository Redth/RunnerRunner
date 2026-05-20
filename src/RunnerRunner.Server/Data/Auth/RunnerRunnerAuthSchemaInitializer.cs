using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RunnerRunner.Server.Authentication;

namespace RunnerRunner.Server.Data.Auth;

public static class RunnerRunnerAuthSchemaInitializer
{
    public static async Task EnsureCreatedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunnerRunnerAuthDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in RunnerRunnerRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                    throw new InvalidOperationException($"Unable to create role '{role}': {string.Join("; ", result.Errors.Select(x => x.Description))}");
            }
        }
    }
}
