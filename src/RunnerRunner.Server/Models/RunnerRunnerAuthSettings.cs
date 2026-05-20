namespace RunnerRunner.Server.Models;

public class RunnerRunnerAuthSettings
{
    public const string SingletonId = "auth-settings";

    public string Id { get; set; } = SingletonId;
    public RunnerRunnerOidcSettings Oidc { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RunnerRunnerOidcSettings
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ProtectedClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public string NameClaimType { get; set; } = "name";
    public string EmailClaimType { get; set; } = "email";
    public string RoleClaimType { get; set; } = "role";
    public string DefaultRole { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId);
}
