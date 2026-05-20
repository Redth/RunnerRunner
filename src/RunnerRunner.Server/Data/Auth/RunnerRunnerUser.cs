using Microsoft.AspNetCore.Identity;
using RunnerRunner.Server.Authentication;

namespace RunnerRunner.Server.Data.Auth;

public class RunnerRunnerUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string Source { get; set; } = RunnerRunnerUserSources.Local;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public string? ExternalIssuer { get; set; }
    public string? ExternalSubject { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName)
        ? UserName ?? Email ?? Id
        : DisplayName;
}
