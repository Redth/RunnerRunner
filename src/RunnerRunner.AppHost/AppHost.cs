var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.RunnerRunner_Server>("server");

// Docker Compose deployment with SSH to remote Linux host
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

builder.Build().Run();
