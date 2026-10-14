using Google.Cloud.Firestore;

namespace IrhamTrade.Server.Services
{
    public class DashboardService
    {
        private readonly FirestoreDb _db;
        private readonly ILogger<DashboardService> _logger;

        public DashboardService(FirebaseService firebaseService, ILogger<DashboardService> logger)
        {
            _db = firebaseService.FirestoreDb ?? throw new ArgumentNullException(nameof(firebaseService.FirestoreDb));
            _logger = logger;
            _logger.LogInformation("DashboardService initialized Boss Project: {ProjectId}", _db.ProjectId);
        }

       
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string opName, int retries = 3)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                    var task = action();
                    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (completed == task) return await task;
                    throw new TimeoutException($"BOSS {opName} timeout 70s Boss");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("BOSS {Op} attempt {Attempt} failed Boss: {Msg} Boss", opName, attempt, ex.Message);
                    if (attempt == retries) return fallback;
                    await Task.Delay(attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000);
                }
            }
            return fallback;
        }

        // === DEVICES BOSS - PROFESSIONAL ZERO-CRASH - KEPT 100% + SEALED ===
        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("devices").OrderByDescending("createdAt").Limit(500).GetSnapshotAsync();
                    return snap.Documents.Select(doc => {
                        var dict = doc.ToDictionary();
                        dict["id"] = doc.Id;
                        dict["deviceId"] = doc.Id;
                        return dict;
                    }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GetAllDevices FAIL Boss: {Message} - Trying fallback Boss", ex.Message);
                    try
                    {
                        var snap = await _db.Collection("devices").Limit(500).GetSnapshotAsync();
                        return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["deviceId"] = d.Id; return dict; }).ToList();
                    }
                    catch { return new List<Dictionary<string, object>>(); }
                }
            }, new List<Dictionary<string, object>>(), "GetAllDevices");
        }

        public async Task<List<Dictionary<string, object>>> GetDevicesByBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).OrderByDescending("createdAt").GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["deviceId"] = d.Id; return dict; }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetDevicesByBusiness ordered query failed Boss, trying unordered Boss for {BusinessId}", businessId);
                    try
                    {
                        var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                        return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; return dict; }).ToList();
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "GetDevicesByBusiness FAIL Boss");
                        return new List<Dictionary<string, object>>();
                    }
                }
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                // ===== IMAGE FIX Boss - 15 lines professional - KEPT 100% =====
                if (data.TryGetValue("imageBase64", out var imgObj) && imgObj is string imgStr && imgStr.Length > 100)
                {
                    data["imageUrl"] = imgStr;
                    data["image"] = imgStr;
                    data["hasImage"] = true;
                }
                else if (data.TryGetValue("image", out var imgObj2) && imgObj2 is string imgStr2 && imgStr2.Length > 100)
                {
                    data["imageUrl"] = imgStr2;
                    data["hasImage"] = true;
                }

                data["createdAt"] = Timestamp.GetCurrentTimestamp();
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();

                if (!data.TryGetValue("businessId", out var bid) || string.IsNullOrWhiteSpace(bid?.ToString()))
                    data["businessId"] = "unknown_boss";

                data["business_id"] = data["businessId"];
                if (!data.ContainsKey("status")) data["status"] = "active";

                var doc = await _db.Collection("devices").AddAsync(data);
                _logger.LogInformation("CreateDevice Boss {DeviceId} Business {BusinessId} Image:{HasImage}", doc.Id, data["businessId"], data.ContainsKey("hasImage"));
                return doc.Id;
            }, null!, "CreateDevice");
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || data == null) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                if (data.TryGetValue("imageBase64", out var imgObj) && imgObj is string imgStr && imgStr.Length > 100)
                {
                    data["imageUrl"] = imgStr;
                    data["image"] = imgStr;
                    data["hasImage"] = true;
                }
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("devices").Document(deviceId).SetAsync(data, SetOptions.MergeAll);
                _logger.LogInformation("UpdateDevice Boss {DeviceId}", deviceId);
                return true;
            }, false, $"UpdateDevice {deviceId}");
        }

        public async Task<bool> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                _logger.LogInformation("DeleteDevice Boss {DeviceId}", deviceId);
                return true;
            }, false, $"DeleteDevice {deviceId}");
        }

        // === INTERESTS BOSS - KEPT 100% + SEALED ===
        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                data["createdAt"] = Timestamp.GetCurrentTimestamp();
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                if (!data.ContainsKey("status")) data["status"] = "Pending";
                if (!data.ContainsKey("date")) data["date"] = Timestamp.GetCurrentTimestamp();
                var doc = await _db.Collection("interests").AddAsync(data);
                _logger.LogInformation("CreateInterest Boss {Id} Business {BusinessId}", doc.Id, data.TryGetValue("businessId", out var b) ? b : "unknown");
                return doc.Id;
            }, null!, "CreateInterest");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).OrderByDescending("createdAt").GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["interestId"] = d.Id; return dict; }).ToList();
                }
                catch
                {
                    try
                    {
                        var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                        return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["interestId"] = d.Id; return dict; }).ToList();
                    }
                    catch { return new List<Dictionary<string, object>>(); }
                }
            }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).OrderByDescending("createdAt").GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["interestId"] = d.Id; return dict; }).ToList();
                }
                catch
                {
                    try
                    {
                        var snap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                        return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["interestId"] = d.Id; return dict; }).ToList();
                    }
                    catch { return new List<Dictionary<string, object>>(); }
                }
            }, new List<Dictionary<string, object>>(), $"GetBusinessInterests {businessId}");
        }

        public async Task<bool> UpdateInterestStatusAsync(string interestId, string status)
        {
            if (string.IsNullOrWhiteSpace(interestId) || string.IsNullOrWhiteSpace(status)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                var docRef = _db.Collection("interests").Document(interestId);
                var snap = await docRef.GetSnapshotAsync();
                if (snap.Exists)
                {
                    await docRef.SetAsync(new Dictionary<string, object> { ["status"] = status, ["updatedAt"] = Timestamp.GetCurrentTimestamp() }, SetOptions.MergeAll);
                }
                else
                {
                    var q = await _db.Collection("interests").WhereEqualTo("orderId", interestId).Limit(1).GetSnapshotAsync();
                    if (q.Count == 0) q = await _db.Collection("interests").WhereEqualTo("OrderId", interestId).Limit(1).GetSnapshotAsync();
                    if (q.Count > 0)
                        await q.Documents[0].Reference.SetAsync(new Dictionary<string, object> { ["status"] = status, ["updatedAt"] = Timestamp.GetCurrentTimestamp() }, SetOptions.MergeAll);
                    else return false;
                }
                _logger.LogInformation("UpdateInterest Boss {Id} -> {Status}", interestId, status);
                return true;
            }, false, $"UpdateInterest {interestId}");
        }

        // === BUYERS + BUSINESSES PROFILE BOSS - KEPT 100% + SEALED ===
        public async Task<Dictionary<string, object>?> GetBuyerProfileAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return null;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("users").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) snap = await _db.Collection("buyers").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) return null;
                var dict = snap.ToDictionary(); dict["id"] = snap.Id; return dict;
            }, null, $"GetBuyerProfile {buyerId}");
        }

        public async Task<bool> UpdateBuyerProfileAsync(string buyerId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(buyerId) || data == null) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("users").Document(buyerId).SetAsync(data, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateBuyerProfile {buyerId}");
        }

        public async Task<Dictionary<string, object>?> GetBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return null;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("users").Document(businessId).GetSnapshotAsync();
                if (!snap.Exists) snap = await _db.Collection("businesses").Document(businessId).GetSnapshotAsync();
                if (!snap.Exists)
                {
                    var q = await _db.Collection("users").WhereEqualTo("businessId", businessId).Limit(1).GetSnapshotAsync();
                    if (q.Count > 0) snap = q.Documents[0];
                }
                if (!snap.Exists) return null;
                var dict = snap.ToDictionary();
                dict["id"] = snap.Id;
                dict["businessId"] = snap.Id;
                if (dict.TryGetValue("businessName", out var bn) && bn != null)
                    dict["BusinessName"] = bn;
                else if (!dict.ContainsKey("BusinessName"))
                    dict["BusinessName"] = "IRHAM TRADE";
                return dict;
            }, null, $"GetBusiness {businessId}");
        }

        public async Task<List<Dictionary<string, object>>> GetAllBusinessesWithDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var bizSnap = await _db.Collection("users").WhereEqualTo("role", "business").Limit(100).GetSnapshotAsync();
                if (bizSnap.Count == 0) bizSnap = await _db.Collection("businesses").Limit(100).GetSnapshotAsync();

                var tasks = bizSnap.Documents.Select(async bizDoc =>
                {
                    var bizDict = bizDoc.ToDictionary();
                    bizDict["businessId"] = bizDoc.Id;
                    bizDict["id"] = bizDoc.Id;
                    try
                    {
                        var devSnap = await _db.Collection("devices").WhereEqualTo("businessId", bizDoc.Id).Limit(50).GetSnapshotAsync();
                        var devices = devSnap.Documents.Select(d => { var dd = d.ToDictionary(); dd["id"] = d.Id; return dd; }).ToList();
                        bizDict["devices"] = devices;
                    }
                    catch { bizDict["devices"] = new List<Dictionary<string, object>>(); }
                    return bizDict;
                });

                var result = await Task.WhenAll(tasks);
                return result.ToList();
            }, new List<Dictionary<string, object>>(), "GetAllBusinessesWithDevices");
        }
    }
}