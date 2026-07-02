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
USER app
ENTRYPOINT ["dotnet", "MayFly.Provisioner.dll"]
