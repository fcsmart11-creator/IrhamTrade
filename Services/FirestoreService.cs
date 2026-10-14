using Google.Cloud.Firestore;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace IrhamTrade.Server.Services;

public class FirestoreService
{
    private readonly FirebaseService _firebaseService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<FirestoreService> _logger;

    public FirestoreService(FirebaseService firebaseService, AppDbContext dbContext, ILogger<FirestoreService> logger)
    {
        _firebaseService = firebaseService;
        _dbContext = dbContext;
        _logger = logger;
    }

    private FirestoreDb Firestore
    {
        get
        {
            if (_firebaseService.FirestoreDb == null)
                throw new InvalidOperationException("BOSS FIREBASE NOT INITIALIZED Boss - FirestoreDb is null Boss!");
            return _firebaseService.FirestoreDb;
        }
    }

    // ===== BOSS SAVE USER TO FIRESTORE - 100% MATCHES YOUR User.cs Boss =====
    public async Task<bool> SaveUserToFirestoreAsync(User user)
    {
        try
        {
            if (!_firebaseService.IsInitialized)
            {
                _logger.LogWarning("BOSS - Firebase not initialized Boss, skipping Firestore save Boss");
                return false;
            }

            var docRef = Firestore.Collection("users").Document(user.Id.ToString());

            // Boss Use YOUR actual properties Boss - 100% accurate Boss!
            var data = new Dictionary<string, object>
            {
                { "id", user.Id },
                { "email", user.Email },
                { "role", user.Role ?? "business" },
                { "businessName", user.BusinessName ?? "" },
                { "fullName", user.FullName ?? user.BusinessName ?? "" },
                { "phone", user.Phone ?? "" },
                { "location", user.Location ?? "" },
                { "firebaseUid", user.FirebaseUid ?? "" },
                { "createdAt", Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)) },
                { "lastLoginAt", user.LastLoginAt != null ? Timestamp.FromDateTime(DateTime.SpecifyKind(user.LastLoginAt.Value, DateTimeKind.Utc)) : Timestamp.GetCurrentTimestamp() },
                { "isVerified", true },
                { "isActive", true }
            };

            await docRef.SetAsync(data, SetOptions.MergeAll);
            Console.WriteLine($"BOSS - User saved to Firestore Boss: {user.Email} Boss!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS - Firestore save error Boss: {ex.Message} Boss");
            Console.WriteLine($"BOSS - Firestore error Boss: {ex.Message} Boss - But SQL saved Boss, so OK Boss");
            return false;
        }
    }

    // ===== BOSS GET BUSINESS DASHBOARD DATA Boss =====
    public async Task<Dictionary<string, object>> GetBusinessStatsAsync(string businessId)
    {
        try
        {
            var stats = new Dictionary<string, object>
            {
                { "totalProducts", 0 },
                { "totalOrders", 0 },
                { "totalRevenue", 0 },
                { "isLive", _firebaseService.IsInitialized }
            };

            if (_firebaseService.IsInitialized)
            {
                var productsRef = Firestore.Collection("businesses").Document(businessId).Collection("products");
                var snapshot = await productsRef.Count().GetSnapshotAsync();
                stats["totalProducts"] = snapshot.Count;
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS Get stats error: {ex.Message}");
            return new Dictionary<string, object> { { "error", ex.Message } };
        }
    }

    // ===== BOSS SAVE PRODUCT Boss =====
    public async Task<bool> SaveProductAsync(string businessId, Dictionary<string, object> productData)
    {
        try
        {
            if (!_firebaseService.IsInitialized) return false;

            var docRef = Firestore.Collection("businesses").Document(businessId).Collection("products").Document();
            productData["id"] = docRef.Id;
            productData["createdAt"] = Timestamp.GetCurrentTimestamp();

            await docRef.SetAsync(productData);
            Console.WriteLine($"BOSS - Product saved Boss: {docRef.Id} Boss!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS Save product error: {ex.Message}");
            return false;
        }
    }

    // ===== BOSS GET USER BY EMAIL FROM SQL Boss =====
    public async Task<User?> GetUserFromDbAsync(string email)
    {
        try
        {
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS Get user error: {ex.Message}");
            return null;
        }
    }
}

// Boss Alias for typo file Boss - So Program.cs won't crash Boss if you still have typo reference Boss!
public class FirestoreSercice : FirestoreService
{
    public FirestoreSercice(FirebaseService firebaseService, AppDbContext dbContext, ILogger<FirestoreService> logger)
        : base(firebaseService, dbContext, logger)
    {
    }
}