# Build context: repo root (docker-compose uses context: .)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MayFly.Api/MayFly.Api.csproj MayFly.Api/
RUN dotnet restore MayFly.Api/MayFly.Api.csproj
COPY MayFly.Api/ MayFly.Api/
RUN dotnet publish MayFly.Api/MayFly.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MayFly.Api.dll"]
