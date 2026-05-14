var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.RunnerRunner_Server>("server");

builder.Build().Run();
