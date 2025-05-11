using Microsoft.EntityFrameworkCore;
using SansPost.Data;
using SansPost.Models;
using System.Threading.Tasks;
using BCrypt.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace SansPost.Services
{
    public class UserService
    {
        private readonly AppliactionDbContext _context;

        public UserService(AppliactionDbContext context)
        {
            _context = context;
        }

        public async Task<bool> RegisterUser(User user)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var existingUser = await _context.Users
                    .AnyAsync(u => u.Email == user.Email || u.Username == user.Username);

                if (existingUser)
                    return false;

                user.Role = UserRole.Regular;
                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                // Możesz tu dodać np. logi, subskrypcję itp.

                await transaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Rejestracja nieudana: {ex.Message}");
                return false;
            }
        }


        public async Task<bool> AssignSubscription(int userId, SubscriptionType type, DateTime expiryDate)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var user = await _context.Users.Include(u => u.Subscription).FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null) return false;

                Console.WriteLine($"Przypisywanie subskrypcji użytkownikowi {user.Email}");

                // Jeśli użytkownik ma już subskrypcję, aktualizujemy
                if (user.Subscription != null)
                {
                    user.Subscription.Type = type;
                    user.Subscription.ExpiresAt = expiryDate;
                }
                else
                {
                    // Jeśli nie ma subskrypcji, tworzymy nową
                    user.Subscription = new Subscription
                    {
                        UserId = user.Id,
                        Type = type,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = expiryDate
                    };
                    _context.Subscriptions.Add(user.Subscription);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                Console.WriteLine(" Subskrypcja dodana!");
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine("Błąd przypisania subskrypcji: " + ex.Message);
                return false;
            }
        }


        public async Task<User?> GetUserById(int userId)
        {
            return await _context.Users.FindAsync(userId);
        }

        public async Task<string?> Authenticate(string email, string password)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                return null;

            var claims = new[]
            {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), 
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role.ToString())
    };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("CHANGE_ME")); 
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.UtcNow.AddHours(2),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task<bool> ChangeUserRole(int userId, UserRole newRole)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                return false;

            user.Role = newRole;
            await _context.SaveChangesAsync();
            return true;
        }

 

        // Funkcja do hashowania hasła
        public static string HashPassword(string password)
        {
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            string hashed = Convert.ToBase64String(KeyDerivation.Pbkdf2(
                password: password,
                salt: salt,
                prf: KeyDerivationPrf.HMACSHA256,
                iterationCount: 10000,
                numBytesRequested: 32));

            return hashed;
        }

        // Funkcja do weryfikacji hasła
        public static bool VerifyPassword(string password, string hashedPassword)
        {
            return HashPassword(password) == hashedPassword;
        }
    }
}
