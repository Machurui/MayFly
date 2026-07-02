# Build context: repo root (docker-compose uses context: .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MayFly.Provisioner/MayFly.Provisioner.csproj MayFly.Provisioner/
RUN dotnet restore MayFly.Provisioner/MayFly.Provisioner.csproj
COPY MayFly.Provisioner/ MayFly.Provisioner/
RUN dotnet publish MayFly.Provisioner/MayFly.Provisioner.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# No USER directive: the provisioner must run as root to access the host Docker socket
# (docker.sock is root:root with srw-rw---- — non-root cannot connect).
# This service is internal-only and never exposed to the host network.
ENTRYPOINT ["dotnet", "MayFly.Provisioner.dll"]
