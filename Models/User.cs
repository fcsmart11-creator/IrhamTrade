using System.ComponentModel.DataAnnotations;

namespace IrhamTrade.Server.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(256)]
        public string Email { get; set; } = "";

        [Required]
        public string PasswordHash { get; set; } = "";

        [Required]
        [MaxLength(20)]
        public string Role { get; set; } = "";

        [MaxLength(200)]
        public string? BusinessName { get; set; }

        [MaxLength(200)]
        public string? FullName { get; set; }

        [MaxLength(20)]
        public string? Phone { get; set; }

        [MaxLength(100)]
        public string? Location { get; set; }

        [MaxLength(128)]
        public string? FirebaseUid { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastLoginAt { get; set; }
    }
}