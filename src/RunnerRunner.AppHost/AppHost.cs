var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.RunnerRunner_Server>("server")
    .WithHttpEndpoint(port: 5080, name: "http");

builder.AddProject<Projects.RunnerRunner_Agent>("agent-local")
    .WithReference(server)
    .WithEnvironment("RunnerRunner__ServerUrl", server.GetEndpoint("http"))
    .WithEnvironment("RunnerRunner__AgentName", "aspire-local-agent")
    .WithEnvironment("RunnerRunner__AgentId", "aspire-local");

builder.Build().Run();
