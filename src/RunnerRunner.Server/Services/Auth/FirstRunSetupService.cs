using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RunnerRunner.Server.Authentication;
using RunnerRunner.Server.Data.Auth;

namespace RunnerRunner.Server.Services.Auth;

public class FirstRunSetupService(
    UserManager<RunnerRunnerUser> userManager,
    RoleManager<IdentityRole> roleManager,
    ILogger<FirstRunSetupService> logger)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public async Task<bool> HasEnabledUsersAsync()
        => await userManager.Users.AnyAsync(x => x.Enabled);

    public async Task<IdentityResult> CreateInitialAdministratorAsync(
        string userName,
        string email,
        string displayName,
        string password)
    {
        await SetupLock.WaitAsync();
        try
        {
            if (await HasEnabledUsersAsync())
                return IdentityResult.Failed(new IdentityError { Description = "Initial setup has already been completed." });

            if (!await roleManager.RoleExistsAsync(RunnerRunnerRoles.Administrator))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(RunnerRunnerRoles.Administrator));
                if (!roleResult.Succeeded)
                    return roleResult;
            }

            var user = new RunnerRunnerUser
            {
                UserName = userName.Trim(),
                Email = email.Trim(),
                EmailConfirmed = true,
                DisplayName = displayName.Trim(),
                Source = RunnerRunnerUserSources.Local,
                Enabled = true
            };

            var createResult = await userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
                return createResult;

            var roleAddResult = await userManager.AddToRoleAsync(user, RunnerRunnerRoles.Administrator);
            if (!roleAddResult.Succeeded)
                return roleAddResult;

            logger.LogInformation("Initial RunnerRunner administrator '{UserName}' was created.", user.UserName);
            return IdentityResult.Success;
        }
        finally
        {
            SetupLock.Release();
        }
    }
}
