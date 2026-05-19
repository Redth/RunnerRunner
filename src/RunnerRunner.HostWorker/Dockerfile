ARG DOTNET_BUILD_PLATFORM=linux/amd64
ARG INFORMATIONAL_VERSION=0.0.0
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TAG=unknown
FROM --platform=$DOTNET_BUILD_PLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG INFORMATIONAL_VERSION
ARG SOURCE_REVISION_ID
ARG BUILD_TAG
ARG TARGETARCH
RUN arch="${TARGETARCH:-$(dpkg --print-architecture)}" \
    && case "${arch}" in \
        amd64|x86_64) rid="linux-x64" ;; \
        arm64|aarch64) rid="linux-arm64" ;; \
        *) echo "Unsupported target architecture: ${arch}" >&2; exit 1 ;; \
    esac \
    && PROTOBUF_TOOLS_OS=linux PROTOBUF_TOOLS_CPU=x64 \
        dotnet publish src/RunnerRunner.HostWorker/RunnerRunner.HostWorker.csproj \
        -c Release \
        -r "${rid}" \
        --self-contained \
        -o /app/publish \
        /p:InformationalVersion=${INFORMATIONAL_VERSION} \
        /p:SourceRevisionId=${SOURCE_REVISION_ID} \
        /p:RunnerRunnerCommitSha=${SOURCE_REVISION_ID} \
        /p:RunnerRunnerBuildTag=${BUILD_TAG}

FROM docker:29-cli AS docker-cli

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG INFORMATIONAL_VERSION=0.0.0
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TAG=unknown
WORKDIR /app
LABEL org.opencontainers.image.title="RunnerRunner HostWorker" \
      org.opencontainers.image.version="${INFORMATIONAL_VERSION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION_ID}" \
      org.opencontainers.image.ref.name="${BUILD_TAG}"
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=build /app/publish .
ENTRYPOINT ["./RunnerRunner.HostWorker"]
