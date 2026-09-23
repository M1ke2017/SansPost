namespace SansPost.Infrastructure.Persistence
{
    // Konfiguracja PostgreSQL full-text search dla postów.
    public static class TextSearch
    {
        // 'simple' = tokenizacja + lowercase, bez stemmingu i stop-words. Treści są po polsku i angielsku,
        // a PostgreSQL nie ma wbudowanego słownika polskiego — stemmer 'english' psułby polskie słowa,
        // a mieszanie konfiguracji wymagałoby wykrywania języka (poza zakresem).
        public const string Configuration = "simple";

        // Kolumna generowana (STORED) utrzymywana przez PostgreSQL — bez synchronizacji w kodzie aplikacji.
        public const string PostSearchVector = "SearchVector";

        // Tytuł waga A (1.0), treść waga B (0.4) — domyślne wagi ts_rank.
        public const string PostSearchVectorSql =
            "setweight(to_tsvector('simple', coalesce(title, '')), 'A') || " +
            "setweight(to_tsvector('simple', coalesce(content, '')), 'B')";
    }
}
