# SansPost — wdrożenie

Pojedyncza instancja: **reverse proxy (TLS) → SansPost → PostgreSQL**, uruchamiana `docker compose`.

## 1. Architektura

```
Klient ──HTTPS──▶ proxy (Caddy, :80/:443, TLS)
                    │  X-Forwarded-For / X-Forwarded-Proto
                    ▼
                 sanspost (ASP.NET Core 8, HTTP :8080, użytkownik bez root)
                    │  sieć Docker "backend" (172.30.57.0/24), bez portów na hoście
                    ▼
                 postgres (17, wolumen pgdata)
```

Wolumeny: `pgdata` (baza), `dpkeys` (klucze Data Protection — sesje przeżywają restart), `caddy_data`/`caddy_config` (certyfikaty).

## 2. Wymagania

- Docker Engine 24+ z Compose v2.
- Porty 80 i 443 (lub `HTTP_PORT` / `HTTPS_PORT`).
- Domena wskazująca na serwer (dla Let's Encrypt); do testu lokalnego wystarczy `localhost`.
- Zasoby jednej instancji — patrz „Zasoby” niżej.
- Wychodzący HTTPS do `*.api.radio-browser.info` (Kącik muzyczny: tylko lista stacji, najwyżej raz na 20 min dzięki pamięci
  podręcznej). Audio pobiera przeglądarka prosto ze stacji — serwer nie przesyła strumieni. Bez dostępu Saloon działa,
  a Kącik pokazuje „Radio jest chwilowo niedostępne”. Opcjonalnie `Music__RadioBrowserServers` (lista adresów po przecinku).

## 3. Zmienne środowiskowe (`.env`, wzór: `.env.example`)

| Zmienna | Domyślnie | Znaczenie |
|---|---|---|
| `POSTGRES_DB` / `POSTGRES_USER` | `sanspost` | baza i użytkownik |
| `SECRETS_DIR` | `./secrets` | katalog plików sekretów |
| `SANSPOST_SITE_ADDRESS` | `localhost` | domena dla Caddy |
| `HTTP_PORT` / `HTTPS_PORT` | `80` / `443` | porty na hoście (HTTPS_PORT = też port przekierowania HTTPS aplikacji) |
| `SANSPOST_DB_MAX_POOL_SIZE` | `30` | `Database:MaxPoolSize` (5–200) |
| `SANSPOST_REGISTRATION_ENABLED` | `true` | rejestracja publiczna |
| `SANSPOST_MAX_PUBLIC_ACCOUNTS` | `100` | limit kont publicznych |
| `SANSPOST_SEED_CONTENT` | `false` | treści demo (tylko demo/dev) |
| `SANSPOST_MEM_LIMIT` | `1g` | limit pamięci kontenera aplikacji |

Konfiguracja aplikacji to zwykłe klucze ASP.NET Core (`Sekcja__Klucz`). Najważniejsze: `ConnectionStrings__DefaultConnection` (bez hasła), `ForwardedHeaders__KnownProxies__N` / `ForwardedHeaders__KnownNetworks__N`, `HttpsRedirection__HttpsPort`, `Database__MigrateOnStartup`, `Hosting__ShutdownTimeout` (15 s).

## 4. Sekrety

Nigdy w repozytorium ani w obrazie. Pliki w `SECRETS_DIR` (katalog jest w `.gitignore`), montowane jako Docker secrets i czytane przez aplikację jako konfiguracja (`/run/secrets/Jwt__Key` → `Jwt:Key`).

| Plik | Użycie |
|---|---|
| `postgres_password` | hasło PostgreSQL (ten sam plik trafia do aplikacji jako `Database__Password`) |
| `jwt_key` | klucz podpisu JWT, min. 32 bajty |
| `bootstrap_admin_password` | tylko na czas bootstrapu admina — potem usunąć |

```bash
mkdir -p secrets
openssl rand -hex 24    | tr -d '\r\n' > secrets/postgres_password
openssl rand -base64 48 | tr -d '\r\n' > secrets/jwt_key
chmod 0444 secrets/*   # Linux: kontener aplikacji działa jako UID 1654
```

Pliki bez końcowego znaku nowej linii. Rotacja klucza JWT unieważnia wszystkie tokeny API (użytkownicy logują się ponownie).

## 5. Budowa obrazu

```bash
docker compose build            # obraz sanspost:${SANSPOST_TAG:-local}
```

Multi-stage: SDK tylko w etapie build; obraz końcowy `mcr.microsoft.com/dotnet/aspnet:8.0` + opublikowana aplikacja (~330 MB), bez testów, źródeł i sekretów.

## 6. Pierwsze uruchomienie

```bash
cp .env.example .env            # uzupełnij domenę, porty
# utwórz sekrety (pkt 4)
docker compose up -d
docker compose ps               # postgres: healthy, sanspost: healthy
curl -fsS https://<domena>/health/ready
```

## 7. Migracje bazy

Aplikacja przy starcie: czeka na PostgreSQL (ograniczone ponawianie: `Database__StartupRetries`=10, opóźnienie rosnące od 2 s) → stosuje brakujące migracje (każda logowana: `Migration {MigrationName} applied.`) → dopiero wtedy przyjmuje ruch.
Błąd połączenia (po limicie prób), złe hasło, błąd migracji lub niepoprawna konfiguracja = **proces kończy się kodem 1** i nie obsługuje ruchu na częściowym schemacie.

**Wiele instancji:** ustaw `Database__MigrateOnStartup=false` i uruchamiaj migracje jednym jobem wdrożeniowym przed startem instancji (np. ten sam obraz z `Database__MigrateOnStartup=true` jako jednorazowy kontener). Brak rozproszonej blokady migracji.

## 8. Pierwszy administrator (bootstrap)

Domyślnie `BootstrapAdmin:Enabled=false`. Jednorazowo:

```bash
openssl rand -base64 18 | tr -d '\r\n' > secrets/bootstrap_admin_password
BOOTSTRAP_ADMIN_USERNAME=admin BOOTSTRAP_ADMIN_EMAIL=admin@example.com \
  docker compose -f docker-compose.yml -f docker-compose.bootstrap.yml up -d
# sprawdź log: "BootstrapAdmin: utworzono administratora ..." (hasło nigdy nie jest logowane)
docker compose up -d            # ponownie BEZ pliku bootstrap → Enabled=false
rm secrets/bootstrap_admin_password
```

Idempotentne: gdy administrator istnieje, bootstrap nic nie robi. Nazwa admina nie pochodzi ze słownika przydomków publicznych.

## 9. Health checks

| Endpoint | Znaczenie | Użycie |
|---|---|---|
| `GET /health/live` | proces działa (bez zależności) | liveness |
| `GET /health/ready` | PostgreSQL osiągalny (timeout 3 s) | readiness, HEALTHCHECK obrazu, `depends_on` proxy |

Odpowiedź: `Healthy` / `Unhealthy` (503) — bez szczegółów. HEALTHCHECK obrazu: `dotnet SansPost.dll --healthcheck` (obraz nie ma curl).

## 10. Reverse proxy

`deploy/Caddyfile`: TLS, HTTP→HTTPS, `reverse_proxy sanspost:8080` (WebSocket Blazora i stołu gry `/hubs/duel` działają bez dodatkowej konfiguracji). Caddy nadpisuje `X-Forwarded-*` od klientów.
Aplikacja ufa nagłówkom **tylko** od `ForwardedHeaders__KnownProxies__0=172.30.57.10` (stały adres proxy w sieci compose). Nagłówki od innych nadawców są ignorowane — klient nie podmieni adresu IP (limity, logi) ani schematu.
Inne proxy (nginx, Traefik, load balancer): ustaw jego adres w `KnownProxies` lub sieć w `KnownNetworks` (CIDR); `ForwardLimit` = liczba zaufanych przeskoków (domyślnie 1). Nie używaj „trust all”.

## 11. HTTPS

TLS kończy się na proxy; w obrazie nie ma certyfikatów, kontener słucha HTTP w prywatnej sieci. Przy `X-Forwarded-Proto: https` od zaufanego proxy `Request.Scheme = https`: cookie `Secure`, brak przekierowania, brak pętli. Przekierowanie HTTP→HTTPS w aplikacji tylko dla bezpośredniego HTTP z jawnym `HttpsRedirection__HttpsPort` (sondy `/health/*` wyłączone). HSTS w środowiskach innych niż Development (poza `localhost`).

## 12. Limity (rate limiting)

W pamięci procesu. **1 instancja:** limity działają zgodnie z konfiguracją (`RateLimiting__Auth|Search|Writes__PermitLimit`, okno 1 min). **N instancji:** efektywny limit ≈ N × skonfigurowany. Rozproszony limiter (np. Redis) — przyszłość, nie ma go w tym wdrożeniu.

### Stół gry na żywo (SignalR)

Pojedynki 1v1 (`/hubs/duel`, uwierzytelnienie cookie sesji albo JWT) trzymają stan w pamięci procesu — tak jak limity:
**jedna instancja** (bez backplane Redis). Przy wielu instancjach potrzebny byłby sticky routing i backplane — poza tym wdrożeniem.
Restart aplikacji kończy trwające pojedynki (nie ma trwałej historii gier). Czasy (sekundy, zakres 1–300, walidowane przy starcie):
`Duels__ChallengeSeconds` (ważność wyzwania, domyślnie 30), `Duels__RoundSeconds` (czas na ruch, 15),
`Duels__GraceSeconds` (okno powrotu po zerwaniu połączenia, 20). Keep-alive huba 5 s / limit 12 s — proxy nie może zamykać
bezczynnych WebSocketów szybciej niż po ~15 s (Caddy domyślnie ich nie zamyka).

## 13. Pula połączeń PostgreSQL

`Database__MaxPoolSize=30` (walidacja 5–200). Domyślne `max_connections` PostgreSQL = 100 → zostaje ~70 połączeń na psql/admina, migracje, `pg_dump`, monitoring i ewentualną drugą instancję. Pomiar (100 wirtualnych użytkowników, 60 s): pula 30 → 801 req/s, p95 59 ms, 0 błędów; pula domyślna ~100 → 665 req/s, p95 131 ms (więcej rywalizacji w bazie). Przy wielu instancjach: `N × MaxPoolSize + zapas < max_connections`.

## 14. Backup

**Wolumen Docker to NIE jest backup** (ginie z `down -v`, awarią dysku, błędem operatora).

```bash
mkdir -p backups
docker compose exec -T postgres pg_dump -U sanspost -d sanspost -Fc > backups/sanspost-$(date +%F-%H%M).dump
```

Format custom (`-Fc`): skompresowany, odtwarzany `pg_restore`. Kopiuj poza serwer; regularnie testuj odtworzenie. Klucze `dpkeys` (sesje) można odtworzyć — utrata wymusza tylko ponowne logowanie.

## 15. Restore

```bash
docker compose stop sanspost
docker compose exec -T postgres psql -U sanspost -d postgres -c "DROP DATABASE sanspost;" -c "CREATE DATABASE sanspost;"
docker compose exec -T postgres pg_restore -U sanspost -d sanspost --no-owner < backups/<plik>.dump
docker compose start sanspost   # migracje: tylko brakujące
```

Test odtworzenia na osobnej bazie: `CREATE DATABASE restore_check;` + `pg_restore -d restore_check` i porównanie liczności tabel.

## 16. Aktualizacja

1. Backup (zawsze, gdy wersja zawiera migracje).
2. `git pull` (lub nowy tag obrazu).
3. `docker compose build` (lub `pull`).
4. `docker compose up -d sanspost` — migracje przy starcie; błąd = kontener nie wstaje, stara baza nietknięta poza nieudaną migracją (każda migracja we własnej transakcji).
5. `docker compose ps` + `curl https://<domena>/health/ready`.
6. Smoke: strona główna, logowanie, post, komentarz.

Pojedyncza instancja = kilka sekund przerwy (proxy zwraca 502 w trakcie restartu).

## 17. Rollback

- **Obraz aplikacji:** prosty — poprzedni tag (`SANSPOST_TAG=<poprzedni> docker compose up -d sanspost`).
- **Baza:** nie zawsze. Migracje są stosowane tylko do przodu; poprzednia wersja aplikacji może nie działać na nowszym schemacie. Nie ma automatycznego „Down migration” w produkcji. Przy migracji potencjalnie niszczącej: backup przed wdrożeniem i rollback = restore (pkt 15) + poprzedni obraz.

## 18. Logi

```bash
docker compose logs -f sanspost
```

JSON w Production (`Timestamp`, `LogLevel`, `Category`, `Message`, `State`, `Scopes` z `RequestId`/`TraceId`). Odpowiedzi mają nagłówek `X-Request-Id`, a błędy API — `requestId` w ProblemDetails: ten sam identyfikator jest w logu. Logi nie zawierają haseł, tokenów, cookie, connection stringu ani hasha hasła. Zdarzenia: start/migracje, nieudane logowanie (kanał + IP), odrzucenia limitów, działania moderacji, seed demo, nieobsłużone wyjątki. 404 nie jest błędem.

Metryki (`System.Diagnostics.Metrics`): meter `SansPost` (`sanspost.rate_limit.rejections`, `sanspost.auth.failed_logins`, `sanspost.notifications.created`, `sanspost.moderation.actions`, `sanspost.db.failures`) + wbudowany `Microsoft.AspNetCore.Hosting` (`http.server.request.duration` — liczba, czas, kody 5xx). Odczyt: `dotnet-counters` lub eksporter OpenTelemetry (nie wbudowany w obraz).

## 19. Rozwiązywanie problemów

| Objaw | Przyczyna / działanie |
|---|---|
| `sanspost` kończy się kodem 1, log `Startup aborted: invalid configuration` | brak/zły `Jwt:Key`, connection string, pula lub proxy — komunikat wskazuje klucz |
| `Database ... not reachable (attempt n/11)` i koniec | PostgreSQL nie wstał / zła nazwa hosta; `docker compose ps postgres` |
| `Startup aborted: database is unavailable or migrations failed` | złe hasło (`postgres_password` różny od tego, z którym zainicjowano wolumen) lub błąd migracji — zobacz wyjątek w logu |
| `/health/ready` = 503 | baza niedostępna; aplikacja wraca sama po powrocie bazy |
| Pierwsze żądanie po restarcie PostgreSQL = 500 | nieaktualne połączenie w puli; kolejne żądania działają (znane, P3) |
| Wszyscy wylogowani po odtworzeniu kontenera | brak wolumenu `dpkeys` |
| Limity działają „na wszystkich naraz” | proxy nie jest w `KnownProxies` — aplikacja widzi tylko adres proxy |
| Pętla przekierowań | proxy nie wysyła `X-Forwarded-Proto` lub nie jest zaufane |
| `No XML encryptor configured` (ostrzeżenie) | klucze Data Protection leżą niezaszyfrowane na wolumenie `dpkeys` — chroń wolumen/dysk |

## Zasoby (jedna instancja)

Pomiar lokalny (nie SLA): spoczynek ~110 MB, stały ruch ~240 MB, szczyt w teście 100 użytkowników: 433 MB (pula 100) / 322 MB (pula 30).
- **Minimum:** 512 MB RAM, 1 vCPU dla aplikacji; PostgreSQL 512 MB.
- **Zalecane:** limit 1 GB (domyślny `mem_limit`), 2 vCPU; PostgreSQL 1 GB.
