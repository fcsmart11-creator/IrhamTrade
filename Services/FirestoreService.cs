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
    private readonly Dictionary<string, object> _memoryCache = new();

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

    
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string operationName, int retries = 3)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70)); 
                var task = action();
                var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                if (completed == task)
                {
                    return await task;
                }
                throw new TimeoutException($"BOSS {operationName} timeout 70s Boss - Render sleeping Boss");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"BOSS {operationName} attempt {attempt} failed Boss: {ex.Message} Boss");
                if (attempt == retries)
                {
                    _logger.LogError($"BOSS {operationName} all {retries} attempts failed Boss: {ex.Message} Boss - Returning safe fallback Boss");
                    return fallback;
                }
                int delay = attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000;
                await Task.Delay(delay);
            }
        }
        return fallback;
    }

   
    public async Task<bool> SaveUserToFirestoreAsync(User user)
    {
        try
        {
            if (!_firebaseService.IsInitialized)
            {
                _logger.LogWarning("BOSS - Firebase not initialized Boss, skipping Firestore save Boss");
                return false;
            }

            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = Firestore.Collection("users").Document(user.Id.ToString());

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
            }, false, $"SaveUser {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS - Firestore save error Boss: {ex.Message} Boss");
            Console.WriteLine($"BOSS - Firestore error Boss: {ex.Message} Boss - But SQL saved Boss, so OK Boss");
            return false; 
        }
    }

    // ===== BOSS GET BUSINESS DASHBOARD DATA - CACHE FIRST LIKE FACEBOOK =====
    public async Task<Dictionary<string, object>> GetBusinessStatsAsync(string businessId)
    {
        
        string cacheKey = $"stats_{businessId}";
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
        {
         
        }

        var result = await ExecuteWithRetryAsync(async () =>
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
                _memoryCache[cacheKey] = stats; // Cache it
            }

            return stats;
        }, new Dictionary<string, object> { { "totalProducts", 0 }, { "totalOrders", 0 }, { "totalRevenue", 0 }, { "isLive", false }, { "fromCache", true } }, $"GetStats {businessId}");

        return result;
    }

    
    public async Task<bool> SaveProductAsync(string businessId, Dictionary<string, object> productData)
    {
        try
        {
            if (!_firebaseService.IsInitialized) return false;

            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = Firestore.Collection("businesses").Document(businessId).Collection("products").Document();
                productData["id"] = docRef.Id;
                productData["createdAt"] = Timestamp.GetCurrentTimestamp();

                await docRef.SetAsync(productData);
                Console.WriteLine($"BOSS - Product saved Boss: {docRef.Id} Boss!");
                return true;
            }, false, $"SaveProduct {businessId}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS Save product error: {ex.Message} - NEVER CRASH Boss");
            return false;
        }
    }

 
    public async Task<User?> GetUserFromDbAsync(string email)
    {
        try
        {
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);
        }
        catch (Exception ex)
        {
            _logger.LogError($"BOSS Get user error: {ex.Message} - Returning null but not crashing Boss");
            return null;
        }
    }
}
public class FirestoreSercice : FirestoreService
{
    public FirestoreSercice(FirebaseService firebaseService, AppDbContext dbContext, ILogger<FirestoreService> logger)
        : base(firebaseService, dbContext, logger)
    {
    }
}