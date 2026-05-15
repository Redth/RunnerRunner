ARG DOTNET_BUILD_PLATFORM=linux/amd64
ARG INFORMATIONAL_VERSION=0.0.0
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TAG=unknown
FROM --platform=$DOTNET_BUILD_PLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH
ARG INFORMATIONAL_VERSION
ARG SOURCE_REVISION_ID
ARG BUILD_TAG
COPY Directory.Build.props ./
COPY src/RunnerRunner.Core/RunnerRunner.Core.csproj RunnerRunner.Core/
COPY src/RunnerRunner.ServiceDefaults/RunnerRunner.ServiceDefaults.csproj RunnerRunner.ServiceDefaults/
COPY src/RunnerRunner.Server/RunnerRunner.Server.csproj RunnerRunner.Server/
COPY deploy/postgres-init/ /deploy/postgres-init/
RUN arch_args="" \
    && if [ -n "${TARGETARCH}" ]; then arch_args="-a ${TARGETARCH}"; fi \
    && PROTOBUF_TOOLS_OS=linux PROTOBUF_TOOLS_CPU=x64 \
        dotnet restore RunnerRunner.Server/RunnerRunner.Server.csproj ${arch_args}
COPY src/RunnerRunner.Core/ RunnerRunner.Core/
COPY src/RunnerRunner.ServiceDefaults/ RunnerRunner.ServiceDefaults/
COPY src/RunnerRunner.Server/ RunnerRunner.Server/
RUN arch_args="" \
    && if [ -n "${TARGETARCH}" ]; then arch_args="-a ${TARGETARCH}"; fi \
    && PROTOBUF_TOOLS_OS=linux PROTOBUF_TOOLS_CPU=x64 \
        dotnet publish RunnerRunner.Server/RunnerRunner.Server.csproj -c Release -o /app/publish \
        ${arch_args} \
        /p:InformationalVersion=${INFORMATIONAL_VERSION} \
        /p:SourceRevisionId=${SOURCE_REVISION_ID} \
        /p:RunnerRunnerCommitSha=${SOURCE_REVISION_ID} \
        /p:RunnerRunnerBuildTag=${BUILD_TAG}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG INFORMATIONAL_VERSION=0.0.0
ARG SOURCE_REVISION_ID=unknown
ARG BUILD_TAG=unknown
WORKDIR /app
LABEL org.opencontainers.image.title="RunnerRunner Server" \
      org.opencontainers.image.version="${INFORMATIONAL_VERSION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION_ID}" \
      org.opencontainers.image.ref.name="${BUILD_TAG}"
COPY --from=build /app/publish .
EXPOSE 4779 4780 11111 30000
ENTRYPOINT ["dotnet", "RunnerRunner.Server.dll"]
