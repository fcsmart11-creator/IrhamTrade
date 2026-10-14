using System.ComponentModel.DataAnnotations;

namespace IrhamTrade.Server.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; } // Auto increment Boss for SQLite Boss

        [Required]
        public string Email { get; set; } = "";

        [Required]
        public string PasswordHash { get; set; } = ""; // BCrypt Boss - secure Boss

        [Required]
        public string Role { get; set; } = ""; // "business" or "buyer" Boss

        public string? BusinessName { get; set; } // For business Boss

        public string? FullName { get; set; } // For buyer Boss

        public string? Phone { get; set; }

        public string? Location { get; set; }

        public string? FirebaseUid { get; set; } // Firebase ID Boss for Android APK Boss

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastLoginAt { get; set; }
    }
}