using RunnerRunner.Server.Components;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Document store (Shiny DocumentDB with SQLite)
var dbPath = builder.Configuration.GetValue<string>("Database:Path") ?? "runnerrunner.db";
builder.Services.AddRunnerRunnerDocumentStore($"Data Source={dbPath}");

// SignalR for agent communication
builder.Services.AddSignalR();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// SignalR hub for agent connections
app.MapHub<AgentHub>("/hubs/agent");

app.Run();
