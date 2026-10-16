using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace IrhamTrade.Server.Services;

public class FirebaseService
{
    public FirebaseAuth? FirebaseAuth { get; private set; }
    public FirestoreDb? FirestoreDb { get; private set; }
    public bool IsInitialized { get; private set; } = false;

    public FirebaseService(IConfiguration configuration, ILogger<FirebaseService> logger)
    {
        const int maxRetries = 3;
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var jsonFromEnv = Environment.GetEnvironmentVariable("FIREBASE_KEY_JSON");
                var projectId = Environment.GetEnvironmentVariable("Firebase__ProjectId")
                            ?? configuration["Firebase:ProjectId"]
                            ?? "irhamtrade-31ca6";

                GoogleCredential credential;

                if (!string.IsNullOrEmpty(jsonFromEnv))
                {
                    logger.LogInformation("Initializing Firebase from ENV, Project: {ProjectId}, Attempt: {Attempt}", projectId, attempt);
                    credential = GoogleCredential.FromJson(jsonFromEnv);
                }
                else
                {
                    logger.LogInformation("Initializing Firebase from local file, Attempt: {Attempt}", attempt);
                    var possiblePaths = new[]
                    {
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "firebase-key.json"),
                        Path.Combine(Directory.GetCurrentDirectory(), "Data", "firebase-key.json"),
                        "Data/firebase-key.json"
                    };
                    var path = possiblePaths.FirstOrDefault(File.Exists) ?? possiblePaths[0];
                    if (!File.Exists(path))
                        throw new FileNotFoundException($"Firebase key file not found. Checked: {string.Join(", ", possiblePaths)}");

                    credential = GoogleCredential.FromFile(path);
                }

                if (FirebaseApp.DefaultInstance == null)
                {
                    FirebaseApp.Create(new AppOptions
                    {
                        Credential = credential,
                        ProjectId = projectId
                    });
                }

                FirebaseAuth = FirebaseAuth.DefaultInstance;

                var firestoreClient = new FirestoreClientBuilder { Credential = credential }.Build();
                FirestoreDb = FirestoreDb.Create(projectId, firestoreClient);

                IsInitialized = true;
                logger.LogInformation("Firebase initialized successfully. Project: {ProjectId}", projectId);
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

        logger.LogError(lastEx, "Firebase initialization failed after {MaxRetries} attempts. App will run with SQLite only.", maxRetries);
        IsInitialized = false;
    }
}