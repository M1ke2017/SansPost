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
    }
}
