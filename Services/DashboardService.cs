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
                if (data.TryGetValue(key, out var v) && v != null)
                {
                    if (v is System.Text.Json.JsonElement je)
                    {
                        if (je.ValueKind == System.Text.Json.JsonValueKind.String) return je.GetString()?.Trim() ?? "";
                        if (je.ValueKind == System.Text.Json.JsonValueKind.Number) return je.GetRawText();
                        return je.ToString().Trim().Trim('"');
                    }
                    return v.ToString()?.Trim().Trim('"') ?? "";
                }

                foreach (var kv in data)
                {
                    if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    {
                        var val = kv.Value;
                        if (val is System.Text.Json.JsonElement je2)
                        {
                            if (je2.ValueKind == System.Text.Json.JsonValueKind.String) return je2.GetString()?.Trim() ?? "";
                            if (je2.ValueKind == System.Text.Json.JsonValueKind.Number) return je2.GetRawText();
                            return je2.ToString().Trim().Trim('"');
                        }
                        return val.ToString()?.Trim().Trim('"') ?? "";
                    }
                }
            }
            catch { }
            return "";
        }

        private static object ConvertValue(object value) => value is Timestamp t ? t.ToDateTime() : value;

        private static Dictionary<string, object> SanitizeDoc(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            try { foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value); } catch { }
            clean["id"] = docId;
            clean["deviceId"] = docId;
            clean["productId"] = docId;
            if (!clean.ContainsKey("productName") && clean.TryGetValue("name", out var n)) clean["productName"] = n;
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;
            return clean;
        }

        private static Dictionary<string, object> SanitizeInterest(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            try { foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value); } catch { }
            clean["id"] = docId;
            clean["interestId"] = docId;
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

        private static bool IsBadId(string? id) =>
            string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" ||
            id.Length < 10 || id.Contains("@") || id.Contains(".com");

        private static string NormalizeStatus(string status)
        {
            var s = status?.ToLowerInvariant().Trim() ?? "pending";
            if (s.Contains("success") || s.Contains("accept") || s.Contains("approv") || s.Contains("contact"))
                return "Successful";
            if (s.Contains("fail") || s.Contains("reject") || s.Contains("cancel") || s.Contains("declin") || s.Contains("denied"))
                return "Failed";
            return "Pending";
        }

        private static string GetBuyerId(Dictionary<string, object> data)
        {
            var buyerId = GetString(data, "buyerId");
            if (string.IsNullOrWhiteSpace(buyerId)) buyerId = GetString(data, "userId");
            return buyerId;
        }

        private static object ExistingOrNow(Dictionary<string, object> data, string key, Timestamp now)
        {
            return data.TryGetValue(key, out var value) && value != null ? value : now;
        }

        private Dictionary<string, object> BuildHistoryData(
            Dictionary<string, object> source,
            string stableId,
            string status,
            string response,
            string businessName,
            Timestamp now)
        {
            var buyerId = GetBuyerId(source);
            var buyerPhone = GetString(source, "buyerPhone");
            var buyerName = GetString(source, "buyerName");
            if (string.IsNullOrWhiteSpace(buyerName)) buyerName = GetString(source, "fullName");

            var businessId = GetString(source, "businessId");
            if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(source, "ownerId");

            var productId = GetString(source, "productId");
            if (string.IsNullOrWhiteSpace(productId)) productId = GetString(source, "deviceId");

            var productName = GetString(source, "productName");
            if (string.IsNullOrWhiteSpace(productName)) productName = GetString(source, "name");

            var productImage = GetString(source, "productImage");
            if (string.IsNullOrWhiteSpace(productImage)) productImage = GetString(source, "imageBase64");
            if (string.IsNullOrWhiteSpace(productImage)) productImage = GetString(source, "imageUrl");

            var nowValue = (object)now;
            var data = new Dictionary<string, object>(source)
            {
                ["id"] = stableId,
                ["orderId"] = stableId,
                ["interestId"] = stableId,
                ["transactionId"] = stableId,
                ["buyerId"] = buyerId,
                ["userId"] = buyerId,
                ["buyerPhone"] = buyerPhone,
                ["buyerName"] = string.IsNullOrWhiteSpace(buyerName) ? "Buyer" : buyerName,
                ["businessId"] = businessId,
                ["ownerId"] = businessId,
                ["businessName"] = string.IsNullOrWhiteSpace(businessName) ? GetString(source, "businessName") : businessName,
                ["deviceId"] = productId,
                ["productId"] = productId,
                ["productName"] = string.IsNullOrWhiteSpace(productName) ? "Product" : productName,
                ["productImage"] = productImage,
                ["imageBase64"] = productImage,
                ["status"] = status,
                ["transactionStatus"] = status,
                ["response"] = response,
                ["message"] = response,
                ["businessResponse"] = response,
                ["updatedAt"] = nowValue
            };

            if (!data.ContainsKey("createdAt") || data["createdAt"] == null)
                data["createdAt"] = nowValue;

            return data;
        }

        private async Task WriteHistoryMirrorsAsync(string stableId, Dictionary<string, object> data, bool deleteInterest)
        {
            var batch = _db.StartBatch();
            batch.Set(_db.Collection("transactions").Document(stableId), data, SetOptions.MergeAll);
            batch.Set(_db.Collection("buyer_history").Document(stableId), data, SetOptions.MergeAll);
            batch.Set(_db.Collection("buyer_transactions").Document(stableId), data, SetOptions.MergeAll);
            if (deleteInterest)
                batch.Delete(_db.Collection("interests").Document(stableId));
            await batch.CommitAsync();
        }

        public async Task<string> CreateBuyerHistoryAsync(
            string buyerId, string buyerPhone, string buyerName, string businessId,
            string businessName, string productId, string productName, string productImage,
            string message, string status, string? stableId = null)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                if (IsBadId(buyerId) && string.IsNullOrWhiteSpace(buyerPhone))
                    throw new ArgumentException($"Invalid buyerId {buyerId}");

                var docId = !string.IsNullOrWhiteSpace(stableId)
                    ? stableId.Trim()
                    : Guid.NewGuid().ToString("N");

                var now = Timestamp.GetCurrentTimestamp();
                var normStatus = NormalizeStatus(status);
                var data = new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["orderId"] = docId,
                    ["interestId"] = docId,
                    ["transactionId"] = docId,
                    ["buyerId"] = buyerId ?? "",
                    ["userId"] = buyerId ?? "",
                    ["buyerPhone"] = buyerPhone ?? "",
                    ["buyerName"] = buyerName ?? "Buyer",
                    ["businessId"] = businessId ?? "",
                    ["ownerId"] = businessId ?? "",
                    ["businessName"] = businessName ?? "Irham Trade",
                    ["deviceId"] = productId ?? "",
                    ["productId"] = productId ?? "",
                    ["productName"] = productName ?? "Product",
                    ["productImage"] = productImage ?? "",
                    ["imageBase64"] = productImage ?? "",
                    ["message"] = message ?? "",
                    ["response"] = message ?? "",
                    ["businessResponse"] = message ?? "",
                    ["status"] = normStatus,
                    ["transactionStatus"] = normStatus,
                    ["read"] = false,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };

                await WriteHistoryMirrorsAsync(docId, data, false);
                _logger.LogInformation("Buyer history saved with stable ID {DocId}, buyer {BuyerId}, status {Status}", docId, buyerId, normStatus);
                return docId;
            }, "", $"CreateBuyerHistory {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerHistoryAsync(string buyerId)
        {
            if (IsBadId(buyerId)) return new List<Dictionary<string, object>>();

            return await ExecuteWithRetryAsync(async () =>
            {
                var all = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                var buyerSnap = await _db.Collection("buyer_history")
                    .WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                foreach (var doc in buyerSnap.Documents)
                    all[doc.Id] = SanitizeDoc(doc.ToDictionary(), doc.Id);

                var userSnap = await _db.Collection("buyer_history")
                    .WhereEqualTo("userId", buyerId).GetSnapshotAsync();
                foreach (var doc in userSnap.Documents)
                    all[doc.Id] = SanitizeDoc(doc.ToDictionary(), doc.Id);

                return all.Values
                    .OrderByDescending(x => x.TryGetValue("createdAt", out var created) ? created?.ToString() : "")
                    .ToList();
            }, new List<Dictionary<string, object>>(), $"GetBuyerHistory {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerHistoryByPhoneAsync(string buyerPhone, string? buyerId = null)
        {
            if (!string.IsNullOrWhiteSpace(buyerId) && !IsBadId(buyerId))
                return await GetBuyerHistoryAsync(buyerId);

            if (string.IsNullOrWhiteSpace(buyerPhone)) return new List<Dictionary<string, object>>();

            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("buyer_history")
                    .WhereEqualTo("buyerPhone", buyerPhone).GetSnapshotAsync();
                return snap.Documents
                    .Select(d => SanitizeDoc(d.ToDictionary(), d.Id))
                    .OrderByDescending(x => x.TryGetValue("createdAt", out var created) ? created?.ToString() : "")
                    .Take(50)
                    .ToList();
            }, new List<Dictionary<string, object>>(), $"GetBuyerHistoryByPhone {buyerPhone}");
        }

        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync() =>
            await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").Limit(200).GetSnapshotAsync();
                return snap.Documents.Select(doc => SanitizeDoc(doc.ToDictionary(), doc.Id)).ToList();
            }, new List<Dictionary<string, object>>(), "GetAllDevices");

        public async Task<List<Dictionary<string, object>>> GetDevicesByBusinessAsync(string businessId)
        {
            if (IsBadId(businessId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                return snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<int> GetBusinessDeviceCountAsync(string businessId)
        {
            if (IsBadId(businessId)) return 0;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
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
                if (IsBadId(businessId)) throw new ArgumentException($"Invalid businessId: {businessId}");

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
                    ["businessId"] = businessId,
                    ["ownerId"] = businessId,
                    ["businessName"] = businessName,
                    ["description"] = description,
                    ["status"] = "active",
                    ["isAvailable"] = true,
                    ["isReserved"] = false,
                    ["isSold"] = false,
                    ["imageBase64"] = imageBase64,
                    ["imageUrl"] = imageBase64,
                    ["location"] = location,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };
                await _db.Collection("devices").Document(docId).SetAsync(firestoreData);
                return docId;
            }, null!, "CreateDevice");
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, Dictionary<string, object> data) =>
            await ExecuteWithRetryAsync(async () =>
            {
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("devices").Document(deviceId).SetAsync(data, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateDevice {deviceId}");

        public async Task<bool> DeleteDeviceAsync(string deviceId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                return true;
            }, false, $"DeleteDevice {deviceId}");

        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            try
            {
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var buyerId = GetString(data, "buyerId");
                if (string.IsNullOrWhiteSpace(buyerId)) buyerId = GetString(data, "userId");
                var productId = GetString(data, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(data, "deviceId");

                string fetchedBusinessId = "";
                string realImage = GetString(data, "productImage");
                if (string.IsNullOrWhiteSpace(realImage)) realImage = GetString(data, "imageBase64");

                if (!string.IsNullOrWhiteSpace(productId))
                {
                    try
                    {
                        var deviceSnap = await _db.Collection("devices").Document(productId).GetSnapshotAsync();
                        if (deviceSnap.Exists)
                        {
                            var deviceData = deviceSnap.ToDictionary();
                            fetchedBusinessId = GetString(deviceData, "businessId");
                            if (string.IsNullOrWhiteSpace(fetchedBusinessId)) fetchedBusinessId = GetString(deviceData, "ownerId");
                            var img1 = GetString(deviceData, "imageBase64");
                            var img2 = GetString(deviceData, "imageUrl");
                            if (!string.IsNullOrWhiteSpace(img1) && img1.Length > 20) realImage = img1;
                            else if (!string.IsNullOrWhiteSpace(img2) && img2.Length > 20) realImage = img2;
                        }
                    }
                    catch { }
                }

                if (IsBadId(businessId) && !IsBadId(fetchedBusinessId)) businessId = fetchedBusinessId;
                if (IsBadId(businessId)) throw new ArgumentException($"Invalid businessId '{businessId}'");
                if (IsBadId(buyerId)) throw new ArgumentException($"Invalid buyerId '{buyerId}'");

                var buyerName = GetString(data, "buyerName");
                var buyerPhone = GetString(data, "buyerPhone");
                var buyerLocation = GetString(data, "buyerLocation");
                var buyerEmail = GetString(data, "buyerEmail");
                var businessName = GetString(data, "businessName");
                var productName = GetString(data, "productName");
                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                var now = Timestamp.GetCurrentTimestamp();
                var cleanData = new Dictionary<string, object>
                {
                    ["deviceId"] = productId,
                    ["productId"] = productId,
                    ["businessId"] = businessId,
                    ["ownerId"] = businessId,
                    ["buyerId"] = buyerId,
                    ["userId"] = buyerId,
                    ["buyerName"] = buyerName,
                    ["buyerPhone"] = buyerPhone,
                    ["buyerLocation"] = buyerLocation,
                    ["buyerEmail"] = buyerEmail,
                    ["businessName"] = businessName,
                    ["productName"] = productName,
                    ["brand"] = brand,
                    ["price"] = price,
                    ["productImage"] = realImage,
                    ["imageBase64"] = realImage,
                    ["status"] = "Pending",
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };

                var doc = await _db.Collection("interests").AddAsync(cleanData);
                await _db.Collection("interests").Document(doc.Id)
                    .SetAsync(new Dictionary<string, object> { ["id"] = doc.Id, ["interestId"] = doc.Id }, SetOptions.MergeAll);
                return doc.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateInterest FAILED");
                throw;
            }
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (IsBadId(buyerId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("interests").WhereEqualTo("userId", buyerId).GetSnapshotAsync();
                return snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList();
            }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (IsBadId(businessId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("interests")
                    .WhereEqualTo("businessId", businessId).WhereEqualTo("status", "Pending").GetSnapshotAsync();
                if (snap.Count == 0)
                {
                    var allSnap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                    var filtered = allSnap.Documents.Where(d =>
                    {
                        try
                        {
                            var dict = d.ToDictionary();
                            var st = GetString(dict, "status");
                            return string.IsNullOrWhiteSpace(st) || st.Equals("Pending", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { return true; }
                    }).ToList();
                    return filtered.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList();
                }
                return snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList();
            }, new List<Dictionary<string, object>>(), $"GetBusinessInterests {businessId}");
        }

        public async Task<bool> UpdateInterestStatusAsync(
            string interestId, string status, string? businessName = null, string? businessResponse = null)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var norm = NormalizeStatus(status);
                var update = new Dictionary<string, object>
                {
                    ["status"] = norm,
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                };
                if (!string.IsNullOrWhiteSpace(businessResponse))
                {
                    update["businessResponse"] = businessResponse;
                    update["response"] = businessResponse;
                    update["message"] = businessResponse;
                }
                if (!string.IsNullOrWhiteSpace(businessName)) update["businessName"] = businessName;
                await _db.Collection("interests").Document(interestId).SetAsync(update, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateInterest {interestId}");
        }

        public async Task<bool> DeleteInterestAsync(string interestId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("interests").Document(interestId).DeleteAsync();
                return true;
            }, false, $"DeleteInterest {interestId}");

        public async Task<string> AcceptOrderToTransactionAsync(string interestId, string businessName)
        {
            if (string.IsNullOrWhiteSpace(interestId)) return "";

            return await ExecuteWithRetryAsync(async () =>
            {
                var transactionRef = _db.Collection("transactions").Document(interestId);
                var existingTransaction = await transactionRef.GetSnapshotAsync();

                var interestRef = _db.Collection("interests").Document(interestId);
                var snap = await interestRef.GetSnapshotAsync();

                if (!snap.Exists)
                {
                    if (existingTransaction.Exists)
                        return interestId;

                    var existingHistory = await _db.Collection("buyer_history").Document(interestId).GetSnapshotAsync();
                    if (existingHistory.Exists &&
                        NormalizeStatus(GetString(existingHistory.ToDictionary(), "status")) == "Successful")
                        return interestId;

                    throw new InvalidOperationException($"Interest {interestId} not found");
                }

                var source = snap.ToDictionary();
                if (existingTransaction.Exists &&
                    NormalizeStatus(GetString(existingTransaction.ToDictionary(), "status")) == "Successful")
                {
                    await interestRef.DeleteAsync();
                    return interestId;
                }

                var buyerId = GetBuyerId(source);
                var buyerPhone = GetString(source, "buyerPhone");
                var productId = GetString(source, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(source, "deviceId");
                var productImage = GetString(source, "productImage");
                if (string.IsNullOrWhiteSpace(productImage)) productImage = GetString(source, "imageBase64");

                if (!string.IsNullOrWhiteSpace(productId))
                {
                    try
                    {
                        var productSnap = await _db.Collection("devices").Document(productId).GetSnapshotAsync();
                        if (productSnap.Exists)
                        {
                            var productData = productSnap.ToDictionary();
                            var img = GetString(productData, "imageBase64");
                            if (string.IsNullOrWhiteSpace(img)) img = GetString(productData, "imageUrl");
                            if (!string.IsNullOrWhiteSpace(img)) productImage = img;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not load product image for {ProductId}", productId);
                    }
                }

                var now = Timestamp.GetCurrentTimestamp();
                var displayBusinessName = string.IsNullOrWhiteSpace(businessName)
                    ? GetString(source, "businessName")
                    : businessName.Trim();
                var response = $"{(string.IsNullOrWhiteSpace(displayBusinessName) ? "The business" : displayBusinessName)} accepted your order. We will get in touch with you soon. Your order has been accepted.";

                source["productImage"] = productImage;
                source["imageBase64"] = productImage;
                source["buyerId"] = buyerId;
                source["userId"] = buyerId;
                source["buyerPhone"] = buyerPhone;
                source["productId"] = productId;
                source["deviceId"] = productId;

                var historyData = BuildHistoryData(source, interestId, "Successful", response, displayBusinessName, now);
                historyData["acceptedAt"] = now;

                // Save all three views under the original interest ID, then remove the pending interest atomically.
                await WriteHistoryMirrorsAsync(interestId, historyData, true);

                if (!string.IsNullOrWhiteSpace(productId))
                {
                    try
                    {
                        await _db.Collection("devices").Document(productId).SetAsync(
                            new Dictionary<string, object>
                            {
                                ["status"] = "Sold",
                                ["isAvailable"] = false,
                                ["isReserved"] = false,
                                ["isSold"] = true,
                                ["transactionId"] = interestId,
                                ["updatedAt"] = now
                            }, SetOptions.MergeAll);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Order {InterestId} saved but product {ProductId} could not be marked sold", interestId, productId);
                    }
                }

                _logger.LogInformation("Accepted order {InterestId}; history mirrors use the same document ID", interestId);
                return interestId;
            }, "", $"Accept {interestId}");
        }

        public async Task<bool> DeclineOrderAsync(string interestId, string businessName)
        {
            if (string.IsNullOrWhiteSpace(interestId)) return false;

            return await ExecuteWithRetryAsync(async () =>
            {
                var interestRef = _db.Collection("interests").Document(interestId);
                var snap = await interestRef.GetSnapshotAsync();

                if (!snap.Exists)
                {
                    var historySnap = await _db.Collection("buyer_history").Document(interestId).GetSnapshotAsync();
                    if (historySnap.Exists)
                    {
                        var savedStatus = NormalizeStatus(GetString(historySnap.ToDictionary(), "status"));
                        return savedStatus == "Failed";
                    }
                    return false;
                }

                var source = snap.ToDictionary();
                var now = Timestamp.GetCurrentTimestamp();
                var displayBusinessName = string.IsNullOrWhiteSpace(businessName)
                    ? GetString(source, "businessName")
                    : businessName.Trim();
                var response = $"{(string.IsNullOrWhiteSpace(displayBusinessName) ? "The business" : displayBusinessName)} declined your order. The product is available again.";

                var productId = GetString(source, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(source, "deviceId");

                var historyData = BuildHistoryData(source, interestId, "Failed", response, displayBusinessName, now);
                historyData["declinedAt"] = now;

                // Save the final buyer history before removing the pending interest.
                await WriteHistoryMirrorsAsync(interestId, historyData, true);

                if (!string.IsNullOrWhiteSpace(productId))
                {
                    try
                    {
                        await _db.Collection("devices").Document(productId).SetAsync(
                            new Dictionary<string, object>
                            {
                                ["status"] = "Available",
                                ["isAvailable"] = true,
                                ["isReserved"] = false,
                                ["isSold"] = false,
                                ["updatedAt"] = now
                            }, SetOptions.MergeAll);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Decline history saved but product {ProductId} could not be released", productId);
                    }
                }

                _logger.LogInformation("Declined order {InterestId}; history mirrors use the same document ID", interestId);
                return true;
            }, false, $"Decline {interestId}");
        }

        public async Task<List<Dictionary<string, object>>> GetTransactionsByBusinessAsync(string businessId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("transactions").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                return snap.Documents.Select(d => SanitizeTransaction(d.ToDictionary(), d.Id)).ToList();
            }, new List<Dictionary<string, object>>(), $"GetTrans {businessId}");

        public async Task<bool> CompleteTransactionAsync(string transactionId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("transactions").Document(transactionId).DeleteAsync();
                return true;
            }, false, $"Complete {transactionId}");

        public async Task<bool> IncompleteTransactionAsync(string transactionId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("transactions").Document(transactionId).GetSnapshotAsync();
                if (!snap.Exists) return false;

                var data = snap.ToDictionary();
                var productName = GetString(data, "productName");
                if (string.IsNullOrWhiteSpace(productName)) productName = GetString(data, "name");
                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                var description = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(description)) description = productName;
                var image = GetString(data, "productImage");
                if (string.IsNullOrWhiteSpace(image)) image = GetString(data, "imageBase64");
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var businessName = GetString(data, "businessName");

                var newId = Guid.NewGuid().ToString("N");
                var now = Timestamp.GetCurrentTimestamp();
                var newDevice = new Dictionary<string, object>
                {
                    ["id"] = newId,
                    ["deviceId"] = newId,
                    ["productId"] = newId,
                    ["name"] = productName,
                    ["productName"] = productName,
                    ["brand"] = brand,
                    ["price"] = price,
                    ["description"] = description,
                    ["imageBase64"] = image,
                    ["imageUrl"] = image,
                    ["businessId"] = businessId,
                    ["ownerId"] = businessId,
                    ["businessName"] = businessName,
                    ["status"] = "active",
                    ["isAvailable"] = true,
                    ["isReserved"] = false,
                    ["isSold"] = false,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };
                await _db.Collection("devices").Document(newId).SetAsync(newDevice);
                await _db.Collection("transactions").Document(transactionId).DeleteAsync();
                return true;
            }, false, $"Incomplete {transactionId}");

        public async Task<Dictionary<string, object>?> GetBuyerProfileAsync(string buyerId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("users").Document(buyerId).GetSnapshotAsync();
                return snap.Exists ? SanitizeDoc(snap.ToDictionary(), snap.Id) : null;
            }, null, $"GetBuyer {buyerId}");

        public async Task<bool> UpdateBuyerProfileAsync(string buyerId, Dictionary<string, object> data) =>
            await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("users").Document(buyerId).SetAsync(data, SetOptions.MergeAll);
                return true;
            }, false, $"UpdateBuyer {buyerId}");

        public async Task<Dictionary<string, object>?> GetBusinessAsync(string businessId) =>
            await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("users").Document(businessId).GetSnapshotAsync();
                return snap.Exists ? SanitizeDoc(snap.ToDictionary(), snap.Id) : null;
            }, null, $"GetBusiness {businessId}");
    }
}
