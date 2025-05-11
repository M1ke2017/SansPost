using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SansPost.Models
{
    public class Comment
    {
        [Key]
        public int Id { get; set; } // Klucz główny

        // Powiązanie z postem
        [ForeignKey("Post")]
        public int PostId { get; set; }
        public Post Post { get; set; }

        // Powiązanie z użytkownikiem
        [ForeignKey("User")]
        public int UserId { get; set; }
        public User User { get; set; }

        public string Content { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
