using Microsoft.EntityFrameworkCore;
using IrhamTrade.Server.Models;

namespace IrhamTrade.Server.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // This creates table "Users" in irhamtrade.db Boss!
        public DbSet<User> Users { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);

                // Email unique Boss - no duplicate Boss - important for huge users Boss!
                entity.HasIndex(u => u.Email).IsUnique();

                // Fast search Boss
                entity.HasIndex(u => u.Role);

                // FirebaseUid unique Boss if exists Boss
                entity.HasIndex(u => u.FirebaseUid);

                entity.Property(u => u.Email).IsRequired().HasMaxLength(200);
                entity.Property(u => u.Role).IsRequired().HasMaxLength(20);
                entity.Property(u => u.PasswordHash).IsRequired();
            });
        }
    }
}