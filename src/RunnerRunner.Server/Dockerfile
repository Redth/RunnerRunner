ARG DOTNET_BUILD_PLATFORM=linux/amd64
FROM --platform=$DOTNET_BUILD_PLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH
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
ARG INFORMATIONAL_VERSION=0.0.0
RUN arch_args="" \
    && if [ -n "${TARGETARCH}" ]; then arch_args="-a ${TARGETARCH}"; fi \
    && PROTOBUF_TOOLS_OS=linux PROTOBUF_TOOLS_CPU=x64 \
        dotnet publish RunnerRunner.Server/RunnerRunner.Server.csproj -c Release -o /app/publish \
        ${arch_args} \
        /p:InformationalVersion=${INFORMATIONAL_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 4779 4780 11111 30000
ENTRYPOINT ["dotnet", "RunnerRunner.Server.dll"]
