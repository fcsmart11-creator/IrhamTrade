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
            _logger.LogInformation("DashboardService initialized Project: {ProjectId}", _db.ProjectId);
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
                    throw new TimeoutException($"{opName} timeout 70s");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("{Op} attempt {Attempt} failed: {Msg}", opName, attempt, ex.Message);
                    if (attempt == retries) return fallback;
                    await Task.Delay(attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000);
                }
            }
            return fallback;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var v) && v != null) return v.ToString()?.Trim() ?? "";
            // case-insensitive
            foreach (var kv in data)
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    return kv.Value.ToString()?.Trim() ?? "";
            return "";
        }

        // ================= DEVICES =================
        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("devices").OrderByDescending("createdAt").Limit(500).GetSnapshotAsync();
                    return snap.Documents.Select(doc => { var d = doc.ToDictionary(); d["id"] = doc.Id; d["deviceId"] = doc.Id; return d; }).ToList();
                }
                catch
                {
                    var snap = await _db.Collection("devices").Limit(500).GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["deviceId"] = d.Id; return dict; }).ToList();
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
                catch
                {
                    var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; return dict; }).ToList();
                }
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                // Strong one-to-one mapping with APK UploadPage
                var name = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "model");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Device name is required");

                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                var contactNumber = GetString(data, "contactNumber");
                if (string.IsNullOrWhiteSpace(contactNumber)) contactNumber = GetString(data, "contact");
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "business_id");
                var businessName = GetString(data, "businessName");
                var description = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(description)) description = name;
                var imageBase64 = GetString(data, "imageBase64");
                if (string.IsNullOrWhiteSpace(imageBase64)) imageBase64 = GetString(data, "image");
                if (string.IsNullOrWhiteSpace(imageBase64)) imageBase64 = GetString(data, "imageUrl");

                var docId = GetString(data, "id");
                if (string.IsNullOrWhiteSpace(docId)) docId = Guid.NewGuid().ToString("N");

                var firestoreData = new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["Id"] = docId,
                    ["name"] = name,
                    ["model"] = name,
                    ["brand"] = brand,
                    ["price"] = price,
                    ["contactNumber"] = contactNumber,
                    ["contact"] = contactNumber,
                    ["businessId"] = businessId,
                    ["businessName"] = businessName,
                    ["description"] = description,
                    ["status"] = "active",
                    ["imageBase64"] = imageBase64,
                    ["image"] = imageBase64,
                    ["imageUrl"] = imageBase64,
                    ["hasImage"] = !string.IsNullOrWhiteSpace(imageBase64),
                    ["createdAt"] = Timestamp.GetCurrentTimestamp(),
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                };

                await _db.Collection("devices").Document(docId).SetAsync(firestoreData);
                _logger.LogInformation("Device created {DeviceId} Brand {Brand} Business {BusinessId}", docId, brand, businessId);
                return docId;
            }, null!, "CreateDevice");
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || data == null) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                var update = new Dictionary<string, object>();
                foreach (var kv in data)
                {
                    if (kv.Key.Equals("imageBase64", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("image", StringComparison.OrdinalIgnoreCase))
                    {
                        var img = kv.Value?.ToString() ?? "";
                        if (img.Length > 100)
                        {
                            update["imageBase64"] = img;
                            update["imageUrl"] = img;
                            update["image"] = img;
                            update["hasImage"] = true;
                        }
                    }
                    else
                    {
                        update[kv.Key] = kv.Value;
                    }
                }
                update["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("devices").Document(deviceId).SetAsync(update, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateDevice {deviceId}");
        }

        public async Task<bool> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                return true;
            }, false, $"DeleteDevice {deviceId}");
        }

        // ================= INTERESTS =================
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
                    var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; return dict; }).ToList();
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
                    var snap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; return dict; }).ToList();
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
                    return true;
                }
                var q = await _db.Collection("interests").WhereEqualTo("orderId", interestId).Limit(1).GetSnapshotAsync();
                if (q.Count > 0)
                {
                    await q.Documents[0].Reference.SetAsync(new Dictionary<string, object> { ["status"] = status, ["updatedAt"] = Timestamp.GetCurrentTimestamp() }, SetOptions.MergeAll);
                    return true;
                }
                return false;
            }, false, $"UpdateInterest {interestId}");
        }

        // ================= PROFILES =================
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
                        bizDict["devices"] = devSnap.Documents.Select(d => { var dd = d.ToDictionary(); dd["id"] = d.Id; return dd; }).ToList();
                    }
                    catch { bizDict["devices"] = new List<Dictionary<string, object>>(); }
                    return bizDict;
                });
                return (await Task.WhenAll(tasks)).ToList();
            }, new List<Dictionary<string, object>>(), "GetAllBusinessesWithDevices");
        }
    }
}