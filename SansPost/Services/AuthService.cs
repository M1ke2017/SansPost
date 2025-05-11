using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using BCrypt.Net;
using SansPost.Data;
using SansPost.Models;

namespace SansPost.Services
{
    public class AuthService
    {
        private readonly AppliactionDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthService(AppliactionDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<bool> RegisterUser(string username, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);

            var user = new User
            {
                Username = username,
                Email = email,
                PasswordHash = hashedPassword,
                Role = UserRole.User,
                Subscription = new Subscription
                {
                    Type = SubscriptionType.Free,
                    CreatedAt = DateTime.UtcNow
                }
            };

            user.Subscription.User = user;

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            Console.WriteLine($"Zapisano użytkownika (ID: {user.Id}), subskrypcja (ID: {user.Subscription?.Id})");

            return true;
        }

        public async Task<string?> LoginUser(string email, string password)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            {
                return null; 
            }
            return GenerateJwtToken(user);
        }

        private string GenerateJwtToken(User user)
        {
            var jwtKey = _configuration["Jwt:Key"];
            Console.WriteLine($"Loaded JWT Key: {jwtKey}");
            if (string.IsNullOrEmpty(jwtKey))
            {
                throw new Exception("Brak klucza JWT w konfiguracji!");
            }

            var key = Encoding.UTF8.GetBytes(jwtKey);
            var tokenHandler = new JwtSecurityTokenHandler();
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role.ToString()) // Obsługa null
                }),
                Expires = DateTime.UtcNow.AddHours(2),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}
