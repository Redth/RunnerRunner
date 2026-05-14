FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/RunnerRunner.Core/RunnerRunner.Core.csproj RunnerRunner.Core/
COPY src/RunnerRunner.ServiceDefaults/RunnerRunner.ServiceDefaults.csproj RunnerRunner.ServiceDefaults/
COPY src/RunnerRunner.Server/RunnerRunner.Server.csproj RunnerRunner.Server/
RUN dotnet restore RunnerRunner.Server/RunnerRunner.Server.csproj
COPY src/RunnerRunner.Core/ RunnerRunner.Core/
COPY src/RunnerRunner.ServiceDefaults/ RunnerRunner.ServiceDefaults/
COPY src/RunnerRunner.Server/ RunnerRunner.Server/
ARG INFORMATIONAL_VERSION=0.0.0
RUN dotnet publish RunnerRunner.Server/RunnerRunner.Server.csproj -c Release -o /app/publish \
    /p:InformationalVersion=${INFORMATIONAL_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 4779 11111 30000
ENTRYPOINT ["dotnet", "RunnerRunner.Server.dll"]
