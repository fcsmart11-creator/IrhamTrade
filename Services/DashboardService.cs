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

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string opName, int retries = 2)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    var task = action();
                    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (completed == task) return await task;
                    throw new TimeoutException($"{opName} timeout 25s");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("{Op} attempt {Attempt} failed: {Msg}", opName, attempt, ex.Message);
                    if (attempt == retries) return fallback;
                    await Task.Delay(attempt == 1 ? 1500 : 3000);
                }
            }
            return fallback;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out var v) && v != null) return v.ToString()?.Trim() ?? "";
            foreach (var kv in data)
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    return kv.Value.ToString()?.Trim() ?? "";
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
            foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value);
            clean["id"] = docId;
            clean["deviceId"] = docId;
            if (!clean.ContainsKey("productName") && clean.TryGetValue("name", out var n)) clean["productName"] = n;
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;
            return clean;
        }

        private static Dictionary<string, object> SanitizeInterest(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value);
            clean["id"] = docId;
            clean["interestId"] = docId;
            clean["orderId"] = docId;

            if (!clean.ContainsKey("productName") || string.IsNullOrWhiteSpace(clean["productName"]?.ToString()))
            {
                var name = GetString(clean, "name");
                if (!string.IsNullOrWhiteSpace(name)) clean["productName"] = name;
                else if (clean.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m?.ToString())) clean["productName"] = m;
            }
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;

            var img = GetString(clean, "productImage");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "imageBase64");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "image");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "imageUrl");
            if (!string.IsNullOrWhiteSpace(img))
            {
                clean["productImage"] = img;
                clean["imageBase64"] = img;
                clean["imageUrl"] = img;
            }

            if (!clean.ContainsKey("price") && clean.TryGetValue("productPrice", out var pp)) clean["price"] = pp;
            if (!clean.ContainsKey("productPrice") && clean.TryGetValue("price", out var p)) clean["productPrice"] = p;

            if (!clean.ContainsKey("buyerName"))
            {
                var bn = GetString(clean, "fullName");
                if (!string.IsNullOrWhiteSpace(bn)) clean["buyerName"] = bn;
                else clean["buyerName"] = GetString(clean, "name");
            }

            if (!clean.ContainsKey("status") || string.IsNullOrWhiteSpace(clean["status"]?.ToString())) clean["status"] = "Pending";
            if (!clean.ContainsKey("businessResponse")) clean["businessResponse"] = "";
            if (!clean.ContainsKey("response") && clean.TryGetValue("businessResponse", out var br)) clean["response"] = br;
            if (!clean.ContainsKey("message") && clean.TryGetValue("businessResponse", out var br2)) clean["message"] = br2;

            return clean;
        }

        private static Dictionary<string, object> SanitizeTransaction(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value);
            clean["id"] = docId;
            clean["transactionId"] = docId;

            var img = GetString(clean, "productImage");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "imageBase64");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "image");
            if (string.IsNullOrWhiteSpace(img)) img = GetString(clean, "imageUrl");
            if (!string.IsNullOrWhiteSpace(img))
            {
                clean["productImage"] = img;
                clean["imageBase64"] = img;
                clean["imageUrl"] = img;
            }

            if (!clean.ContainsKey("productName") && clean.TryGetValue("name", out var n)) clean["productName"] = n;
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;
            if (!clean.ContainsKey("transactionStatus") || string.IsNullOrWhiteSpace(clean["transactionStatus"]?.ToString())) clean["transactionStatus"] = "InProgress";
            return clean;
        }

        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("devices").WhereEqualTo("status", "active").Limit(200).GetSnapshotAsync();
                    if (snap.Count == 0) snap = await _db.Collection("devices").Limit(200).GetSnapshotAsync();
                    var list = snap.Documents.Select(doc => SanitizeDoc(doc.ToDictionary(), doc.Id)).ToList();
                    list = list.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
                    return list;
                }
                catch
                {
                    var snap = await _db.Collection("devices").Limit(200).GetSnapshotAsync();
                    return snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                }
            }, new List<Dictionary<string, object>>(), "GetAllDevices");
        }

        public async Task<List<Dictionary<string, object>>> GetDevicesByBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new List<Dictionary<string, object>>();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0) snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                var list = snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                return list.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<int> GetBusinessDeviceCountAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return 0;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0) snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                return snap.Count;
            }, 0, $"GetCount {businessId}");
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var name = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "productName");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Device name required");
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                if (string.IsNullOrWhiteSpace(businessId)) throw new ArgumentException("businessId required");
                var count = await GetBusinessDeviceCountAsync(businessId);
                if (count >= 20) throw new InvalidOperationException("Free limit reached: 20 items max. Upgrade to publish more.");
                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                if (string.IsNullOrWhiteSpace(price)) price = GetString(data, "productPrice");
                var contactNumber = GetString(data, "contactNumber");
                if (string.IsNullOrWhiteSpace(contactNumber)) contactNumber = GetString(data, "contact");
                var businessName = GetString(data, "businessName");
                var description = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(description)) description = name;
                var imageBase64 = GetString(data, "imageBase64");
                if (string.IsNullOrWhiteSpace(imageBase64)) imageBase64 = GetString(data, "imageUrl");
                var location = GetString(data, "location");
                var category = GetString(data, "category");
                if (string.IsNullOrWhiteSpace(category)) category = "General";
                var docId = GetString(data, "id");
                if (string.IsNullOrWhiteSpace(docId)) docId = Guid.NewGuid().ToString("N");
                var now = Timestamp.GetCurrentTimestamp();
                var firestoreData = new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["deviceId"] = docId,
                    ["productId"] = docId,
                    ["name"] = name,
                    ["productName"] = name,
                    ["model"] = name,
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
                    ["isActive"] = true,
                    ["imageBase64"] = imageBase64,
                    ["imageUrl"] = imageBase64,
                    ["hasImage"] = !string.IsNullOrWhiteSpace(imageBase64),
                    ["location"] = location,
                    ["stock"] = 100,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };
                await _db.Collection("devices").Document(docId).SetAsync(firestoreData);
                try { await _db.Collection("products").Document(docId).SetAsync(firestoreData, SetOptions.MergeAll); } catch { }
                return docId;
            }, null!, "CreateDevice");
        }

        public async Task<bool> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                try { await _db.Collection("products").Document(deviceId).DeleteAsync(); } catch { }
                try { await _db.Collection("inventory").Document(deviceId).DeleteAsync(); } catch { }
                return true;
            }, false, $"DeleteDevice {deviceId}");
        }

        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var productId = GetString(data, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(data, "deviceId");
                var buyerId = GetString(data, "buyerId");
                if (string.IsNullOrWhiteSpace(businessId) || string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("businessId and productId required");

                try
                {
                    if (string.IsNullOrWhiteSpace(GetString(data, "productName")) || string.IsNullOrWhiteSpace(GetString(data, "productImage")))
                    {
                        var devSnap = await _db.Collection("devices").Document(productId).GetSnapshotAsync();
                        if (devSnap.Exists)
                        {
                            var devDict = devSnap.ToDictionary();
                            if (string.IsNullOrWhiteSpace(GetString(data, "productName"))) data["productName"] = GetString(devDict, "name");
                            if (string.IsNullOrWhiteSpace(GetString(data, "price"))) data["price"] = GetString(devDict, "price");
                            var img = GetString(devDict, "imageBase64");
                            if (string.IsNullOrWhiteSpace(img)) img = GetString(devDict, "imageUrl");
                            if (!string.IsNullOrWhiteSpace(img)) { data["productImage"] = img; data["imageBase64"] = img; }
                        }
                    }
                }
                catch { }

                data["businessId"] = businessId; data["ownerId"] = businessId;
                data["productId"] = productId; data["deviceId"] = productId;
                data["createdAt"] = Timestamp.GetCurrentTimestamp(); data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                if (!data.ContainsKey("status")) data["status"] = "Pending";
                if (!data.ContainsKey("businessResponse")) data["businessResponse"] = "";
                var doc = await _db.Collection("interests").AddAsync(data);
                try { await _db.Collection("orders").Document(doc.Id).SetAsync(data); } catch { }
                return doc.Id;
            }, null!, "CreateInterest");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var all = new List<Dictionary<string, object>>();
                var seen = new HashSet<string>();
                foreach (var field in new[] { "buyerId", "userId" })
                {
                    var snap = await _db.Collection("interests").WhereEqualTo(field, buyerId).GetSnapshotAsync();
                    foreach (var doc in snap.Documents) { if (seen.Add(doc.Id)) all.Add(SanitizeInterest(doc.ToDictionary(), doc.Id)); }
                }
                all = all.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
                return all;
            }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var all = new List<Dictionary<string, object>>();
                var seen = new HashSet<string>();
                foreach (var coll in new[] { "interests", "orders" })
                {
                    foreach (var field in new[] { "businessId", "ownerId" })
                    {
                        try
                        {
                            var snap = await _db.Collection(coll).WhereEqualTo(field, businessId).GetSnapshotAsync();
                            foreach (var doc in snap.Documents) { if (seen.Add(doc.Id)) all.Add(SanitizeInterest(doc.ToDictionary(), doc.Id)); }
                        }
                        catch { }
                    }
                }
                return all.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
            }, new List<Dictionary<string, object>>(), $"GetBusinessInterests {businessId}");
        }

        public async Task<bool> UpdateInterestStatusAsync(string interestId, string status, string? businessName = null, string? businessResponse = null)
        {
            if (string.IsNullOrWhiteSpace(interestId) || string.IsNullOrWhiteSpace(status)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                var updateData = new Dictionary<string, object>
                {
                    ["status"] = status,
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp(),
                    ["businessName"] = businessName ?? "",
                    ["businessResponse"] = !string.IsNullOrWhiteSpace(businessResponse) ? businessResponse : status.Equals("Successful", StringComparison.OrdinalIgnoreCase) ? "We will get in touch with you soon. Please wait for our call or WhatsApp message." : "Sorry, this product is no longer available. The business is currently busy.",
                };
                updateData["response"] = updateData["businessResponse"];
                updateData["message"] = updateData["businessResponse"];

                bool found = false;
                foreach (var col in new[] { "interests", "orders" })
                {
                    try { var snap = await _db.Collection(col).Document(interestId).GetSnapshotAsync(); if (snap.Exists) { await _db.Collection(col).Document(interestId).SetAsync(updateData, SetOptions.MergeAll); found = true; } } catch { }
                }
                return found;
            }, false, $"UpdateInterest {interestId}");
        }

        public async Task<string> AcceptOrderToTransactionAsync(string interestId, string businessName)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                var interestSnap = await _db.Collection("interests").Document(interestId).GetSnapshotAsync();
                if (!interestSnap.Exists) interestSnap = await _db.Collection("orders").Document(interestId).GetSnapshotAsync();
                if (!interestSnap.Exists) throw new ArgumentException("Order not found");
                var interestData = interestSnap.ToDictionary();

                var productId = GetString(interestData, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(interestData, "deviceId");
                var businessId = GetString(interestData, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(interestData, "ownerId");

                Dictionary<string, object> deviceData = new();
                try
                {
                    if (!string.IsNullOrWhiteSpace(productId))
                    {
                        var devSnap = await _db.Collection("devices").Document(productId).GetSnapshotAsync();
                        if (devSnap.Exists) deviceData = devSnap.ToDictionary();
                    }
                }
                catch { }

                var transactionData = new Dictionary<string, object>
                {
                    ["transactionId"] = Guid.NewGuid().ToString("N"),
                    ["interestId"] = interestId,
                    ["orderId"] = interestId,
                    ["productId"] = productId,
                    ["deviceId"] = productId,
                    ["businessId"] = businessId,
                    ["ownerId"] = businessId,
                    ["businessName"] = businessName,
                    ["buyerId"] = GetString(interestData, "buyerId"),
                    ["buyerName"] = GetString(interestData, "buyerName"),
                    ["buyerPhone"] = GetString(interestData, "buyerPhone"),
                    ["buyerLocation"] = GetString(interestData, "buyerLocation"),
                    ["productName"] = GetString(interestData, "productName") != "" ? GetString(interestData, "productName") : GetString(deviceData, "name"),
                    ["name"] = GetString(interestData, "productName") != "" ? GetString(interestData, "productName") : GetString(deviceData, "name"),
                    ["price"] = GetString(interestData, "price") != "" ? GetString(interestData, "price") : GetString(deviceData, "price"),
                    ["productPrice"] = GetString(interestData, "price") != "" ? GetString(interestData, "price") : GetString(deviceData, "price"),
                    ["brand"] = GetString(deviceData, "brand"),
                    ["description"] = GetString(deviceData, "description"),
                    ["imageBase64"] = GetString(interestData, "productImage") != "" ? GetString(interestData, "productImage") : GetString(deviceData, "imageBase64"),
                    ["productImage"] = GetString(interestData, "productImage") != "" ? GetString(interestData, "productImage") : GetString(deviceData, "imageBase64"),
                    ["imageUrl"] = GetString(interestData, "productImage") != "" ? GetString(interestData, "productImage") : GetString(deviceData, "imageBase64"),
                    ["contactNumber"] = GetString(deviceData, "contactNumber"),
                    ["location"] = GetString(deviceData, "location"),
                    ["category"] = GetString(deviceData, "category"),
                    ["transactionStatus"] = "InProgress",
                    ["status"] = "Successful",
                    ["businessResponse"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["response"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["message"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["createdAt"] = Timestamp.GetCurrentTimestamp(),
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                };

                var transRef = await _db.Collection("transactions").AddAsync(transactionData);

                try
                {
                    if (!string.IsNullOrWhiteSpace(productId))
                    {
                        await _db.Collection("devices").Document(productId).DeleteAsync();
                        try { await _db.Collection("products").Document(productId).DeleteAsync(); } catch { }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete device {ProductId} on accept", productId); }

                var updateData = new Dictionary<string, object>
                {
                    ["status"] = "Successful",
                    ["businessName"] = businessName,
                    ["businessResponse"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["response"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["message"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.",
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                };
                try { await _db.Collection("interests").Document(interestId).SetAsync(updateData, SetOptions.MergeAll); } catch { }
                try { await _db.Collection("orders").Document(interestId).SetAsync(updateData, SetOptions.MergeAll); } catch { }

                _logger.LogInformation("Order {InterestId} accepted to transaction {TransId}", interestId, transRef.Id);
                return transRef.Id;
            }, null!, $"AcceptToTransaction {interestId}");
        }

        public async Task<List<Dictionary<string, object>>> GetTransactionsByBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("transactions").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0) snap = await _db.Collection("transactions").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                var all = snap.Documents.Select(d => SanitizeTransaction(d.ToDictionary(), d.Id)).ToList();
                return all.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
            }, new List<Dictionary<string, object>>(), $"GetTransactions {businessId}");
        }

        // BOSS FIX: Complete = delete transaction doc after completion - frees slot for 20 limit
        public async Task<bool> CompleteTransactionAsync(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("transactions").Document(transactionId).GetSnapshotAsync();
                string productId = "";
                if (snap.Exists)
                {
                    var data = snap.ToDictionary();
                    productId = GetString(data, "productId");
                    if (string.IsNullOrWhiteSpace(productId)) productId = GetString(data, "deviceId");
                }

                if (!string.IsNullOrWhiteSpace(productId))
                {
                    try { await _db.Collection("devices").Document(productId).DeleteAsync(); } catch { }
                    try { await _db.Collection("products").Document(productId).DeleteAsync(); } catch { }
                    try { await _db.Collection("inventory").Document(productId).DeleteAsync(); } catch { }
                }

                try
                {
                    var interestId = snap.Exists ? GetString(snap.ToDictionary(), "interestId") : "";
                    if (!string.IsNullOrWhiteSpace(interestId))
                    {
                        try { await _db.Collection("interests").Document(interestId).DeleteAsync(); } catch { }
                        try { await _db.Collection("orders").Document(interestId).DeleteAsync(); } catch { }
                    }
                }
                catch { }

                await _db.Collection("transactions").Document(transactionId).DeleteAsync();
                _logger.LogInformation("Transaction {Id} completed and deleted - slot freed", transactionId);
                return true;
            }, false, $"CompleteTransaction {transactionId}");
        }

        // BOSS FIX: Incomplete = republish as new device + delete transaction - returns to buyer home
        public async Task<bool> IncompleteTransactionAsync(string transactionId)
        {
            if (string.IsNullOrWhiteSpace(transactionId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("transactions").Document(transactionId).GetSnapshotAsync();
                if (!snap.Exists) return false;
                var data = snap.ToDictionary();

                var productId = Guid.NewGuid().ToString("N");
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var businessName = GetString(data, "businessName");
                var name = GetString(data, "productName");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(name)) name = "Product";
                var price = GetString(data, "price");
                if (string.IsNullOrWhiteSpace(price)) price = GetString(data, "productPrice");
                var brand = GetString(data, "brand");
                var desc = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(desc)) desc = name;
                var img = GetString(data, "productImage");
                if (string.IsNullOrWhiteSpace(img)) img = GetString(data, "imageBase64");
                if (string.IsNullOrWhiteSpace(img)) img = GetString(data, "imageUrl");
                var contact = GetString(data, "contactNumber");
                var location = GetString(data, "location");
                var category = GetString(data, "category");
                if (string.IsNullOrWhiteSpace(category)) category = "General";

                var now = Timestamp.GetCurrentTimestamp();
                var firestoreData = new Dictionary<string, object>
                {
                    ["id"] = productId,
                    ["deviceId"] = productId,
                    ["productId"] = productId,
                    ["name"] = name,
                    ["productName"] = name,
                    ["model"] = name,
                    ["brand"] = brand,
                    ["category"] = category,
                    ["price"] = price,
                    ["productPrice"] = price,
                    ["contactNumber"] = contact,
                    ["businessId"] = businessId,
                    ["ownerId"] = businessId,
                    ["businessName"] = businessName,
                    ["description"] = desc,
                    ["status"] = "active",
                    ["isAvailable"] = true,
                    ["isActive"] = true,
                    ["imageBase64"] = img,
                    ["imageUrl"] = img,
                    ["hasImage"] = !string.IsNullOrWhiteSpace(img),
                    ["location"] = location,
                    ["stock"] = 100,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };

                await _db.Collection("devices").Document(productId).SetAsync(firestoreData);
                try { await _db.Collection("products").Document(productId).SetAsync(firestoreData, SetOptions.MergeAll); } catch { }

                var interestId = GetString(data, "interestId");
                if (!string.IsNullOrWhiteSpace(interestId))
                {
                    var failUpdate = new Dictionary<string, object>
                    {
                        ["status"] = "Failed",
                        ["businessResponse"] = "Sorry, this product is back in stock.",
                        ["response"] = "Sorry, this product is back in stock.",
                        ["message"] = "Sorry, this product is back in stock.",
                        ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                    };
                    try { await _db.Collection("interests").Document(interestId).DeleteAsync(); } catch { }
                    try { await _db.Collection("orders").Document(interestId).DeleteAsync(); } catch { }
                }

                await _db.Collection("transactions").Document(transactionId).DeleteAsync();
                _logger.LogInformation("Transaction {Id} incomplete - republished as {NewId}", transactionId, productId);
                return true;
            }, false, $"IncompleteTransaction {transactionId}");
        }
    }
}