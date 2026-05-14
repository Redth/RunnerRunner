ARG DOTNET_SDK_PLATFORM=linux/amd64
FROM --platform=$DOTNET_SDK_PLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG INFORMATIONAL_VERSION=0.0.0
ARG TARGETARCH
RUN arch="${TARGETARCH:-$(dpkg --print-architecture)}" \
    && case "${arch}" in \
        amd64|x86_64) rid="linux-x64" ;; \
        arm64|aarch64) rid="linux-arm64" ;; \
        *) echo "Unsupported target architecture: ${arch}" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/RunnerRunner.HostWorker/RunnerRunner.HostWorker.csproj \
        -c Release \
        -r "${rid}" \
        --self-contained \
        -o /app/publish \
        /p:InformationalVersion=${INFORMATIONAL_VERSION}

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
# Install Docker CLI for DockerBackend
RUN apt-get update && apt-get install -y --no-install-recommends docker.io && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["./RunnerRunner.HostWorker"]
