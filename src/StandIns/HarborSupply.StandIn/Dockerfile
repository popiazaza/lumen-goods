# Build context: the repository root.
#   docker build -f src/StandIns/HarborSupply.StandIn/Dockerfile -t harbor-standin:dev .
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /repo

# Restore first, from project files only, so the restore layer is cached across source edits.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StandIns/HarborSupply.StandIn/HarborSupply.StandIn.csproj src/StandIns/HarborSupply.StandIn/
RUN dotnet restore src/StandIns/HarborSupply.StandIn/HarborSupply.StandIn.csproj

COPY src/StandIns/HarborSupply.StandIn/ src/StandIns/HarborSupply.StandIn/
RUN dotnet publish src/StandIns/HarborSupply.StandIn/HarborSupply.StandIn.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app

# The SQLite file lives here; mount a volume on it to keep data across container re-creation.
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
ENV Harbor__DatabasePath=/app/data/harbor.db

COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HarborSupply.StandIn.dll"]
