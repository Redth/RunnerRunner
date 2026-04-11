var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.RunnerRunner_Server>("server");

builder.AddProject<Projects.RunnerRunner_Agent>("agent-local")
    .WithReference(server)
    .WithEnvironment("RunnerRunner__ServerUrl", server.GetEndpoint("http"))
    .WithEnvironment("RunnerRunner__AgentName", "aspire-local-agent")
    .WithEnvironment("RunnerRunner__AgentId", "aspire-local");

// Docker Compose deployment with SSH to remote Linux host
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

builder.Build().Run();
