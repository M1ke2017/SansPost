namespace SansPost.Models
{
    public class RegisterUserRequest
    {
        public string Username { get; set; }
        public string Email { get; set; } 
        public string Password { get; set; }

        public Subscription? Subscription { get; set; } = new Subscription();
    }
}
