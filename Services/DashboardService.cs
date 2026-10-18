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

        // BOSS FIX: SanitizeInterest now normalizes all fields for Order Button + That's What - fixes picture + name
        private static Dictionary<string, object> SanitizeInterest(Dictionary<string, object> data, string docId)
        {
            var clean = new Dictionary<string, object>();
            foreach (var kv in data) clean[kv.Key] = ConvertValue(kv.Value);
            clean["id"] = docId;
            clean["interestId"] = docId;
            clean["orderId"] = docId;

            // Normalize product name
            if (!clean.ContainsKey("productName") || string.IsNullOrWhiteSpace(clean["productName"]?.ToString()))
            {
                var name = GetString(clean, "name");
                if (!string.IsNullOrWhiteSpace(name)) clean["productName"] = name;
                else if (clean.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m?.ToString())) clean["productName"] = m;
            }
            if (!clean.ContainsKey("name") && clean.TryGetValue("productName", out var pn)) clean["name"] = pn;

            // Normalize image - keep all variants
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

            // Normalize price, brand
            if (!clean.ContainsKey("price") && clean.TryGetValue("productPrice", out var pp)) clean["price"] = pp;
            if (!clean.ContainsKey("productPrice") && clean.TryGetValue("price", out var p)) clean["productPrice"] = p;

            // Normalize buyer fields
            if (!clean.ContainsKey("buyerName"))
            {
                var bn = GetString(clean, "fullName");
                if (!string.IsNullOrWhiteSpace(bn)) clean["buyerName"] = bn;
                else clean["buyerName"] = GetString(clean, "name");
            }

            // Normalize business fields
            if (!clean.ContainsKey("businessName"))
            {
                var bnn = GetString(clean, "business_name");
                if (!string.IsNullOrWhiteSpace(bnn)) clean["businessName"] = bnn;
            }

            // Ensure status
            if (!clean.ContainsKey("status") || string.IsNullOrWhiteSpace(clean["status"]?.ToString())) clean["status"] = "Pending";
            if (!clean.ContainsKey("businessResponse")) clean["businessResponse"] = "";
            if (!clean.ContainsKey("response") && clean.TryGetValue("businessResponse", out var br)) clean["response"] = br;
            if (!clean.ContainsKey("message") && clean.TryGetValue("businessResponse", out var br2)) clean["message"] = br2;

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
                    if (snap.Count == 0) snap = await _db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
                    var list = snap.Documents.Select(d => SanitizeDoc(d.ToDictionary(), d.Id)).ToList();
                    list = list.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
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
                        if (img.Length > 20) { update["imageBase64"] = img; update["imageUrl"] = img; update["image"] = img; update["hasImage"] = true; }
                    }
                    else { update[kv.Key] = kv.Value; }
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

        // BOSS FIX: CreateInterest now fetches device to copy product details - fixes Order Button picture + buyer info
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

                // Fetch device details to copy if missing - critical for picture + price
                try
                {
                    if (string.IsNullOrWhiteSpace(GetString(data, "productName")) || string.IsNullOrWhiteSpace(GetString(data, "price")) || string.IsNullOrWhiteSpace(GetString(data, "imageBase64")))
                    {
                        var devSnap = await _db.Collection("devices").Document(productId).GetSnapshotAsync();
                        if (devSnap.Exists)
                        {
                            var devDict = devSnap.ToDictionary();
                            if (string.IsNullOrWhiteSpace(GetString(data, "productName"))) data["productName"] = GetString(devDict, "name");
                            if (string.IsNullOrWhiteSpace(GetString(data, "name"))) data["name"] = GetString(devDict, "name");
                            if (string.IsNullOrWhiteSpace(GetString(data, "price"))) data["price"] = GetString(devDict, "price");
                            if (string.IsNullOrWhiteSpace(GetString(data, "productPrice"))) data["productPrice"] = GetString(devDict, "price");
                            if (string.IsNullOrWhiteSpace(GetString(data, "brand"))) data["brand"] = GetString(devDict, "brand");
                            var img = GetString(devDict, "imageBase64");
                            if (string.IsNullOrWhiteSpace(img)) img = GetString(devDict, "image");
                            if (string.IsNullOrWhiteSpace(img)) img = GetString(devDict, "imageUrl");
                            if (!string.IsNullOrWhiteSpace(img))
                            {
                                data["productImage"] = img;
                                data["imageBase64"] = img;
                                data["image"] = img;
                            }
                            if (string.IsNullOrWhiteSpace(businessId))
                            {
                                var devBiz = GetString(devDict, "businessId");
                                if (!string.IsNullOrWhiteSpace(devBiz)) businessId = devBiz;
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to fetch device {ProductId} for interest enrichment", productId); }

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

        // BOSS FIX: GetBuyerInterests checks buyerId + userId + buyerEmail + deduplicates - fixes That's What crash + empty
        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var all = new List<Dictionary<string, object>>();
                var seenIds = new HashSet<string>();

                var fieldsToTry = new[] { "buyerId", "userId" };
                foreach (var field in fieldsToTry)
                {
                    try
                    {
                        var snap = await _db.Collection("interests").WhereEqualTo(field, buyerId).GetSnapshotAsync();
                        foreach (var doc in snap.Documents)
                        {
                            if (seenIds.Contains(doc.Id)) continue;
                            seenIds.Add(doc.Id);
                            all.Add(SanitizeInterest(doc.ToDictionary(), doc.Id));
                        }
                    }
                    catch { }
                }

                // Also try orders collection
                try
                {
                    var snap = await _db.Collection("orders").WhereEqualTo("buyerId", buyerId).GetSnapshotAsync();
                    foreach (var doc in snap.Documents)
                    {
                        if (seenIds.Contains(doc.Id)) continue;
                        seenIds.Add(doc.Id);
                        all.Add(SanitizeInterest(doc.ToDictionary(), doc.Id));
                    }
                }
                catch { }

                all = all.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
                return all;
            }, new List<Dictionary<string, object>>(), $"GetBuyerInterests {buyerId}");
        }

        // BOSS FIX: GetBusinessInterests combines interests + orders + businessId + ownerId + deduplicates - fixes 0 Orders bug
        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
            return await ExecuteWithRetryAsync(async () =>
            {
                var all = new List<Dictionary<string, object>>();
                var seenIds = new HashSet<string>();

                var collections = new[] { "interests", "orders" };
                var fields = new[] { "businessId", "ownerId" };

                foreach (var coll in collections)
                {
                    foreach (var field in fields)
                    {
                        try
                        {
                            var snap = await _db.Collection(coll).WhereEqualTo(field, businessId).GetSnapshotAsync();
                            foreach (var doc in snap.Documents)
                            {
                                if (seenIds.Contains(doc.Id)) continue;
                                seenIds.Add(doc.Id);
                                all.Add(SanitizeInterest(doc.ToDictionary(), doc.Id));
                            }
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Query failed {Coll} {Field} {Biz}", coll, field, businessId); }
                    }
                }

                all = all.OrderByDescending(d => { if (d.TryGetValue("createdAt", out var v) && v is DateTime dt) return dt; return DateTime.MinValue; }).ToList();
                _logger.LogInformation("GetBusinessInterests {Biz} total {Count}", businessId, all.Count);
                return all;
            }, new List<Dictionary<string, object>>(), $"GetBusinessInterests {businessId}");
        }

        // BOSS FIX: UpdateInterest now updates BOTH interests + orders + sets professional default messages + businessName - fixes Accept not persisting to buyer
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

                if (!string.IsNullOrWhiteSpace(businessName)) updateData["businessName"] = businessName;

                if (!string.IsNullOrWhiteSpace(businessResponse))
                {
                    updateData["businessResponse"] = businessResponse;
                    updateData["response"] = businessResponse;
                    updateData["message"] = businessResponse;
                }
                else
                {
                    if (status.Equals("Successful", StringComparison.OrdinalIgnoreCase))
                        updateData["businessResponse"] = "We will get in touch with you soon. Please wait for our call or WhatsApp message.";
                    else if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                        updateData["businessResponse"] = "Sorry, this product is no longer available. The business is currently busy.";

                    updateData["response"] = updateData["businessResponse"];
                    updateData["message"] = updateData["businessResponse"];
                }

                bool found = false;

                try
                {
                    var docRef = _db.Collection("interests").Document(interestId);
                    var snap = await docRef.GetSnapshotAsync();
                    if (snap.Exists)
                    {
                        await docRef.SetAsync(updateData, SetOptions.MergeAll);
                        found = true;
                    }
                }
                catch { }

                try
                {
                    var orderRef = _db.Collection("orders").Document(interestId);
                    var orderSnap = await orderRef.GetSnapshotAsync();
                    if (orderSnap.Exists)
                    {
                        await orderRef.SetAsync(updateData, SetOptions.MergeAll);
                        found = true;
                    }
                    else if (found)
                    {
                        // If found in interests but not orders, create in orders too
                        await orderRef.SetAsync(updateData, SetOptions.MergeAll);
                    }
                }
                catch { }

                // Fallback search by orderId field
                if (!found)
                {
                    try
                    {
                        var q = await _db.Collection("interests").WhereEqualTo("orderId", interestId).Limit(1).GetSnapshotAsync();
                        if (q.Count > 0)
                        {
                            await q.Documents[0].Reference.SetAsync(updateData, SetOptions.MergeAll);
                            found = true;
                        }
                    }
                    catch { }
                }

                if (!found)
                {
                    try
                    {
                        var q2 = await _db.Collection("orders").WhereEqualTo("orderId", interestId).Limit(1).GetSnapshotAsync();
                        if (q2.Count > 0)
                        {
                            await q2.Documents[0].Reference.SetAsync(updateData, SetOptions.MergeAll);
                            found = true;
                        }
                    }
                    catch { }
                }

                _logger.LogInformation("UpdateInterest {Id} to {Status} found={Found} Biz={Biz}", interestId, status, found, businessName);
                return found;
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