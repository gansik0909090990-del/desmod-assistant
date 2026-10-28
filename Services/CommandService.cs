using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using DesmodAssistant.Models;

namespace DesmodAssistant.Services;

public sealed record CommandResult(bool Success, string Message, bool RequiresConfirmation = false, Func<CommandResult>? ConfirmedAction = null);

public sealed class CommandService
{
    private readonly HttpClient _http = new();

    public async Task<CommandResult> ExecuteAsync(string rawCommand, AssistantSettings settings)
    {
        var command = rawCommand.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command)) return new(false, "Команда пустая");

        if (ContainsAny(command, "открой youtube", "открой ютуб", "запусти youtube", "запусти ютуб"))
        {
            var query = ExtractAfter(command, "найди", "канал", "видео");
            if (string.IsNullOrWhiteSpace(query)) return OpenUrl("https://www.youtube.com", "Открываю YouTube");
            query = CleanYouTubeQuery(query);
            if (ContainsAny(command, "последнее", "новое видео", "свежее видео"))
                return await OpenLatestYouTubeVideoAsync(query);
            return OpenUrl($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}&sp=CAI%253D", $"Ищу свежие видео: {query}");
        }

        if (ContainsAny(command, "открой доту", "запусти доту", "dota 2", "дота 2"))
            return OpenUrl("steam://rungameid/570", "Запускаю Dota 2");

        if (ContainsAny(command, "открой стим", "запусти стим", "open steam"))
            return OpenKnownApp("Steam", new[] { "steam.exe" }, new[]
            {
                @"C:\Program Files (x86)\Steam\steam.exe", @"C:\Program Files\Steam\steam.exe"
            });

        if (ContainsAny(command, "tlauncher", "т лаунчер", "тлаунчер"))
            return OpenKnownApp("TLauncher", new[] { "TLauncher.exe" }, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".tlauncher", "TLauncher.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", ".minecraft", "TLauncher.exe")
            });

        if (ContainsAny(command, "capcut", "капкат", "кап кат"))
            return OpenKnownApp("CapCut", new[] { "CapCut.exe" }, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapCut", "Apps", "CapCut.exe")
            });

        if (ContainsAny(command, "сделай скриншот", "снимок экрана", "скрин экрана"))
            return CaptureScreenshot();

        if (ContainsAny(command, "начни запись", "запусти запись", "nvidia запись", "запись экрана"))
        {
            SendKeys.SendWait("%{F9}");
            return new(true, "Переключаю запись NVIDIA");
        }

        if (command.StartsWith("открой сайт ") || command.StartsWith("перейди на "))
        {
            var target = command.Replace("открой сайт ", "").Replace("перейди на ", "").Trim();
            if (!target.StartsWith("http")) target = "https://" + target;
            return OpenUrl(target, "Открываю сайт");
        }

        if (ContainsAny(command, "выключи компьютер", "перезагрузи компьютер"))
        {
            var restart = command.Contains("перезагрузи");
            return new(false, restart ? "Подтвердите перезагрузку компьютера" : "Подтвердите выключение компьютера", true,
                () =>
                {
                    Process.Start(new ProcessStartInfo("shutdown", restart ? "/r /t 0" : "/s /t 0") { UseShellExecute = false });
                    return new(true, restart ? "Перезагружаю компьютер" : "Выключаю компьютер");
                });
        }

        if (!string.IsNullOrWhiteSpace(settings.AiApiKey))
            return await AskAiAsync(rawCommand, settings);

        return new(false, "Команда пока не распознана. Добавьте ключ ИИ в настройках или сформулируйте иначе");
    }

    private async Task<CommandResult> AskAiAsync(string command, AssistantSettings settings)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.AiEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = settings.AiModel,
                input = "Ты голосовой Windows-помощник. Ответь кратко по-русски. Если действие нельзя выполнить локально, объясни это. Запрос: " + command
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return new(false, $"ИИ вернул ошибку {response.StatusCode}");
            using var document = JsonDocument.Parse(json);
            var text = document.RootElement.TryGetProperty("output_text", out var direct)
                ? direct.GetString()
                : document.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
            return new(true, text ?? "Ответ получен");
        }
        catch (Exception ex)
        {
            return new(false, "Не удалось обратиться к ИИ: " + ex.Message);
        }
    }

    private static CommandResult CaptureScreenshot()
    {
        try
        {
            var bounds = Screen.AllScreens.Aggregate(Rectangle.Empty, (current, screen) => Rectangle.Union(current, screen.Bounds));
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Desmod Assistant");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Screenshot-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return new(true, "Скриншот сохранён в папке Изображения — Desmod Assistant");
        }
        catch (Exception ex)
        {
            return new(false, "Не удалось сделать скриншот: " + ex.Message);
        }
    }

    private static CommandResult OpenUrl(string url, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return new(true, message);
        }
        catch (Exception ex) { return new(false, "Не удалось открыть: " + ex.Message); }
    }

    private static CommandResult OpenKnownApp(string displayName, IEnumerable<string> processNames, IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return new(true, $"Запускаю {displayName}");
            }
            catch { }
        }

        foreach (var name in processNames)
        {
            try
            {
                Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
                return new(true, $"Запускаю {displayName}");
            }
            catch { }
        }

        return new(false, $"{displayName} не найден. Укажите путь к приложению в будущей версии настроек команд");
    }

    private static string ExtractAfter(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return text[(index + marker.Length)..].Trim();
        }
        return "";
    }

    private async Task<CommandResult> OpenLatestYouTubeVideoAsync(string query)
    {
        try
        {
            var searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query + " официальный канал")}&sp=CAI%253D";
            var html = await _http.GetStringAsync(searchUrl);
            var videoId = Regex.Match(html, "\\\"videoId\\\":\\\"(?<id>[A-Za-z0-9_-]{11})\\\"").Groups["id"].Value;
            if (!string.IsNullOrWhiteSpace(videoId))
                return OpenUrl($"https://www.youtube.com/watch?v={videoId}&autoplay=1", $"Запускаю последнее видео: {query}");
            return OpenUrl(searchUrl, $"Открываю свежие видео: {query}");
        }
        catch
        {
            return OpenUrl($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}&sp=CAI%253D", $"Открываю свежие видео: {query}");
        }
    }

    private static string CleanYouTubeQuery(string query)
    {
        var cutMarkers = new[] { " и запусти", " и включи", " последнее", " новое видео", " свежее видео" };
        foreach (var marker in cutMarkers)
        {
            var index = query.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) query = query[..index];
        }
        return query.Trim(' ', ',', '.', '!', '?');
    }

    private static bool ContainsAny(string value, params string[] options) =>
        options.Any(option => value.Contains(option, StringComparison.OrdinalIgnoreCase));
}
