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
            if (value is Timestamp timestamp)
            {
                return timestamp.ToDateTime();
            }
            return value;
        }

        private static Dictionary<string, object> SanitizeDoc(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in data)
            {
                clean[kv.Key] = ConvertValue(kv.Value);
            }
            clean["id"] = docId;
            clean["deviceId"] = docId;
            if (!clean.ContainsKey("productName") && clean.TryGetValue("name", out var n)) clean["productName"] = n;
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;
            return clean;
        }

        private static Dictionary<string, object> SanitizeInterest(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in data)
            {
                clean[kv.Key] = ConvertValue(kv.Value);
            }
            clean["id"] = docId;
            clean["interestId"] = docId;
            clean["orderId"] = docId;
            return clean;
        }

        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    var snap = await _db.Collection("devices").WhereEqualTo("status", "active").Limit(200).GetSnapshotAsync();
                    if (snap.Count == 0)
                    {
                        snap = await _db.Collection("devices").Limit(200).GetSnapshotAsync();
                    }
                    var list = snap.Documents.Select(doc => SanitizeDoc(doc.ToDictionary(), doc.Id)).ToList();
                    list = list.OrderByDescending(d =>
                    {
                        if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt;
                        return DateTime.MinValue;
                    }).ToList();
                    _logger.LogInformation("GetAllDevices count {Count}", list.Count);
                    return list;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetAllDevices with status filter failed, trying simple list");
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
                try
                {
                    var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                    if (snap.Count == 0)
                        snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();

                    var list = snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                    list = list.OrderByDescending(d =>
                    {
                        if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt;
                        return DateTime.MinValue;
                    }).ToList();
                    return list;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetDevicesByBusiness failed for {BusinessId}", businessId);
                    var snap = await _db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                    return snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                }
            }, new List<Dictionary<string, object>>(), $"GetDevicesByBusiness {businessId}");
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                var name = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "model");
                if (string.IsNullOrWhiteSpace(name)) name = GetString(data, "productName");
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Device name is required");

                var brand = GetString(data, "brand");
                var price = GetString(data, "price");
                var priceNum = GetString(data, "productPrice");
                if (string.IsNullOrWhiteSpace(price)) price = priceNum;
                var contactNumber = GetString(data, "contactNumber");
                if (string.IsNullOrWhiteSpace(contactNumber)) contactNumber = GetString(data, "contact");
                if (string.IsNullOrWhiteSpace(contactNumber)) contactNumber = GetString(data, "phone");
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "business_id");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var businessName = GetString(data, "businessName");
                var description = GetString(data, "description");
                if (string.IsNullOrWhiteSpace(description)) description = name;
                var imageBase64 = GetString(data, "imageBase64");
                if (string.IsNullOrWhiteSpace(imageBase64)) imageBase64 = GetString(data, "image");
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
                    ["image"] = imageBase64,
                    ["imageUrl"] = imageBase64,
                    ["hasImage"] = !string.IsNullOrWhiteSpace(imageBase64),
                    ["location"] = location,
                    ["stock"] = 100,
                    ["createdAt"] = now,
                    ["updatedAt"] = now
                };

                await _db.Collection("devices").Document(docId).SetAsync(firestoreData);
                try { await _db.Collection("products").Document(docId).SetAsync(firestoreData, SetOptions.MergeAll); } catch { }

                _logger.LogInformation("Device created {DeviceId} {Name} Business {BusinessId}", docId, name, businessId);
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
                    if (kv.Key.Equals("imageBase64", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("image", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("imageUrl", StringComparison.OrdinalIgnoreCase))
                    {
                        var img = kv.Value?.ToString() ?? "";
                        if (img.Length > 20)
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
                try { await _db.Collection("products").Document(deviceId).SetAsync(update, SetOptions.MergeAll); } catch { }
                return true;
            }, false, $"UpdateDevice {deviceId}");
        }

        public async Task<bool> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                try { await _db.Collection("products").Document(deviceId).DeleteAsync(); } catch { }
                return true;
            }, false, $"DeleteDevice {deviceId}");
        }

        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return await ExecuteWithRetryAsync(async () =>
            {
                var businessId = GetString(data, "businessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "ownerId");
                var productId = GetString(data, "productId");
                if (string.IsNullOrWhiteSpace(productId)) productId = GetString(data, "deviceId");
                var buyerId = GetString(data, "buyerId");
                if (string.IsNullOrWhiteSpace(buyerId)) buyerId = GetString(data, "userId");
                var buyerName = GetString(data, "buyerName");
                if (string.IsNullOrWhiteSpace(buyerName)) buyerName = GetString(data, "fullName");
                if (string.IsNullOrWhiteSpace(buyerName)) buyerName = GetString(data, "name");

                if (string.IsNullOrWhiteSpace(businessId)) throw new ArgumentException("businessId is required for Order");
                if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("productId is required");

                data["businessId"] = businessId;
                data["ownerId"] = businessId;
                data["productId"] = productId;
                data["deviceId"] = productId;
                data["buyerId"] = buyerId;
                data["buyerName"] = buyerName;
                data["createdAt"] = Timestamp.GetCurrentTimestamp();
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                if (!data.ContainsKey("status")) data["status"] = "Pending";
                if (!data.ContainsKey("businessResponse")) data["businessResponse"] = "";
                if (!data.ContainsKey("businessName")) data["businessName"] = GetString(data, "businessName");
                if (!data.ContainsKey("date")) data["date"] = Timestamp.GetCurrentTimestamp();

                var doc = await _db.Collection("interests").AddAsync(data);
                try { await _db.Collection("orders").Document(doc.Id).SetAsync(data); } catch { }

                _logger.LogInformation("Interest created {InterestId} Product {ProductId} Business {BusinessId} Buyer {BuyerName}", doc.Id, productId, businessId, buyerName);
                return doc.Id;
            }, null!, "CreateInterest");
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("interests").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                var list = snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList();
                list = list.OrderByDescending(d =>
                {
                    if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt;
                    return DateTime.MinValue;
                }).ToList();
                return list;
            }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");
        }

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("interests").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("interests").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("orders").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                if (snap.Count == 0)
                    snap = await _db.Collection("orders").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();

                var list = snap.Documents.Select(d => SanitizeInterest(d.ToDictionary(), d.Id)).ToList();
                list = list.OrderByDescending(d =>
                {
                    if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt;
                    return DateTime.MinValue;
                }).ToList();
                return list;
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
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                };

                if (!string.IsNullOrWhiteSpace(businessName))
                {
                    updateData["businessName"] = businessName;
                }

                if (!string.IsNullOrWhiteSpace(businessResponse))
                {
                    updateData["businessResponse"] = businessResponse;
                    updateData["response"] = businessResponse;
                    updateData["message"] = businessResponse;
                }
                else
                {
                    // Default professional messages
                    if (status.Equals("Successful", StringComparison.OrdinalIgnoreCase))
                    {
                        updateData["businessResponse"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.";
                    }
                    else if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        updateData["businessResponse"] = "Sorry, this product is no longer available. The business is currently busy.";
                    }
                }

                var docRef = _db.Collection("interests").Document(interestId);
                var snap = await docRef.GetSnapshotAsync();
                if (snap.Exists)
                {
                    await docRef.SetAsync(updateData, SetOptions.MergeAll);
                    try { await _db.Collection("orders").Document(interestId).SetAsync(updateData, SetOptions.MergeAll); } catch { }
                    return true;
                }

                var q = await _db.Collection("interests").WhereEqualTo("orderId", interestId).Limit(1).GetSnapshotAsync();
                if (q.Count > 0)
                {
                    await q.Documents[0].Reference.SetAsync(updateData, SetOptions.MergeAll);
                    return true;
                }

                var oq = await _db.Collection("orders").Document(interestId).GetSnapshotAsync();
                if (oq.Exists)
                {
                    await oq.Reference.SetAsync(updateData, SetOptions.MergeAll);
                    try { await _db.Collection("interests").Document(interestId).SetAsync(updateData, SetOptions.MergeAll); } catch { }
                    return true;
                }

                return false;
            }, false, $"UpdateInterest {interestId}");
        }

        public async Task<Dictionary<string, object>?> GetBuyerProfileAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return null;
            return await ExecuteWithRetryAsync(async () =>
            {
                var snap = await _db.Collection("users").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) snap = await _db.Collection("buyers").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) return null;
                return SanitizeDoc(snap.ToDictionary(), snap.Id);
            }, null, $"GetBuyerProfile {buyerId}");
        }

        public async Task<bool> UpdateBuyerProfileAsync(string buyerId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(buyerId) || data == null) return false;
            return await ExecuteWithRetryAsync(async () =>
            {
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("users").Document(buyerId).SetAsync(data, SetOptions.MergeAll);
                try { await _db.Collection("buyers").Document(buyerId).SetAsync(data, SetOptions.MergeAll); } catch { }
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
                return SanitizeDoc(snap.ToDictionary(), snap.Id);
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
                    var bizDict = SanitizeDoc(bizDoc.ToDictionary(), bizDoc.Id);
                    try
                    {
                        var devSnap = await _db.Collection("devices").WhereEqualTo("businessId", bizDoc.Id).Limit(50).GetSnapshotAsync();
                        bizDict["devices"] = devSnap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                    }
                    catch { bizDict["devices"] = new List<Dictionary<string, object>>(); }
                    return bizDict;
                });
                return (await Task.WhenAll(tasks)).ToList();
            }, new List<Dictionary<string, object>>(), "GetAllBusinessesWithDevices");
        }
    }
}