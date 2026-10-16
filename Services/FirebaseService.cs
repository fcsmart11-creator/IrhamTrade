using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace IrhamTrade.Server.Services;

public class FirebaseService
{
    private static readonly object _lock = new();
    private static bool _alreadyTriedInit = false;

    public FirebaseAuth? FirebaseAuth { get; private set; }
    public FirestoreDb? FirestoreDb { get; private set; }
    public bool IsInitialized { get; private set; } = false;
    public string ProjectId { get; private set; } = "irhamtrade-31ca6";

    public FirebaseService(IConfiguration configuration, ILogger<FirebaseService> logger)
    {
        lock (_lock)
        {
            if (_alreadyTriedInit && FirebaseApp.DefaultInstance != null)
            {
                try
                {
                    ProjectId = Environment.GetEnvironmentVariable("Firebase__ProjectId")
                        ?? configuration["Firebase:ProjectId"] ?? "irhamtrade-31ca6";
                    FirebaseAuth = FirebaseAuth.DefaultInstance;
                    var credential = GetCredential(configuration, logger);
                    var firestoreClient = new FirestoreClientBuilder { Credential = credential }.Build();
                    FirestoreDb = FirestoreDb.Create(ProjectId, firestoreClient);
                    IsInitialized = true;
                    logger.LogInformation("Firebase reused existing app. Project: {ProjectId}", ProjectId);
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Reusing existing FirebaseApp failed, will re-init");
                }
            }

            const int maxRetries = 3;
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var jsonFromEnv = Environment.GetEnvironmentVariable("FIREBASE_KEY_JSON");
                    ProjectId = Environment.GetEnvironmentVariable("Firebase__ProjectId")
                                ?? configuration["Firebase:ProjectId"]
                                ?? configuration["Firebase__ProjectId"]
                                ?? "irhamtrade-31ca6";

                    GoogleCredential credential = GetCredential(configuration, logger, jsonFromEnv, attempt);

                    if (FirebaseApp.DefaultInstance == null)
                    {
                        logger.LogInformation("Creating FirebaseApp Attempt {Attempt} Project {ProjectId}", attempt, ProjectId);
                        FirebaseApp.Create(new AppOptions
                        {
                            Credential = credential,
                            ProjectId = ProjectId
                        });
                    }

                    FirebaseAuth = FirebaseAuth.DefaultInstance;

                    var firestoreClient = new FirestoreClientBuilder
                    {
                        Credential = credential,
                        // Increase timeout like Facebook backend
                    }.Build();

                    FirestoreDb = FirestoreDb.Create(ProjectId, firestoreClient);

                    // Test Firestore connection - like Facebook health check
                    try
                    {
                        var testTask = FirestoreDb.Collection("_health").Document("test").GetSnapshotAsync();
                        Task.WhenAny(testTask, Task.Delay(5000)).Wait();
                    }
                    catch { /* Health check non-critical */ }

                    IsInitialized = true;
                    _alreadyTriedInit = true;
                    logger.LogInformation("✅ Firebase initialized SUCCESS. Project: {ProjectId}, Attempt: {Attempt}", ProjectId, attempt);
                    return;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    logger.LogWarning(ex, "Firebase init attempt {Attempt} failed", attempt);
                    if (attempt < maxRetries)
                    {
                        int delay = attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000;
                        Thread.Sleep(delay);
                    }
                }
            }

            logger.LogError(lastEx, "❌ Firebase initialization failed after {MaxRetries} attempts. App will run with SQLite only fallback.", maxRetries);
            IsInitialized = false;
            _alreadyTriedInit = true;
        }
    }

    private GoogleCredential GetCredential(IConfiguration configuration, ILogger<FirebaseService> logger, string? jsonFromEnv = null, int attempt = 1)
    {
        jsonFromEnv ??= Environment.GetEnvironmentVariable("FIREBASE_KEY_JSON");

        if (!string.IsNullOrWhiteSpace(jsonFromEnv))
        {
            logger.LogInformation("Initializing Firebase from ENV JSON, Attempt: {Attempt}", attempt);
            // Handle escaped newlines from Render env
            if (jsonFromEnv.Contains("\\n")) jsonFromEnv = jsonFromEnv.Replace("\\n", "\n");
            return GoogleCredential.FromJson(jsonFromEnv);
        }
        else
        {
            logger.LogInformation("Initializing Firebase from local file, Attempt: {Attempt}", attempt);
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "firebase-key.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Data", "firebase-key.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "IrhamTrade.Server", "Data", "firebase-key.json"),
                "Data/firebase-key.json",
                "/app/Data/firebase-key.json" // Docker path on Render
            };
            var path = possiblePaths.FirstOrDefault(File.Exists);
            if (path == null)
                throw new FileNotFoundException($"Firebase key file not found. Checked: {string.Join(", ", possiblePaths)}");

            return GoogleCredential.FromFile(path);
        }
    }
}