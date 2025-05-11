using SansPost.Data;
using SansPost.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace SansPost.Services
{
    public class LikeService
    {
        private readonly AppliactionDbContext _context;

        public LikeService(AppliactionDbContext context)
        {
            _context = context;
        }

        public async Task<int> GetLikeCount(int postId)
        {
            return await _context.Likes.CountAsync(l => l.PostId == postId); 
        }

        public async Task<bool> ToggleLike(int postId, int userId)
        {
            var existingLike = await _context.Likes
                .FirstOrDefaultAsync(l => l.PostId == postId && l.UserId == userId );

            if(existingLike != null)
            {
                _context.Likes.Remove(existingLike);
            }
            else
            {
                _context.Likes.Add(new Like { PostId = postId, UserId = userId });
            }

            await _context.SaveChangesAsync();
            return true;
        }
    }
}
