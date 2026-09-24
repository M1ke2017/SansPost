namespace SansPost.Features.Identity
{
    // Sekcja "PublicDemo" — pojemność publicznej instancji.
    public sealed class PublicDemoOptions
    {
        public const string SectionName = "PublicDemo";

        public bool RegistrationEnabled { get; set; } = true;

        // Liczba zwykłych kont (Role = User, w dowolnym statusie). Admini nie zajmują slotów.
        public int MaxPublicAccounts { get; set; } = 100;
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
