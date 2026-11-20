# Kestrel Outfitters stand-in. Build context is the repository root:
#   docker build -f src/StandIns/KestrelOutfitters.StandIn/Dockerfile -t kestrel-standin:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src

# Restore first, from the files that pin the SDK and package versions, so source edits reuse the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StandIns/KestrelOutfitters.StandIn/KestrelOutfitters.StandIn.csproj src/StandIns/KestrelOutfitters.StandIn/
RUN dotnet restore src/StandIns/KestrelOutfitters.StandIn/KestrelOutfitters.StandIn.csproj

COPY src/StandIns/KestrelOutfitters.StandIn/ src/StandIns/KestrelOutfitters.StandIn/
RUN dotnet publish src/StandIns/KestrelOutfitters.StandIn/KestrelOutfitters.StandIn.csproj \
    --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    KestrelOutfitters__DatabasePath=/app/data/kestrel.db

COPY --from=build /app/publish .

# The SQLite file lives in /app/data (mount a volume there to keep data across container rebuilds).
# The non-root app user must own it to create and seed the database on first run.
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "KestrelOutfitters.StandIn.dll"]
