namespace SansPost.Features.Identity
{
    // Sekcja "PublicDemo" — pojemność publicznej instancji.
    public sealed class PublicDemoOptions
    {
        public const string SectionName = "PublicDemo";

        public bool RegistrationEnabled { get; set; } = true;

        // Liczba zwykłych kont (Role = User, w dowolnym statusie). Admini nie zajmują slotów.
        public int MaxPublicAccounts { get; set; } = 100;

        // Opcjonalny post przypięty na stronie głównej (np. "Witaj w SansPost") — zwykły post utworzony w aplikacji,
        // wskazany tylko identyfikatorem. Treść nie jest zaszyta w UI. Ukryty/usunięty post po prostu się nie wyświetla.
        public int? FeaturedPostId { get; set; }

        // Treści startowe (konta demo z przydomkami, posty, komentarze, reakcje) tworzone raz przy starcie — DemoContentSeeder.
        // Konta demo zajmują publiczne sloty (Role = User). Domyślnie wyłączone.
        public bool SeedContent { get; set; }
    }

    // Sekcja "BootstrapAdmin" — jednorazowe utworzenie pierwszego administratora.
    // Sekrety wyłącznie z User Secrets / zmiennych środowiskowych; po pierwszym wdrożeniu wyłączyć (Enabled = false).
    public sealed class BootstrapAdminOptions
    {
        public const string SectionName = "BootstrapAdmin";

        public bool Enabled { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
