using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

namespace IrhamTrade.Server.Services;

public class FirebaseService
{
    public FirebaseAuth? FirebaseAuth { get; private set; }
    public FirestoreDb? FirestoreDb { get; private set; }
    public bool IsInitialized { get; private set; } = false;

    public FirebaseService(IConfiguration configuration)
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
                Console.WriteLine("🔥 Boss Firebase initializing from ENV Boss! - SECURE");
                credential = GoogleCredential.FromJson(jsonFromEnv);
            }
            else
            {
                Console.WriteLine("🔥 Boss Firebase initializing from local file Boss!");
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "firebase-key.json");
                if (!File.Exists(path))
                    path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "firebase-key.json");
                if (!File.Exists(path))
                    path = "Data/firebase-key.json";

                credential = GoogleCredential.FromFile(path);
            }

            if (FirebaseApp.DefaultInstance == null)
            {
                FirebaseApp.Create(new AppOptions()
                {
                    Credential = credential,
                    ProjectId = projectId
                });
            }

            FirebaseAuth = FirebaseAuth.DefaultInstance;
            FirestoreDb = FirestoreDb.Create(projectId, new FirestoreClientBuilder { Credential = credential }.Build());

            IsInitialized = true;
            Console.WriteLine($"🔥 Boss Firebase initialized successfully Boss! Project: {projectId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Boss Firebase init failed Boss: {ex.Message}");
            IsInitialized = false;
        }
    }
}