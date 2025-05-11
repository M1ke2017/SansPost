namespace SansPost.Models
{
    public enum SubscriptionType
    {
        Free,
        Premium
    }

    public class Subscription
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public SubscriptionType Type { get; set; } = SubscriptionType.Free;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt { get; set; }

        // Powiązanie z użytkownikiem
        public User User { get; set; }
    }
}
