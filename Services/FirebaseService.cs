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

    public FirebaseService(IConfiguration configuration)
    {
        
        int maxRetries = 3;
        int attempt = 0;
        Exception? lastEx = null;

        while (attempt < maxRetries)
        {
            try
            {
                attempt++;
                var jsonFromEnv = Environment.GetEnvironmentVariable("FIREBASE_KEY_JSON");
                var projectId = Environment.GetEnvironmentVariable("Firebase__ProjectId")
                             ?? configuration["Firebase:ProjectId"]
                             ?? "irhamtrade-31ca6";

                GoogleCredential credential;

                if (!string.IsNullOrEmpty(jsonFromEnv))
                {
                    Console.WriteLine(" Boss Firebase initializing from ENV Boss! - SECURE");
                    Console.WriteLine($" Boss ENV JSON Length: {jsonFromEnv.Length} Boss Attempt {attempt} Boss");
                    credential = GoogleCredential.FromJson(jsonFromEnv);
                }
                else
                {
                    Console.WriteLine($" Firebase initializing from local file Boss! Attempt {attempt} Boss");
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "firebase-key.json");
                    if (!File.Exists(path))
                        path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "firebase-key.json");
                    if (!File.Exists(path))
                        path = "Data/firebase-key.json";

                    Console.WriteLine($" Firebase using file: {path} Boss");
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

             
                var clientBuilder = new FirestoreClientBuilder
                {
                    Credential = credential
                };
                var firestoreClient = clientBuilder.Build();
                FirestoreDb = FirestoreDb.Create(projectId, firestoreClient);

                IsInitialized = true;
                Console.WriteLine($"  Firebase initialized successfully Boss! Project: {projectId} Boss! Attempt {attempt} Boss");
                Console.WriteLine($"  FIREBASE STRONG LINK CONFIRMED Boss! Firestore READY Boss! ");
                return; 
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"  Firebase init attempt {attempt} failed Boss: {ex.Message} Boss");
                if (attempt < maxRetries)
                {
                    int delay = attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000;
                    Console.WriteLine($"  Boss Retrying in {delay}ms Boss - Render cold start survival Boss");
                    Thread.Sleep(delay);
                }
            }
        }

        
        Console.WriteLine($"  Firebase init failed after {maxRetries} attempts Boss: {lastEx?.Message} Boss");
        Console.WriteLine($"  Stack: {lastEx?.StackTrace} Boss - App will run without Firebase Boss");
        IsInitialized = false;
    }
}