# syntax=docker/dockerfile:1
# SansPost — produkcyjny obraz (multi-stage). Końcowy obraz: tylko ASP.NET Core runtime + opublikowana aplikacja.
# Bez SDK, testów, kodu źródłowego, historii repozytorium i sekretów (konfiguracja wyłącznie w czasie uruchomienia).

# ---- build: restore (osobna warstwa cache) + publish Release ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SansPost/SansPost.csproj SansPost/
RUN dotnet restore SansPost/SansPost.csproj
COPY SansPost/ SansPost/
RUN dotnet publish SansPost/SansPost.csproj -c Release -o /app/publish --no-restore -p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# HTTP wewnątrz prywatnej sieci Docker; TLS kończy się na reverse proxy.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/app/keys \
    DOTNET_NOLOGO=1
EXPOSE 8080

# Katalog kluczy Data Protection (wolumen) należy do użytkownika bez uprawnień root.
RUN mkdir -p /app/keys && chown "$APP_UID" /app/keys
COPY --from=build --chown=root:root /app/publish .

# Oficjalny użytkownik "app" z obrazu .NET 8 (UID 1654) — proces bez root; pliki aplikacji tylko do odczytu dla niego.
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD ["dotnet", "SansPost.dll", "--healthcheck"]

# Forma exec: dotnet jest PID 1 i dostaje SIGTERM bezpośrednio (graceful shutdown przy docker stop).
ENTRYPOINT ["dotnet", "SansPost.dll"]
