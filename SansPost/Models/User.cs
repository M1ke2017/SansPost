namespace SansPost.Models
{
    public enum UserRole
    {
        User,
        Premium,
        Admin,
        Regular
    }

    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.User;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Powiązanie użytkownika z subskrypcją
        public Subscription Subscription { get; set; }

        // Limit postów bazujący na subskrypcji użytkownika
        public int PostLimit => Subscription?.Type == SubscriptionType.Premium ? 50 : 10;

        public User()
        {
            CreatedAt = DateTime.UtcNow;
            Role = UserRole.User;
            Subscription = new Subscription
            {
                Type = SubscriptionType.Free,
                CreatedAt = DateTime.UtcNow
            };
        }
     

    public static User CreateWithSubscription(int id, SubscriptionType type)
        {
            return new User
            {
                Id = id,
                Subscription = new Subscription { Type = type }
            };

        }
    }
}
