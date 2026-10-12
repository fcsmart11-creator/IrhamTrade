using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

namespace IrhamTrade.Server.Services
{
    public class FirebaseService
    {
        private static readonly object _lock = new object();
        private static bool _initialized = false;

        public FirebaseAuth? FirebaseAuth { get; private set; }
        public FirestoreDb? FirestoreDb { get; private set; }
        public bool IsInitialized { get; private set; } = false;

        public FirebaseService(IConfiguration config, IWebHostEnvironment env, ILogger<FirebaseService> logger)
        {
            lock (_lock)
            {
                try
                {
                    var projectId = config["Firebase:ProjectId"];
                    var bucket = config["Firebase:StorageBucket"];
                    var keyRelativePath = config["Firebase:KeyFilePath"] ?? "Data/firebase-key.json";

                    var keyPath = Path.Combine(env.ContentRootPath, keyRelativePath);

                    logger.LogInformation($"Boss - Firebase init attempt Boss: {projectId}, Key: {keyPath}");

                    if (File.Exists(keyPath))
                    {
                        if (FirebaseApp.DefaultInstance == null)
                        {
                            FirebaseApp.Create(new AppOptions()
                            {
                                Credential = GoogleCredential.FromFile(keyPath),
                                ProjectId = projectId
                            });
                            logger.LogInformation($"Boss - FirebaseApp created Boss: {projectId}");
                        }

                        FirebaseAuth = FirebaseAuth.DefaultInstance;
                        FirestoreDb = FirestoreDb.Create(projectId);

                        IsInitialized = true;
                        _initialized = true;

                        logger.LogInformation($"Boss - Firebase FULLY initialized Boss! Project: {projectId}, Bucket: {bucket} Boss - Ready for huge users Boss!");
                    }
                    else
                    {
                        logger.LogWarning($"Boss - WARNING Boss - Firebase key NOT FOUND Boss at {keyPath} Boss - Auth will use SQLite only Boss!");
                        IsInitialized = false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Boss - Firebase init failed Boss - will use SQLite only Boss");
                    IsInitialized = false;
                }
            }
        }
    }
}