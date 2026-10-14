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

        // === DEVICES BOSS - PROFESSIONAL ZERO-CRASH ===
        public async Task<List<Dictionary<string, object>>> GetAllDevicesAsync()
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
                _logger.LogError(ex, "GetAllDevices FAIL Boss: {Message}", ex.Message);
                // Fallback without OrderBy if index missing Boss
                try
                {
                    var snap = await _db.Collection("devices").Limit(500).GetSnapshotAsync();
                    return snap.Documents.Select(d => { var dict = d.ToDictionary(); dict["id"] = d.Id; dict["deviceId"] = d.Id; return dict; }).ToList();
                }
                catch { return new List<Dictionary<string, object>>(); }
            }
        }

        public async Task<List<Dictionary<string, object>>> GetDevicesByBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new List<Dictionary<string, object>>();
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
        }

        public async Task<string> CreateDeviceAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            try
            {
                // ===== IMAGE FIX Boss - 15 lines professional =====
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

                data["business_id"] = data["businessId"]; // duplicate for compatibility Boss
                if (!data.ContainsKey("status")) data["status"] = "active";

                var doc = await _db.Collection("devices").AddAsync(data);
                _logger.LogInformation("CreateDevice Boss {DeviceId} Business {BusinessId} Image:{HasImage}", doc.Id, data["businessId"], data.ContainsKey("hasImage"));
                return doc.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateDevice FAIL Boss: {Message}", ex.Message);
                throw; // Let controller return 500 Boss with real error Boss
            }
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || data == null) return false;
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateDevice FAIL Boss {DeviceId}: {Message}", deviceId, ex.Message);
                return false;
            }
        }

        public async Task<bool> DeleteDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            try
            {
                await _db.Collection("devices").Document(deviceId).DeleteAsync();
                _logger.LogInformation("DeleteDevice Boss {DeviceId}", deviceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteDevice FAIL Boss");
                return false;
            }
        }

        // === INTERESTS BOSS ===
        public async Task<string> CreateInterestAsync(Dictionary<string, object> data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            try
            {
                data["createdAt"] = Timestamp.GetCurrentTimestamp();
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                if (!data.ContainsKey("status")) data["status"] = "Pending";
                if (!data.ContainsKey("date")) data["date"] = Timestamp.GetCurrentTimestamp();
                var doc = await _db.Collection("interests").AddAsync(data);
                _logger.LogInformation("CreateInterest Boss {Id} Business {BusinessId}", doc.Id, data.TryGetValue("businessId", out var b) ? b : "unknown");
                return doc.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateInterest FAIL Boss");
                throw;
            }
        }

        public async Task<List<Dictionary<string, object>>> GetBuyerInterestsAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return new();
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
                catch { return new(); }
            }
        }

        public async Task<List<Dictionary<string, object>>> GetBusinessInterestsAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return new();
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
                catch { return new(); }
            }
        }

        public async Task<bool> UpdateInterestStatusAsync(string interestId, string status)
        {
            if (string.IsNullOrWhiteSpace(interestId) || string.IsNullOrWhiteSpace(status)) return false;
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateInterest FAIL Boss");
                return false;
            }
        }

        // === BUYERS + BUSINESSES PROFILE BOSS ===
        public async Task<Dictionary<string, object>?> GetBuyerProfileAsync(string buyerId)
        {
            if (string.IsNullOrWhiteSpace(buyerId)) return null;
            try
            {
                var snap = await _db.Collection("users").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) snap = await _db.Collection("buyers").Document(buyerId).GetSnapshotAsync();
                if (!snap.Exists) return null;
                var dict = snap.ToDictionary(); dict["id"] = snap.Id; return dict;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBuyerProfile FAIL Boss");
                return null;
            }
        }

        public async Task<bool> UpdateBuyerProfileAsync(string buyerId, Dictionary<string, object> data)
        {
            if (string.IsNullOrWhiteSpace(buyerId) || data == null) return false;
            try
            {
                data["updatedAt"] = Timestamp.GetCurrentTimestamp();
                await _db.Collection("users").Document(buyerId).SetAsync(data, SetOptions.MergeAll);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateBuyerProfile FAIL Boss");
                return false;
            }
        }

        public async Task<Dictionary<string, object>?> GetBusinessAsync(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId)) return null;
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBusiness FAIL Boss");
                return null;
            }
        }

        public async Task<List<Dictionary<string, object>>> GetAllBusinessesWithDevicesAsync()
        {
            try
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAllBusinessesWithDevices FAIL Boss");
                return new List<Dictionary<string, object>>();
            }
        }
    }
}