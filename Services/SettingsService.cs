using System.Text.Json;
using System.IO;
using DesmodAssistant.Models;

namespace DesmodAssistant.Services;

public sealed class SettingsService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesmodAssistant");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public AssistantSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AssistantSettings();
            return JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(FilePath))
                   ?? new AssistantSettings();
        }
        catch
        {
            return new AssistantSettings();
        }
    }

    public void Save(AssistantSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
