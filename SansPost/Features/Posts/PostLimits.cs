namespace SansPost.Features.Posts
{
    // Jedno źródło limitów — używane przez DTO (walidacja) i mapowanie EF (długości kolumn).
    public static class PostLimits
    {
        public const int TitleMinLength = 3;
        public const int TitleMaxLength = 150;
        public const int ContentMaxLength = 10_000;
        public const int ImageUrlMaxLength = 2048;
        public const int CategoryMaxLength = 32;

        public const int PreviewLength = 280;
        public const int DefaultPageSize = 20;
        public const int MaxPageSize = 50;

        public const int FreePostLimit = 10;
        public const int PremiumPostLimit = 50;

        // Popular: score = PopularHoursPerEngagementUnit × ln(1 + likes + PopularCommentWeight × comments) − wiek_w_godzinach.
        // Każda godzina wieku kosztuje 1 punkt; e-krotny wzrost zaangażowania jest wart 24 godziny świeżości.
        public const double PopularHoursPerEngagementUnit = 24.0;
        public const int PopularCommentWeight = 2;

        // Okno kandydatów — ogranicza skan do świeżych postów (zakres na IX_posts_feed / IX_posts_category_feed).
        public const int PopularWindowDays = 30;

        // Score w SQL mnożony i zaokrąglany do bigint → dokładny, deterministyczny klucz kursora.
        public const double ScoreScale = 1000.0;
    }
}
