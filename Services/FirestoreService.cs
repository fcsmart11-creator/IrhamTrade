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
                throw new InvalidOperationException("Firestore is not initialized - running in SQLite-only mode");
            return _firebaseService.FirestoreDb;
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string operationName, int retries = 3)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var task = action();
                var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                if (completed == task) return await task;
                throw new TimeoutException($"{operationName} timed out after 30s");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Operation} attempt {Attempt} failed", operationName, attempt);
                if (attempt == retries)
                {
                    _logger.LogError(ex, "{Operation} failed after {Retries} attempts", operationName, retries);
                    return fallback;
                }
                await Task.Delay(attempt == 1 ? 1500 : attempt == 2 ? 3000 : 6000);
            }
        }
        return fallback;
    }

    // =========================================================
    // USERS - Business + Buyer - Like Facebook backend
    // =========================================================
    public async Task<bool> SaveUserToFirestoreAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;

        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = Firestore.Collection("users").Document(user.Id.ToString());
                var data = new Dictionary<string, object>
                {
                    { "id", user.Id },
                    { "email", user.Email },
                    { "role", user.Role?? "business" },
                    { "businessName", user.BusinessName?? "" },
                    { "fullName", user.FullName?? user.BusinessName?? "" },
                    { "phone", user.Phone?? "" },
                    { "location", user.Location?? "" },
                    { "firebaseUid", user.FirebaseUid?? "" },
                    { "createdAt", Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)) },
                    { "lastLoginAt", user.LastLoginAt!= null? Timestamp.FromDateTime(DateTime.SpecifyKind(user.LastLoginAt.Value, DateTimeKind.Utc)) : Timestamp.GetCurrentTimestamp() },
                    { "isVerified", true },
                    { "isActive", true },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };
                await docRef.SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("User saved to Firestore: {Email} Role: {Role}", user.Email, user.Role);
                return true;
            }, false, $"SaveUser {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user to Firestore: {Email}", user.Email);
            return false;
        }
    }

    public async Task<bool> SaveBuyerProfileAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = Firestore.Collection("buyers").Document(user.Id.ToString());
                // Also save in users collection for unified lookup
                var userRef = Firestore.Collection("users").Document(user.Id.ToString());

                var data = new Dictionary<string, object>
                {
                    { "id", user.Id },
                    { "buyerId", user.Id.ToString() },
                    { "email", user.Email },
                    { "fullName", user.FullName?? "" },
                    { "name", user.FullName?? "" },
                    { "phone", user.Phone?? "" },
                    { "location", user.Location?? "" },
                    { "role", "buyer" },
                    { "firebaseUid", user.FirebaseUid?? "" },
                    { "createdAt", Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)) },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };
                await docRef.SetAsync(data, SetOptions.MergeAll);
                await userRef.SetAsync(data, SetOptions.MergeAll);
                return true;
            }, false, $"SaveBuyer {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save buyer: {Email}", user.Email);
            return false;
        }
    }

    // =========================================================
    // PRODUCTS - CRITICAL! This is what makes buyer see LIVE!
    // When Business uploads 2 products, they go to Firestore products collection
    // =========================================================
    public async Task<bool> SaveProductToFirestoreAsync(int productId, string businessId, string businessName, string name, string category, double price, string description, string imageUrl, string location)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = Firestore.Collection("products").Document(productId.ToString());
                var data = new Dictionary<string, object>
                {
                    { "id", productId },
                    { "productId", productId.ToString() },
                    { "businessId", businessId },
                    { "ownerId", businessId },
                    { "businessName", businessName?? "" },
                    { "name", name },
                    { "productName", name },
                    { "title", name },
                    { "category", category?? "General" },
                    { "price", price },
                    { "productPrice", price },
                    { "description", description?? "" },
                    { "imageUrl", imageUrl?? "" },
                    { "image", imageUrl?? "" },
                    { "location", location?? "" },
                    { "isAvailable", true },
                    { "isActive", true },
                    { "stock", 100 },
                    { "createdAt", Timestamp.GetCurrentTimestamp() },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };
                await docRef.SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("Product saved LIVE to Firestore: {Name} Business: {Biz}", name, businessName);
                return true;
            }, false, $"SaveProduct {name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save product: {Name}", name);
            return false;
        }
    }

    public async Task<bool> DeleteProductFromFirestoreAsync(int productId)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                await Firestore.Collection("products").Document(productId.ToString()).DeleteAsync();
                return true;
            }, false, $"DeleteProduct {productId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete product: {Id}", productId);
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
            _logger.LogError(ex, "Failed to get user from DB: {Email}", email);
            return null;
        }
    }

    public async Task<User?> GetUserFromDbByIdAsync(string id)
    {
        try
        {
            if (!int.TryParse(id, out var intId)) return null;
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == intId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user by Id: {Id}", id);
            return null;
        }
    }
}