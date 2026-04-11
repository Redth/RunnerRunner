FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RunnerRunner.Core/RunnerRunner.Core.csproj RunnerRunner.Core/
COPY src/RunnerRunner.Agent/RunnerRunner.Agent.csproj RunnerRunner.Agent/
RUN dotnet restore RunnerRunner.Agent/RunnerRunner.Agent.csproj
COPY src/RunnerRunner.Core/ RunnerRunner.Core/
COPY src/RunnerRunner.Agent/ RunnerRunner.Agent/
RUN dotnet publish RunnerRunner.Agent/RunnerRunner.Agent.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends docker.io && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RunnerRunner.Agent.dll"]
