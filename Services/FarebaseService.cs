using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using System;

public class FirebaseService
{
    public static void Initialize()
    {
        var jsonEnv = Environment.GetEnvironmentVariable("FIREBASE_KEY_JSON");
        var projectId = Environment.GetEnvironmentVariable("Firebase__ProjectId") ?? "irhamtrade-31ca6";

        GoogleCredential cred;

        if (!string.IsNullOrEmpty(jsonEnv))
        {
            Console.WriteLine("🔥 Firebase: Using ENV variable - SECURE Boss!");
            cred = GoogleCredential.FromJson(jsonEnv);
        }
        else
        {
            Console.WriteLine("🔥 Firebase: Using local file for testing Boss!");
            cred = GoogleCredential.FromFile("Data/firebase-key.json");
        }

        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions { Credential = cred, ProjectId = projectId });
            Console.WriteLine($"🔥 Firebase Initialized for project: {projectId} Boss!");
        }
    }
}