#!/usr/bin/env sh
# SansPost — backup PostgreSQL: pg_dump (format custom, skompresowany) z kontenera do katalogu na hoście,
# nazwa z czasem UTC, retencja KEEP najnowszych kopii. Uruchamiać z katalogu projektu (tam, gdzie docker-compose.yml).
#   ./deploy/backup.sh                     BACKUP_DIR=./backups, KEEP=14
# cron (codziennie 03:15 UTC):
#   15 3 * * * cd /opt/sanspost && ./deploy/backup.sh >> backups/backup.log 2>&1
# Kopiuj katalog backupów poza serwer — wolumen ani dysk serwera nie są backupem.
set -eu
[ -f .env ] && set -a && . ./.env && set +a
BACKUP_DIR="${BACKUP_DIR:-./backups}"
KEEP="${KEEP:-14}"
DB="${POSTGRES_DB:-sanspost}"
DB_USER="${POSTGRES_USER:-sanspost}"

mkdir -p "$BACKUP_DIR"
file="$BACKUP_DIR/sanspost-$(date -u +%Y%m%dT%H%M%SZ).dump"
# Najpierw plik tymczasowy — przerwany zrzut nie wygląda jak poprawny backup.
docker compose exec -T postgres pg_dump -U "$DB_USER" -d "$DB" -Fc > "$file.partial"
mv "$file.partial" "$file"

# Retencja: zostaje KEEP najnowszych kopii (starsze usuwane — tylko pliki sanspost-*.dump w BACKUP_DIR).
ls -1t "$BACKUP_DIR"/sanspost-*.dump | tail -n +$((KEEP + 1)) | while read -r old; do rm -f -- "$old"; done
echo "$(date -u +%FT%TZ) backup ok: $file ($(du -h "$file" | cut -f1)), kept $(ls -1 "$BACKUP_DIR"/sanspost-*.dump | wc -l)"
