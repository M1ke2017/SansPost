using SansPost.Features.Posts;

namespace SansPost.Shared.Ui
{
    // Stan edytora posta w UI. Bez ImageUrl — pole legacy usunięte z kontraktu żądania (Sprint 10); istniejąca
    // wartość w bazie nie jest zmieniana przy edycji.
    public sealed class PostDraft
    {
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public PostCategory? Category { get; set; }

        public PostRequest ToRequest() => new()
        {
            Title = Title.Trim(),
            Content = Content,
            Category = Category
        };

        public PostDraft Clone() => new() { Title = Title, Content = Content, Category = Category };
    }
}
