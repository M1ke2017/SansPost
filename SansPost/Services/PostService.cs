using Microsoft.EntityFrameworkCore;
using SansPost.Data;
using SansPost.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SansPost.Services
{
    public class PostService
    {
        private readonly AppliactionDbContext _context;

        public PostService(AppliactionDbContext context)
        {
            _context = context;
        }

        public async Task<bool> AddPost(int userId, string title, string content, string category, string? imageUrl = null)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                    return false;

                int userPostsCount = await _context.Posts.CountAsync(p => p.UserId == userId);
                int remaining = user.PostLimit - userPostsCount;

                if (remaining <= 0)
                    return false;

                var post = new Post
                {
                    UserId = userId,
                    Title = title,
                    Content = content,
                    Category = category,
                    ImageUrl = imageUrl,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Posts.Add(post);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return true;
            }
            catch
            {
                await transaction.RollbackAsync();
                return false;
            }
        }


        public async Task<Post?> GetPostById(int postId)
        {
            return await _context.Posts.FindAsync(postId);
        }

        public async Task<List<Post>> GetUserPosts(int userId)
        {
            return await _context.Posts.Where(p => p.UserId == userId).ToListAsync();
        }

        public async Task<bool> DeletePost(int userId, int postId)
        {
            var post = await _context.Posts.FindAsync(postId);
            if (post == null || post.UserId != userId)
                return false;

            _context.Posts.Remove(post);
            await _context.SaveChangesAsync();
            return true;
        }
        public async Task<int> GetRemainingPosts(int userId)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return 0;

            int userPostsCount = await _context.Posts.CountAsync(p => p.UserId == userId);
            return user.PostLimit - userPostsCount;
        }

        public async Task<List<Post>> GetAllPosts()
        {
            return await _context.Posts.ToListAsync();
        }

        public async Task<bool> UpdatePost(Post post)
        {
            var existingPost = await _context.Posts.FindAsync(post.Id);

            if (existingPost == null) return false;

            existingPost.Title = post.Title;
            existingPost.Content = post.Content;
            existingPost.Category = post.Category;
            existingPost.ImageUrl = post.ImageUrl;

            await _context.SaveChangesAsync();
            return true;
        }



    }
}
