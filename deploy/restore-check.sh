#!/usr/bin/env sh
# SansPost — test odtworzenia backupu BEZ dotykania bazy produkcyjnej: pg_restore do tymczasowej bazy restore_check,
# porównanie liczności kluczowych tabel z bazą bieżącą, usunięcie bazy tymczasowej.
#   ./deploy/restore-check.sh backups/sanspost-<czas>.dump
set -eu
[ -f .env ] && set -a && . ./.env && set +a
DUMP="${1:?podaj plik backupu}"
DB="${POSTGRES_DB:-sanspost}"
DB_USER="${POSTGRES_USER:-sanspost}"
psql() { docker compose exec -T postgres psql -U "$DB_USER" -v ON_ERROR_STOP=1 "$@"; }
COUNTS="select (select count(*) from users)||'/'||(select count(*) from posts)||'/'||(select count(*) from comments)||'/'||(select count(*) from \"__EFMigrationsHistory\")"

psql -d postgres -qc "DROP DATABASE IF EXISTS restore_check;" -c "CREATE DATABASE restore_check;"
docker compose exec -T postgres pg_restore -U "$DB_USER" -d restore_check --no-owner < "$DUMP"
restored=$(psql -d restore_check -tAc "$COUNTS")
current=$(psql -d "$DB" -tAc "$COUNTS")
psql -d postgres -qc "DROP DATABASE restore_check;"
echo "users/posts/comments/migrations — backup: $restored, bieżąca baza: $current (baza tymczasowa usunięta)"
