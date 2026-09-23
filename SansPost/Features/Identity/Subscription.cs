namespace SansPost.Features.Identity
{
    // Wartości są zapisywane w bazie jako integer — nie zmieniaj ich bez migracji.
    public enum SubscriptionType
    {
        Free = 0,
        Premium = 1
    }

    public class Subscription
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public SubscriptionType Type { get; set; } = SubscriptionType.Free;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt { get; set; }

        public User User { get; set; } = null!;

        // Brak subskrypcji lub wygasły Premium oznacza Free.
        public static SubscriptionType EffectiveTypeOf(Subscription? subscription, DateTime utcNow)
        {
            if (subscription is null || subscription.Type != SubscriptionType.Premium)
                return SubscriptionType.Free;

            return subscription.ExpiresAt is null || subscription.ExpiresAt > utcNow
                ? SubscriptionType.Premium
                : SubscriptionType.Free;
        }
    }
}
