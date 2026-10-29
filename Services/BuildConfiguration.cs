using System.Reflection;

namespace DesmodAssistant.Services;

public static class BuildConfiguration
{
    public static string GoogleOAuthClientId
    {
        get
        {
            var embedded = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "GoogleOAuthClientId")?.Value;

            return string.IsNullOrWhiteSpace(embedded)
                ? Environment.GetEnvironmentVariable("DESMOD_GOOGLE_OAUTH_CLIENT_ID") ?? ""
                : embedded;
        }
    }

    public static string GoogleOAuthClientSecret => ReadMetadata("GoogleOAuthClientSecret", "DESMOD_GOOGLE_OAUTH_CLIENT_SECRET");

    private static string ReadMetadata(string key, string environmentVariable)
    {
        var embedded = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(embedded)
            ? Environment.GetEnvironmentVariable(environmentVariable) ?? ""
            : embedded;
    }
}
