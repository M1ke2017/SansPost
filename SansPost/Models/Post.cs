namespace SansPost.Models
{
    public class Post
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ? ImageUrl { get; set; } 
        public string ? Category { get; set; } 
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public  User? User  { get; set; } // Realcja z użytkownikiem
    }
}
