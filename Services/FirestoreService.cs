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

    public async Task<bool> SaveUserToFirestoreAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docId = user.FirebaseUid;
                if (string.IsNullOrWhiteSpace(docId)) docId = Guid.NewGuid().ToString("N");
                if (docId == "1" || docId == "2" || docId.Length < 10) docId = Guid.NewGuid().ToString("N");
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
                _logger.LogInformation("User saved to Firestore ONE TIME: users/{DocId}", docId);
                return true;
            }, false, $"SaveUser {user.Email}");
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to save user to Firestore: {Email}", user.Email); return false; }
    }

    public async Task<bool> SaveBuyerProfileAsync(User user)
    {
        if (!_firebaseService.IsInitialized) return false;
        try
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var docId = user.FirebaseUid;
                if (string.IsNullOrWhiteSpace(docId)) docId = Guid.NewGuid().ToString("N");
                if (docId == "1" || docId == "2" || docId.Length < 10) docId = Guid.NewGuid().ToString("N");
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
                return true;
            }, false, $"SaveBuyer {user.Email}");
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to save buyer: {Email}", user.Email); return false; }
    }

    public async Task<bool> SaveProductToFirestoreAsync(int productId, string businessId, string businessName, string name, string category, double price, string description, string imageUrl, string location)
    {
        if (!_firebaseService.IsInitialized) return false;
        if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10) return false;
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
                return true;
            }, false, $"SaveProduct {name}");
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to save product: {Name}", name); return false; }
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
        catch (Exception ex) { _logger.LogError(ex, "Failed to delete product: {Id}", productId); return false; }
    }

    public async Task<User?> GetUserFromDbAsync(string email)
    {
        try { return await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get user from DB: {Email}", email); return null; }
    }

    public async Task<User?> GetUserFromDbByIdAsync(string id)
    {
        try
        {
            if (!int.TryParse(id, out var intId)) return await _dbContext.Users.FirstOrDefaultAsync(u => u.FirebaseUid == id);
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == intId);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get user by Id: {Id}", id); return null; }
    }

    // ========== BOSS NEW: BUYER HISTORY FOR 3 JOBS / 2 JOBS - REAL CODE ==========
    public async Task<string> CreateBuyerHistoryAsync(string buyerId, string buyerPhone, string buyerName, string businessId, string businessName, string productId, string productName, string productImage, string message, string status)
    {
        if (!_firebaseService.IsInitialized) return "";
        return await ExecuteWithRetryAsync(async () =>
        {
            var docId = Guid.NewGuid().ToString("N");
            var data = new Dictionary<string, object>
            {
                ["id"] = docId,
                ["buyerId"] = buyerId ?? "",
                ["buyerPhone"] = buyerPhone ?? "",
                ["buyerName"] = buyerName ?? "Buyer",
                ["businessId"] = businessId ?? "",
                ["businessName"] = businessName ?? "Irham Trade",
                ["deviceId"] = productId ?? "",
                ["productId"] = productId ?? "",
                ["productName"] = productName ?? "Product",
                ["productImage"] = productImage ?? "",
                ["message"] = message ?? "",
                ["businessResponse"] = message ?? "",
                ["status"] = status ?? "Pending",
                ["read"] = false,
                ["createdAt"] = Timestamp.GetCurrentTimestamp(),
                ["updatedAt"] = Timestamp.GetCurrentTimestamp()
            };
            await Firestore.Collection("buyer_history").Document(docId).SetAsync(data);
            _logger.LogInformation("BOSS FirestoreService BuyerHistory Created {DocId}", docId);
            return docId;
        }, "", $"CreateBuyerHistory {buyerId}");
    }

    public async Task<List<Dictionary<string, object>>> GetBuyerHistoryAsync(string buyerId)
    {
        if (!_firebaseService.IsInitialized) return new();
        return await ExecuteWithRetryAsync(async () =>
        {
            var snap = await Firestore.Collection("buyer_history").WhereEqualTo("buyerId", buyerId).OrderByDescending("createdAt").GetSnapshotAsync();
            if (snap.Count == 0) snap = await Firestore.Collection("buyer_history").WhereEqualTo("buyerPhone", buyerId).OrderByDescending("createdAt").GetSnapshotAsync();
            var list = new List<Dictionary<string, object>>();
            foreach (var doc in snap.Documents) { var dict = doc.ToDictionary(); dict["id"] = doc.Id; list.Add(dict); }
            return list;
        }, new List<Dictionary<string, object>>(), $"GetBuyerHistory {buyerId}");
    }
}