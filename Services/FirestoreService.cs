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
                throw new InvalidOperationException("Firestore is not initialized");
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
                    { "isActive", true }
                };
                await docRef.SetAsync(data, SetOptions.MergeAll);
                return true;
            }, false, $"SaveUser {user.Email}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save user to Firestore: {Email}", user.Email);
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
}