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

Produkcja startuje z **pustej** bazy: migracje tworzą schemat, opcjonalny seed demo dodaje rozmowy startowe. Nie kopiuj bazy
deweloperskiej (konta QA, testowe posty, wyniki pojedynków, dane Playwright) i nie twórz kont QA na produkcji.

```bash
cp .env.example .env            # SANSPOST_SITE_ADDRESS=<domena>, HTTP_PORT=80, HTTPS_PORT=443
                                # publiczne demo: SANSPOST_SEED_CONTENT=true (rozmowy startowe, konta demo bez logowania)
# utwórz sekrety (pkt 4)
docker compose up -d
docker compose ps               # postgres: healthy, sanspost: healthy, proxy: up
curl -fsS https://<domena>/health/ready
```

DNS: rekord `A` (i `AAAA`, jeśli serwer ma IPv6) domeny na adres serwera **przed** startem Caddy — certyfikat Let's Encrypt
wymaga, by domena wskazywała serwer i porty 80/443 były osiągalne z internetu.

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

### Nagłówki bezpieczeństwa, cache i kompresja (v1.0)

Aplikacja ustawia na każdej odpowiedzi (także błędach): `Content-Security-Policy` (bez `unsafe-eval`; skrypty z własnego
origin + nonce dla dwóch skryptów inline `_Host` + hashe skryptów `error.html`; `frame-ancestors 'none'`; `media-src https:`
dla strumieni radia; `connect-src` z WebSocketem tego samego hosta), `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` (bez kamery/mikrofonu/
geolokacji). `style-src` zawiera `'unsafe-inline'` (atrybuty `style` komponentów) — świadomy wyjątek, nie dotyczy skryptów.
Proxy nie musi dodawać własnych nagłówków (duplikat CSP zaostrza politykę — nie wpisuj drugiej).

Pliki statyczne: z wersją w adresie (`?v=` z `asp-append-version`) — `Cache-Control: public, max-age=31536000, immutable`;
bez wersji (moduły scen 3D, Three.js, SignalR JS, HTML) — `no-cache` + ETag (304). Kompresja w Caddy: `encode zstd gzip`.

`AllowedHosts` zostaje `*`: Caddy obsługuje wyłącznie blok `SANSPOST_SITE_ADDRESS`, więc żądania z innym `Host` nie docierają
do aplikacji, a sonda HEALTHCHECK łączy się na `localhost`.

## 12. Limity

### Dzienne limity biznesowe (v1.0)

Na użytkownika na dobę UTC: **10 postów, 20 komentarzy, 15 rozpoczętych gier** (stałe w `Features/Usage/DailyQuota`).
Źródło prawdy: PostgreSQL (`dailyuserusages`, jeden wiersz na użytkownika i dzień) — liczniki przeżywają restart i działają
przy wielu instancjach; reset to nowa data UTC (bez zadań w tle). Zużycie jest atomowe i w transakcji operacji: post/komentarz,
który się nie zapisze, nie zużywa limitu; usunięcie nie zwraca limitu. Gra liczy się, gdy naprawdę się zaczyna (trening,
start pojedynku po gotowości obu, rewanż, dołączenie do wyzwania REST); pojedynek zużywa limit obu graczy albo żadnego.
Wysłane / odrzucone / wycofane / wygasłe wyzwanie nie jest grą. Odpowiedź: **429** + `Retry-After` do północy UTC, kody
`daily-post-limit-reached`, `daily-comment-limit-reached`, `daily-game-limit-reached`, `duel-player-daily-limit-reached`.
Plan Premium (panel admina) nie zmienia limitów.

### Limity HTTP (rate limiting)

W pamięci procesu. **1 instancja:** limity działają zgodnie z konfiguracją (`RateLimiting__Auth|Search|Writes__PermitLimit`, okno 1 min). **N instancji:** efektywny limit ≈ N × skonfigurowany. Rozproszony limiter (np. Redis) — przyszłość, nie ma go w tym wdrożeniu.

### Stół gry na żywo (SignalR)

Pojedynki 1v1 (`/hubs/duel`, uwierzytelnienie cookie sesji albo JWT) trzymają stan w pamięci procesu — tak jak limity:
**jedna instancja** (bez backplane Redis). Przy wielu instancjach potrzebny byłby sticky routing i backplane — poza tym wdrożeniem.
Restart aplikacji kończy trwające pojedynki; wyniki zakończonych pojedynków na żywo są w bazie (`duelresults` — gwiazdki,
ranking, Mistrz Stołu). Czasy (sekundy, zakres 1–300, walidowane przy starcie):
`Duels__ChallengeSeconds` (ważność wyzwania, domyślnie 30), `Duels__RoundSeconds` (czas na ruch, 15),
`Duels__GraceSeconds` (okno powrotu po zerwaniu połączenia, 20). Keep-alive huba 5 s / limit 12 s — proxy nie może zamykać
bezczynnych WebSocketów szybciej niż po ~15 s (Caddy domyślnie ich nie zamyka).

## 13. Pula połączeń PostgreSQL

`Database__MaxPoolSize=30` (walidacja 5–200). Domyślne `max_connections` PostgreSQL = 100 → zostaje ~70 połączeń na psql/admina, migracje, `pg_dump`, monitoring i ewentualną drugą instancję. Pomiar (100 wirtualnych użytkowników, 60 s): pula 30 → 801 req/s, p95 59 ms, 0 błędów; pula domyślna ~100 → 665 req/s, p95 131 ms (więcej rywalizacji w bazie). Przy wielu instancjach: `N × MaxPoolSize + zapas < max_connections`.

## 14. Backup

**Wolumen Docker to NIE jest backup** (ginie z `down -v`, awarią dysku, błędem operatora).

```bash
./deploy/backup.sh              # backups/sanspost-<czas UTC>.dump, retencja: 14 najnowszych (KEEP=14, BACKUP_DIR=./backups)
crontab -e                      # codziennie 03:15:
# 15 3 * * * cd <katalog-projektu> && ./deploy/backup.sh >> backups/backup.log 2>&1
```

`pg_dump -Fc` z kontenera bazy do katalogu na hoście (poza kontenerem i wolumenem), najpierw plik `.partial`, potem zmiana nazwy.
Kopiuj `backups/` poza serwer. Klucze `dpkeys` (sesje) można odtworzyć — utrata wymusza tylko ponowne logowanie.

## 15. Restore

```bash
docker compose stop sanspost
docker compose exec -T postgres psql -U sanspost -d postgres -c "DROP DATABASE sanspost;" -c "CREATE DATABASE sanspost;"
docker compose exec -T postgres pg_restore -U sanspost -d sanspost --no-owner < backups/<plik>.dump
docker compose start sanspost   # migracje: tylko brakujące
```

Test odtworzenia bez dotykania produkcji: `./deploy/restore-check.sh backups/<plik>.dump` — odtwarza do tymczasowej bazy
`restore_check`, porównuje liczności (użytkownicy / posty / komentarze / migracje) z bazą bieżącą i usuwa bazę tymczasową.

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

Rotacja logów Dockera w `docker-compose.yml` (`json-file`, 5 × 10 MB na kontener). Obrazy: po aktualizacji usuń stare tagi
ręcznie (`docker image ls sanspost`, `docker image rm sanspost:<stary>`) — bez automatycznego `docker system prune`.

JSON w Production (`Timestamp`, `LogLevel`, `Category`, `Message`, `State`, `Scopes` z `RequestId`/`TraceId`). Odpowiedzi mają nagłówek `X-Request-Id`, a błędy API — `requestId` w ProblemDetails: ten sam identyfikator jest w logu. Logi nie zawierają haseł, tokenów, cookie, connection stringu ani hasha hasła. Zdarzenia: start z wersją wydania i środowiskiem (`SansPost 1.0.0 starting in Production.`), migracje, nieudane logowanie (kanał + IP), odrzucenia limitów, działania moderacji, seed demo, błędy Radio Browser, stół gry (dołączenie/odejście gracza — tylko pierwsze połączenie i ostatnie rozłączenie), start i wynik pojedynku na żywo (bez kart), nieobsłużone wyjątki. 404 nie jest błędem.
Structured logging to wbudowany formatter JSON .NET — bez dodatkowej biblioteki (Serilog nie jest potrzebny w tym wdrożeniu).

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

## Serwer docelowy (v1.0): mały VPS

Konfiguracja nie jest związana z dostawcą — ten sam `docker compose` działa na większym serwerze.
Przed instalacją sprawdź serwer (nic nie usuwaj z istniejących usług):

```bash
cat /etc/os-release; uname -a; uname -m      # obraz jest linux/amd64 — na ARM zbuduj obraz na serwerze
free -h; df -h; ip addr; ss -tulpn          # zajęte porty 80/443? publiczny IPv4/IPv6?
docker --version; docker compose version    # Docker 24+, Compose v2
```

Jeśli serwer nie ma publicznego IPv4 z portami 80/443 (np. tylko IPv6 albo przekierowane porty), Let's Encrypt i ruch
z internetu wymagają rozwiązania dostawcy (proxy / przekierowanie domeny) — sprawdź jego dokumentację przed konfiguracją DNS.
Zapora: otwarte tylko 22 (SSH), 80, 443. PostgreSQL i aplikacja nie publikują portów.

Pomiar lokalnego stacku produkcyjnego v1.0 w spoczynku: SansPost ~150 MB, PostgreSQL ~50 MB, Caddy ~50 MB (razem ~250 MB),
CPU < 1%, obraz aplikacji 339 MB, baza demo ~9 MB. Na 4 GB RAM / 40 GB dysku zostaje duży zapas (logi i backupy z retencją).
