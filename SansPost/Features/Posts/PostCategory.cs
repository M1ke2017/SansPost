namespace SansPost.Features.Posts
{
    // Zamknięty zestaw kategorii ogólnej platformy dyskusyjnej.
    // Zapisywane w bazie jako nazwa (varchar + CHECK constraint) — dodanie wartości wymaga migracji.
    public enum PostCategory
    {
        General,
        Technology,
        Games,
        Travel,
        Ideas,
        Projects,
        Feedback
    }

    public static class PostCategoryExtensions
    {
        public static string DisplayName(this PostCategory category) => category switch
        {
            PostCategory.General => "Ogólne",
            PostCategory.Technology => "Technologia",
            PostCategory.Games => "Gry",
            PostCategory.Travel => "Podróże",
            PostCategory.Ideas => "Pomysły",
            PostCategory.Projects => "Projekty",
            PostCategory.Feedback => "Feedback",
            _ => category.ToString()
        };
    }
}
