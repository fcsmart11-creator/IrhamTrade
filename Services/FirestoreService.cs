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

    // ========== BOSS FIX 100% - ONLY USERS COLLECTION - ONLY REAL FIREBASE UID - NO DOUBLE ==========
    public async Task<bool> SaveUserToFirestoreAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;

        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docId = user.FirebaseUid;
                if (string.IsNullOrWhiteSpace(docId))
                {
                    docId = Guid.NewGuid().ToString("N");
                    _logger.LogWarning("SaveUserToFirestore: FirebaseUid missing for {Email}, using Guid {DocId} instead of SQL Id {SqlId}", user.Email, docId, user.Id);
                }

                if (docId == "1" || docId == "2" || docId.Length < 10)
                {
                    docId = Guid.NewGuid().ToString("N");
                    _logger.LogWarning("SaveUserToFirestore: Blocked bad docId, using Guid {DocId}", docId);
                }

                var docRef = Firestore.Collection("users").Document(docId);

                var data = new Dictionary<string, object>
                {
                    { "id", docId },
                    { "idString", docId },
                    { "businessId", docId },
                    { "ownerId", docId },
                    { "buyerId", docId },
                    { "userId", docId },
                    { "firebaseUid", user.FirebaseUid ?? docId },
                    { "firebase_uid", user.FirebaseUid ?? docId },
                    { "email", user.Email ?? "" },
                    { "role", user.Role ?? "business" },
                    { "businessName", user.BusinessName ?? "" },
                    { "fullName", !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName : user.BusinessName ?? "" },
                    { "phone", user.Phone ?? "" },
                    { "location", user.Location ?? "" },
                    { "createdAt", Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)) },
                    { "lastLoginAt", user.LastLoginAt != null ? Timestamp.FromDateTime(DateTime.SpecifyKind(user.LastLoginAt.Value, DateTimeKind.Utc)) : Timestamp.GetCurrentTimestamp() },
                    { "isVerified", true },
                    { "isActive", true },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };

                await docRef.SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("User saved to Firestore ONE TIME: users/{DocId} Email: {Email} Role: {Role}", docId, user.Email, user.Role);
                return true;
            }, false, $"SaveUser {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user to Firestore: {Email}", user.Email);
            return false;
        }
    }

    // ========== BOSS FIX - BUYER ALSO ONLY USERS - NO BUYERS COLLECTION - NO DOUBLE ==========
    public async Task<bool> SaveBuyerProfileAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docId = user.FirebaseUid;
                if (string.IsNullOrWhiteSpace(docId))
                    docId = Guid.NewGuid().ToString("N");

                if (docId == "1" || docId == "2" || docId.Length < 10)
                    docId = Guid.NewGuid().ToString("N");

                var docRef = Firestore.Collection("users").Document(docId);

                var data = new Dictionary<string, object>
                {
                    { "id", docId },
                    { "idString", docId },
                    { "buyerId", docId },
                    { "userId", docId },
                    { "businessId", docId },
                    { "ownerId", docId },
                    { "email", user.Email ?? "" },
                    { "fullName", user.FullName ?? "" },
                    { "name", user.FullName ?? "" },
                    { "phone", user.Phone ?? "" },
                    { "location", user.Location ?? "" },
                    { "role", "buyer" },
                    { "firebaseUid", user.FirebaseUid ?? docId },
                    { "createdAt", Timestamp.FromDateTime(DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc)) },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };

                await docRef.SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("Buyer saved to Firestore ONE TIME: users/{DocId} Email: {Email}", docId, user.Email);
                return true;
            }, false, $"SaveBuyer {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save buyer: {Email}", user.Email);
            return false;
        }
    }

    // ========== BOSS FIX - PRODUCTS - ONLY DEVICES COLLECTION - NO PRODUCTS COLLECTION - NO DOUBLE ==========
    public async Task<bool> SaveProductToFirestoreAsync(int productId, string businessId, string businessName, string name, string category, double price, string description, string imageUrl, string location)
    {
        if (!_firebaseService.IsInitialized) return false;

        if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10)
        {
            _logger.LogWarning("SaveProduct blocked bad businessId {BusinessId} for product {Name}", businessId, name);
            return false;
        }

        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docId = productId.ToString();
                var docRef = Firestore.Collection("devices").Document(docId);

                var data = new Dictionary<string, object>
                {
                    { "id", productId },
                    { "idString", docId },
                    { "productId", docId },
                    { "deviceId", docId },
                    { "businessId", businessId },
                    { "ownerId", businessId },
                    { "userId", businessId },
                    { "businessName", businessName ?? "" },
                    { "name", name },
                    { "productName", name },
                    { "title", name },
                    { "model", name },
                    { "brand", category ?? "General" },
                    { "category", category ?? "General" },
                    { "price", price },
                    { "productPrice", price },
                    { "description", description ?? "" },
                    { "imageUrl", imageUrl ?? "" },
                    { "image", imageUrl ?? "" },
                    { "imageBase64", imageUrl ?? "" },
                    { "location", location ?? "" },
                    { "contactNumber", "" },
                    { "isAvailable", true },
                    { "isActive", true },
                    { "status", "active" },
                    { "stock", 100 },
                    { "createdAt", Timestamp.GetCurrentTimestamp() },
                    { "updatedAt", Timestamp.GetCurrentTimestamp() }
                };

                await docRef.SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("Product saved LIVE to Firestore ONE TIME: devices/{Id} Business: {Biz} RealId: {RealId}", docId, businessName, businessId);
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
                await Firestore.Collection("devices").Document(productId.ToString()).DeleteAsync();
                try { await Firestore.Collection("products").Document(productId.ToString()).DeleteAsync(); } catch { }
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
            if (!int.TryParse(id, out var intId))
            {
                return await _dbContext.Users.FirstOrDefaultAsync(u => u.FirebaseUid == id);
            }
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == intId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user by Id: {Id}", id);
            return null;
        }
    }
}