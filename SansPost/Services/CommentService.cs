using SansPost.Data;
using SansPost.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SansPost.Services
{
    public class CommentService
    {
        private readonly AppliactionDbContext _context;

        public CommentService(AppliactionDbContext context)
        {
            _context = context;
        }

        public async Task<List<Comment>> GetCommentsByPost(int postId)
        {
            return await _context.Comments
                .Where(c => c.PostId == postId)
                .Include(c => c.User)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
        
        public async Task<bool> AddComment(int postId, int userId, string content)
        {
            var comment = new Comment
            {
                PostId = postId,
                UserId = userId,
                Content = content
            };
            _context.Comments.Add(comment);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteComment(int commentId, int userId)
        {
            var comment = await _context.Comments.FindAsync(commentId);
            if (comment == null || comment.UserId != userId) return false;

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();
            return true;
        }
    }
}
