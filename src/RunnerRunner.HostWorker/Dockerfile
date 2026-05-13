FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG INFORMATIONAL_VERSION=0.0.0
RUN dotnet publish src/RunnerRunner.HostWorker/RunnerRunner.HostWorker.csproj -c Release -o /app/publish \
    /p:InformationalVersion=${INFORMATIONAL_VERSION}

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
# Install Docker CLI for DockerBackend
RUN apt-get update && apt-get install -y --no-install-recommends docker.io && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RunnerRunner.HostWorker.dll"]
