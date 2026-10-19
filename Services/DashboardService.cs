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
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string opName, int retries = 3)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var task = action();
                    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (completed == task) return await task;
                    throw new TimeoutException($"{opName} timeout 30s");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("{Op} attempt {Attempt} failed: {Msg}", opName, attempt, ex.Message);
                    if (attempt == retries) return fallback;
                    await Task.Delay(attempt == 1 ? 1500 : attempt == 2 ? 3000 : 6000);
                }
            }
            return fallback;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            try
            {
                if (data.TryGetValue(key, out var v) && v != null) return v.ToString()?.Trim() ?? "";
                foreach (var kv in data)
                    if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                        return kv.Value.ToString()?.Trim() ?? "";
            }
            catch { }
            return "";
        }

        private static object ConvertValue(object value)
        {
            if (value is Timestamp timestamp) return timestamp.ToDateTime();
            return value;
        }

        private static Dictionary<string, object> SanitizeDoc(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            try { foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value); } catch { }
            clean["id"] = docId;
            clean["deviceId"] = docId;
            clean["productId"] = docId;
            if (!clean.ContainsKey("productName") && clean.TryGetValue("name", out var n)) clean["productName"] = n;
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;
            if (!clean.ContainsKey("businessId") && clean.TryGetValue("ownerId", out var oid)) clean["businessId"] = oid;
            if (!clean.ContainsKey("ownerId") && clean.TryGetValue("businessId", out var bid)) clean["ownerId"] = bid;
            return clean;
        }

        private static Dictionary<string, object> SanitizeInterest(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            try { foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value); } catch { }
            clean["id"] = docId;
            clean["interestId"] = docId;
            if (!clean.ContainsKey("businessId") && clean.TryGetValue("ownerId", out var obid)) clean["businessId"] = obid;
            if (!clean.ContainsKey("ownerId") && clean.TryGetValue("businessId", out var bbid)) clean["ownerId"] = bbid;
            return clean;
        }

        private static Dictionary<string, object> SanitizeTransaction(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            try { foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value); } catch { }
            clean["id"] = docId;
            clean["transactionId"] = docId;
            return clean;
        }

        private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").Limit(200).GetSnapshotAsync();
                return snap.Documents.Select(doc => SanitizeDoc(doc.ToDictionary(), doc.Id)).ToList();
            }, new List<Dictionary<string, object>>(), "GetAllDevices");
        }

        public async Task<List<Dictionary<string, object>>> GetDevicesByBusinessAsync(string businessId)
        {
            if (IsBadId(businessId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0) snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                return snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<int> GetBusinessDeviceCountAsync(string businessId)
        {
            if (IsBadId(businessId)) return 0;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0) snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                return snap.Count;
            }, 0, $"GetCount {businessId}");
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");

                // BOSS FIX: BLOCK FAKE ID "1" PERMANENTLY!
                if (IsBadId(businessId)) throw new ArgumentException($"Invalid businessId: {businessId}. Please logout and login again to get real business ID.");

                var name = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "productName");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Device name is required");

                var count = await GetBusinessDeviceCountAsync(businessId);
                if (count >= 20) throw new InvalidOperationException("Free limit reached: 20 items max.");

                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                if (string.IsNullOrWhiteSpace(price)) price = GetString(data, "productPrice");
                var contactNumber = GetString(data, "contactNumber");
                if (string.IsNullOrWhiteSpace(contactNumber)) contactNumber = GetString(data, "phone");
                var businessName = GetString(data, "businessName");
                var description = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(description)) description = name;
                var imageBase64 = GetString(data, "imageBase64");
                if (string.IsNullOrWhiteSpace(imageBase64)) imageBase64 = GetString(data, "imageUrl");
                var location = GetString(data, "location");
                var category = GetString(data, "category");
                if (string.IsNullOrWhiteSpace(category)) category = "General";
                var docId = Guid.NewGuid().ToString("N");
                var now = Timestamp.GetCurrentTimestamp();

                var firestoreData = new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["deviceId"] = docId,
                    ["productId"] = docId,
                    ["name"] = name,
                    ["productName"] = name,
                    ["brand"] = brand,
                    ["category"] = category,
                    ["price"] = price,
                    ["productPrice"] = price,
                    ["contactNumber"] = contactNumber,
                    ["businessId"] = businessId, // REAL ID NOW!
                    ["ownerId"] = businessId,
                    ["businessName"] = businessName,
                    ["description"] = description,
                    ["status"] = "active",
                    ["imageBase64"] = imageBase64,
                    ["imageUrl"] = imageBase64,
                    ["location"] = location,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };
                // BOSS FIX: SAVE ONLY TO DEVICES - NO DOUBLE DOC!
                await _db.Collection("devices").Document(docId).SetAsync(firestoreData);
                _logger.LogInformation("Device created {DeviceId} Business {BusinessId}", docId, businessId);
                return docId;
            }, null!, "CreateDevice");
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, Dictionary<string, object> data) => await ExecuteWithRetryAsync(async () => { data["updatedAt"] = Timestamp.GetCurrentTimestamp(); await _db.Collection("devices").Document(deviceId).SetAsync(data, SetOptions.MergeAll); return true; }, false, $"UpdateDevice {deviceId}");
        public async Task<bool> DeleteDeviceAsync(string deviceId) => await ExecuteWithRetryAsync(async () => { await _db.Collection("devices").Document(deviceId).DeleteAsync(); return true; }, false, $"DeleteDevice {deviceId}");

        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var businessId = GetString(data, "businessId");
                if (IsBadId(businessId)) throw new ArgumentException($"Invalid businessId blocked: {businessId}");

                data["businessId"] = businessId;
                data["ownerId"] = businessId;
                data["createdAt"] = Timestamp.GetCurrentTimestamp();
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                if (!data.ContainsKey("status")) data["status"] = "Pending";

                var doc = await _db.Collection("interests").AddAsync(data);
                return doc.Id;
            }, null!, "CreateInterest");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync(); return snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList(); }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (IsBadId(businessId)) return new();
            return await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).GetSnapshotAsync(); return snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList(); }, new List<Dictionary<string, object>>(), $"GetBusinessInterests {businessId}");
        }

        public async Task<bool> UpdateInterestStatusAsync(string interestId, string status, string? businessName = null, string? businessResponse = null)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var update = new Dictionary<string, object> { ["status"] = status, ["updatedAt"] = Timestamp.GetCurrentTimestamp() };
                if (!string.IsNullOrWhiteSpace(businessResponse)) { update["businessResponse"] = businessResponse; update["response"] = businessResponse; }
                await _db.Collection("interests").Document(interestId).SetAsync(update, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateInterest {interestId}");
        }

        public async Task<bool> DeleteInterestAsync(string interestId) => await ExecuteWithRetryAsync(async () => { await _db.Collection("interests").Document(interestId).DeleteAsync(); return true; }, false, $"DeleteInterest {interestId}");
        public async Task<string> AcceptOrderToTransactionAsync(string interestId, string businessName) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("interests").Document(interestId).GetSnapshotAsync(); var data = snap.ToDictionary(); data["transactionStatus"] = "InProgress"; var trans = await _db.Collection("transactions").AddAsync(data); await _db.Collection("devices").Document(GetString(data, "productId")).DeleteAsync(); await _db.Collection("interests").Document(interestId).SetAsync(new Dictionary<string, object> { ["status"] = "Successful" }, SetOptions.MergeAll); return trans.Id; }, null!, $"Accept {interestId}");
        public async Task<List<Dictionary<string, object>>> GetTransactionsByBusinessAsync(string businessId) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("transactions").WhereEqualTo("businessId", businessId).GetSnapshotAsync(); return snap.Documents.Select(d => SanitizeTransaction(d.ToDictionary(), d.Id)).ToList(); }, new List<Dictionary<string, object>>(), $"GetTrans {businessId}");
        public async Task<bool> CompleteTransactionAsync(string transactionId) => await ExecuteWithRetryAsync(async () => { await _db.Collection("transactions").Document(transactionId).DeleteAsync(); return true; }, false, $"Complete {transactionId}");
        public async Task<bool> IncompleteTransactionAsync(string transactionId) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("transactions").Document(transactionId).GetSnapshotAsync(); var data = snap.ToDictionary(); var newId = Guid.NewGuid().ToString("N"); data["id"] = newId; await _db.Collection("devices").Document(newId).SetAsync(data); await _db.Collection("transactions").Document(transactionId).DeleteAsync(); return true; }, false, $"Incomplete {transactionId}");
        public async Task<Dictionary<string, object>?> GetBuyerProfileAsync(string buyerId) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("users").Document(buyerId).GetSnapshotAsync(); return snap.Exists ? SanitizeDoc(snap.ToDictionary(), snap.Id) : null; }, null, $"GetBuyer {buyerId}");
        public async Task<bool> UpdateBuyerProfileAsync(string buyerId, Dictionary<string, object> data) => await ExecuteWithRetryAsync(async () => { await _db.Collection("users").Document(buyerId).SetAsync(data, SetOptions.MergeAll); return true; }, false, $"UpdateBuyer {buyerId}");
        public async Task<Dictionary<string, object>?> GetBusinessAsync(string businessId) => await ExecuteWithRetryAsync(async () => { var snap = await _db.Collection("users").Document(businessId).GetSnapshotAsync(); return snap.Exists ? SanitizeDoc(snap.ToDictionary(), snap.Id) : null; }, null, $"GetBusiness {businessId}");
    }
}