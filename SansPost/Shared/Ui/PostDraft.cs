using SansPost.Features.Posts;

namespace SansPost.Shared.Ui
{
    // Stan edytora posta w UI. ImageUrl celowo pominięty — pole bez realnego użycia (decyzja: Sprint 10);
    // przy edycji istniejąca wartość jest przenoszona bez zmian, więc kompatybilność backendu zostaje.
    public sealed class PostDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public PostCategory? Category { get; set; }

        public PostRequest ToRequest(string? preservedImageUrl = null) => new()
        {
            Title = Title.Trim(),
            Content = Content,
            Category = Category,
            ImageUrl = preservedImageUrl
        };

        public PostDraft Clone() => new() { Title = Title, Content = Content, Category = Category };
    }
}
