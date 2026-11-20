# Build context: the repository root (docker-compose.yml sets it).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /repo

# Restore first, from project files only, so the restore layer is cached across source edits.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Lumen.Domain/Lumen.Domain.csproj src/Lumen.Domain/
COPY src/Lumen.Api/Lumen.Api.csproj src/Lumen.Api/
RUN dotnet restore src/Lumen.Api/Lumen.Api.csproj

COPY src/Lumen.Domain/ src/Lumen.Domain/
COPY src/Lumen.Api/ src/Lumen.Api/
RUN dotnet publish src/Lumen.Api/Lumen.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app

# Data Protection keys (DataProtection__KeysPath); compose mounts a volume here. A new volume takes this owner.
RUN mkdir -p /app/keys && chown "$APP_UID" /app/keys

COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lumen.Api.dll"]
