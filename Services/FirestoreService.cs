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

    // Purpose: Execute with retry and timeout - prevents server crash if Firestore slow. Returns fallback on failure. Facebook standard: Never throw 500.
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

    private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

    // Purpose: Save business user to Firestore users collection. Generates valid docId if FirebaseUid bad (1,2,<10). MergeAll to avoid overwrite.
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

    // Purpose: Save buyer profile to users collection with role buyer.
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

    // Purpose: Save product to Firestore devices collection. Validates businessId, rejects bad IDs. Sets isAvailable true, isReserved false, isSold false for marketplace LIVE.
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
                    { "isReserved", false },
                    { "isSold", false },
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

    // Purpose: Delete product from devices and products collections. Used for SOLD forever flow.
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

    // Purpose: Get user from SQLite DB by email.
    public async Task<User?> GetUserFromDbAsync(string email)
    {
        try { return await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get user from DB: {Email}", email); return null; }
    }

    // Purpose: Get user from DB by ID (int or FirebaseUid string).
    public async Task<User?> GetUserFromDbByIdAsync(string id)
    {
        try
        {
            if (!int.TryParse(id, out var intId)) return await _dbContext.Users.FirstOrDefaultAsync(u => u.FirebaseUid == id);
            return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == intId);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get user by Id: {Id}", id); return null; }
    }

    // BOSS FIX PROFESSIONAL: Create buyer_history now saves buyerId + userId + buyerPhone - Ensures fresh account logic - New UID xyz789 = Empty Fresh!
    public async Task<string> CreateBuyerHistoryAsync(string buyerId, string buyerPhone, string buyerName, string businessId, string businessName, string productId, string productName, string productImage, string message, string status)
    {
        if (!_firebaseService.IsInitialized) return "";
        if (IsBadId(buyerId)) return "";
        return await ExecuteWithRetryAsync(async () =>
        {
            var docId = Guid.NewGuid().ToString("N");
            var data = new Dictionary<string, object>
            {
                ["id"] = docId,
                ["buyerId"] = buyerId ?? "",
                ["userId"] = buyerId ?? "",
                ["buyerPhone"] = buyerPhone ?? "",
                ["buyerName"] = buyerName ?? "Buyer",
                ["businessId"] = businessId ?? "",
                ["businessName"] = businessName ?? "Irham Trade",
                ["deviceId"] = productId ?? "",
                ["productId"] = productId ?? "",
                ["productName"] = productName ?? "Product",
                ["productImage"] = productImage ?? "",
                ["imageBase64"] = productImage ?? "",
                ["message"] = message ?? "",
                ["businessResponse"] = message ?? "",
                ["status"] = status ?? "Pending",
                ["read"] = false,
                ["createdAt"] = Timestamp.GetCurrentTimestamp(),
                ["updatedAt"] = Timestamp.GetCurrentTimestamp()
            };
            await Firestore.Collection("buyer_history").Document(docId).SetAsync(data);
            _logger.LogInformation("BOSS FirestoreService BuyerHistory Created {DocId} for buyer {Buyer} status {Status} - New UID fresh", docId, buyerId, status);
            return docId;
        }, "", $"CreateBuyerHistory {buyerId}");
    }

    // BOSS FIX PROFESSIONAL: Get buyer history by buyerId ONLY - No phone fallback leak - New UID xyz789 = Empty Fresh! Old UID abc123 login = Full history Restore! Facebook standard!
    public async Task<List<Dictionary<string, object>>> GetBuyerHistoryAsync(string buyerId)
    {
        if (!_firebaseService.IsInitialized) return new();
        if (IsBadId(buyerId)) return new();
        return await ExecuteWithRetryAsync(async () =>
        {
            // PROFESSIONAL: Query only by buyerId - Ensures new account with same phone gets fresh empty history
            var snap = await Firestore.Collection("buyer_history").WhereEqualTo("buyerId", buyerId).OrderByDescending("createdAt").GetSnapshotAsync();
            if (snap.Count == 0)
            {
                // Try userId field for backward compatibility
                snap = await Firestore.Collection("buyer_history").WhereEqualTo("userId", buyerId).OrderByDescending("createdAt").GetSnapshotAsync();
            }
            // DO NOT fallback to buyerPhone - That would leak old accounts! New UID must be fresh!
            _logger.LogInformation("BOSS GetBuyerHistory FirestoreService buyerId {BuyerId} count {Count} - Fresh if 0 for new UID, Restore if >0 for login", buyerId, snap.Count);
            var list = new List<Dictionary<string, object>>();
            foreach (var doc in snap.Documents) { var dict = doc.ToDictionary(); dict["id"] = doc.Id; list.Add(dict); }
            return list;
        }, new List<Dictionary<string, object>>(), $"GetBuyerHistory {buyerId}");
    }
}