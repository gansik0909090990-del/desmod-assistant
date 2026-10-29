using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
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

        if (ContainsAny(command, "яндекс музыка", "яндекс музыке", "яндекс музыку", "yandex music") &&
            ContainsAny(command, "включи", "поставь", "запусти", "найди песню", "найди трек"))
            return await OpenYandexMusicTrackAsync(command);

        if (ContainsAny(command, "скриншот", "скриншоты", "снимок экрана", "скрин экрана", "заскринь", "сделай скрин"))
            return CaptureScreenshot();

        if (ContainsAny(command, "начни запись", "включи запись", "запусти запись", "nvidia запись", "запись экрана", "запись видео"))
        {
            return ToggleNvidiaRecording();
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

        var freeTarget = ExtractAfter(command, "открой", "запусти", "найди", "покажи");
        if (!string.IsNullOrWhiteSpace(freeTarget))
        {
            var openIntent = command.Contains("открой", StringComparison.OrdinalIgnoreCase) || command.Contains("запусти", StringComparison.OrdinalIgnoreCase);
            if (openIntent)
            {
                var localResult = OpenAnyApp(freeTarget, $"Запускаю: {freeTarget}");
                if (localResult.Success) return localResult;
            }
            if (openIntent && TryResolveKnownWebsite(freeTarget, out var directUrl))
                return OpenUrl(directUrl, $"Открываю: {freeTarget}");
            if (openIntent) return new(false, $"Приложение «{freeTarget}» не найдено среди установленных программ и игр");
            return OpenUrl($"https://www.google.com/search?q={Uri.EscapeDataString(freeTarget)}", $"Ищу: {freeTarget}");
        }

        return new(false, "Для свободного понимания любых команд подключите ИИ-провайдера в настройках");
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
                input = "Ты планировщик действий Windows-ассистента. Понимай свободную русскую речь. Верни ТОЛЬКО JSON без markdown: {\"action\":\"open_url|search_web|launch_app|screenshot|answer\",\"target\":\"...\",\"reply\":\"короткий ответ по-русски\"}. Для сайтов выбирай официальный URL. Для программ action launch_app и обычное название. Не предлагай shell-команды, удаление, пароли или обход защиты. Запрос пользователя: " + command
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return new(false, $"ИИ вернул ошибку {response.StatusCode}");
            using var document = JsonDocument.Parse(json);
            var text = document.RootElement.TryGetProperty("output_text", out var direct)
                ? direct.GetString()
                : document.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
            return ExecuteAiPlan(text ?? "", command);
        }
        catch (Exception ex)
        {
            return new(false, "Не удалось обратиться к ИИ: " + ex.Message);
        }
    }

    private CommandResult ExecuteAiPlan(string rawPlan, string originalCommand)
    {
        try
        {
            var start = rawPlan.IndexOf('{');
            var end = rawPlan.LastIndexOf('}');
            if (start < 0 || end <= start) return new(true, rawPlan);
            using var plan = JsonDocument.Parse(rawPlan[start..(end + 1)]);
            var root = plan.RootElement;
            var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? "answer" : "answer";
            var target = root.TryGetProperty("target", out var targetElement) ? targetElement.GetString() ?? "" : "";
            var reply = root.TryGetProperty("reply", out var replyElement) ? replyElement.GetString() ?? "Готово" : "Готово";

            return action switch
            {
                "open_url" when Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" => OpenUrl(target, reply),
                "search_web" => OpenUrl($"https://www.google.com/search?q={Uri.EscapeDataString(target)}", reply),
                "launch_app" => OpenAnyApp(target, reply),
                "screenshot" => CaptureScreenshot(),
                _ => new(true, string.IsNullOrWhiteSpace(reply) ? originalCommand : reply)
            };
        }
        catch
        {
            return new(true, rawPlan);
        }
    }

    private static CommandResult OpenAnyApp(string target, string reply)
    {
        if (string.IsNullOrWhiteSpace(target)) return new(false, "ИИ не указал приложение");
        target = CleanLaunchTarget(target);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Contains(target, StringComparison.OrdinalIgnoreCase)
                                            && Path.GetExtension(path) is ".lnk" or ".url" or ".exe");
                if (match is null) continue;
                Process.Start(new ProcessStartInfo(match) { UseShellExecute = true });
                return new(true, reply);
            }
            catch { }
        }
        var steamResult = TryLaunchSteamGame(target, reply);
        return steamResult ?? new(false, $"Приложение «{target}» не найдено");
    }

    private static string CleanLaunchTarget(string target)
    {
        var cleaned = target.Trim(' ', ',', '.', '!', '?', ':', '-');
        foreach (var prefix in new[] { "игру ", "приложение ", "программу ", "лаунчер " })
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[prefix.Length..].Trim();
        return cleaned;
    }

    private static CommandResult? TryLaunchSteamGame(string target, string reply)
    {
        var steamAppsFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            var defaultApps = Path.Combine(steamRoot, "steamapps");
            if (Directory.Exists(defaultApps)) steamAppsFolders.Add(defaultApps);
            var librariesFile = Path.Combine(defaultApps, "libraryfolders.vdf");
            if (!File.Exists(librariesFile)) continue;
            try
            {
                var text = File.ReadAllText(librariesFile);
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
                {
                    var path = match.Groups["path"].Value.Replace("\\\\", "\\");
                    var apps = Path.Combine(path, "steamapps");
                    if (Directory.Exists(apps)) steamAppsFolders.Add(apps);
                }
            }
            catch { }
        }

        foreach (var folder in steamAppsFolders)
        {
            foreach (var manifest in Directory.EnumerateFiles(folder, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var name = Regex.Match(text, "\\\"name\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"").Groups["value"].Value;
                    var appId = Regex.Match(text, "\\\"appid\\\"\\s+\\\"(?<value>\\d+)\\\"").Groups["value"].Value;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;
                    if (!name.Contains(target, StringComparison.OrdinalIgnoreCase) && !target.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                    Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true });
                    return new(true, reply);
                }
                catch { }
            }
        }
        return null;
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
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return new(false, "Windows не сохранила файл скриншота");
            return new(true, $"Скриншот сохранён: {path}");
        }
        catch (Exception ex)
        {
            return new(false, "Не удалось сделать скриншот: " + ex.Message);
        }
    }

    private static CommandResult ToggleNvidiaRecording()
    {
        try
        {
            const byte alt = 0x12;
            const byte f9 = 0x78;
            const uint keyUp = 0x0002;
            keybd_event(alt, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(f9, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            keybd_event(f9, 0, keyUp, UIntPtr.Zero);
            keybd_event(alt, 0, keyUp, UIntPtr.Zero);
            return new(true, "Нажимаю Alt+F9 — запись NVIDIA переключена");
        }
        catch (Exception ex)
        {
            return new(false, "Не удалось переключить запись NVIDIA: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static void PressMediaPlay()
    {
        const byte mediaPlayPause = 0xB3;
        const uint keyUp = 0x0002;
        keybd_event(mediaPlayPause, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(mediaPlayPause, 0, keyUp, UIntPtr.Zero);
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

    private async Task<CommandResult> OpenYandexMusicTrackAsync(string command)
    {
        var artistIntent = ContainsAny(command, "исполнителя", "исполнитель", "артиста", "артист", "группу");
        var query = command;
        var removable = new[]
        {
            "открой яндекс музыку и", "открой яндекс музыка и", "в яндекс музыке",
            "на яндекс музыке", "яндекс музыка", "яндекс музыку", "yandex music", "в ней",
            "включи мне", "включи", "поставь мне", "поставь", "запусти",
            "найди песню", "найди трек", "исполнителя", "исполнитель", "артиста", "артист",
            "группу", "песни", "песню", "треки", "трек"
        };
        foreach (var value in removable)
            query = query.Replace(value, " ", StringComparison.OrdinalIgnoreCase);
        query = Regex.Replace(query, "\\s+", " ").Trim(' ', ',', '.', '!', '?', ':', '-');

        if (string.IsNullOrWhiteSpace(query))
            return OpenUrl("https://music.yandex.ru/", "Открываю Яндекс Музыку");

        try
        {
            if (artistIntent)
                return await OpenYandexArtistAsync(query);

            var searchUrl = $"https://api.music.yandex.net/search?text={Uri.EscapeDataString(query)}&type=track&page=0&nocorrect=false";
            using var response = await _http.GetAsync(searchUrl);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tracks = document.RootElement.GetProperty("result").GetProperty("tracks").GetProperty("results");
            if (tracks.GetArrayLength() == 0)
                return OpenUrl($"https://music.yandex.ru/search?text={Uri.EscapeDataString(query)}", $"Открываю поиск: {query}");

            var track = tracks[0];
            var trackId = track.GetProperty("id").ToString();
            var title = track.GetProperty("title").GetString() ?? query;
            var albumId = track.GetProperty("albums")[0].GetProperty("id").ToString();
            var artist = track.GetProperty("artists")[0].GetProperty("name").GetString() ?? "";
            var result = OpenUrl($"https://music.yandex.ru/album/{albumId}/track/{trackId}", $"Нашёл {artist} — {title}");
            if (!result.Success) return result;

            await Task.Delay(3500);
            PressMediaPlay();
            return new(true, $"Включаю {artist} — {title}");
        }
        catch
        {
            return OpenUrl($"https://music.yandex.ru/search?text={Uri.EscapeDataString(query)}", $"Открываю поиск трека: {query}");
        }
    }

    private async Task<CommandResult> OpenYandexArtistAsync(string query)
    {
        var searchUrl = $"https://api.music.yandex.net/search?text={Uri.EscapeDataString(query)}&type=artist&page=0&nocorrect=false";
        using var response = await _http.GetAsync(searchUrl);
        response.EnsureSuccessStatusCode();
        using var searchDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var artists = searchDocument.RootElement.GetProperty("result").GetProperty("artists").GetProperty("results");
        if (artists.GetArrayLength() == 0)
            return OpenUrl($"https://music.yandex.ru/search?text={Uri.EscapeDataString(query)}", $"Открываю поиск исполнителя: {query}");

        var artist = artists.EnumerateArray()
            .FirstOrDefault(candidate => string.Equals(candidate.GetProperty("name").GetString(), query, StringComparison.OrdinalIgnoreCase));
        if (artist.ValueKind == JsonValueKind.Undefined) artist = artists[0];
        var artistId = artist.GetProperty("id").ToString();
        var artistName = artist.GetProperty("name").GetString() ?? query;
        using var infoResponse = await _http.GetAsync($"https://api.music.yandex.net/artists/{artistId}/brief-info");
        infoResponse.EnsureSuccessStatusCode();
        using var infoDocument = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        var tracks = infoDocument.RootElement.GetProperty("result").GetProperty("popularTracks");
        if (tracks.GetArrayLength() == 0)
            return OpenUrl($"https://music.yandex.ru/artist/{artistId}", $"Открываю исполнителя: {artistName}");

        var track = tracks[0];
        var trackId = track.GetProperty("id").ToString();
        var albumId = track.GetProperty("albums")[0].GetProperty("id").ToString();
        var title = track.GetProperty("title").GetString() ?? "популярный трек";
        var result = OpenUrl($"https://music.yandex.ru/album/{albumId}/track/{trackId}", $"Открываю {artistName} — {title}");
        if (!result.Success) return result;
        await Task.Delay(3500);
        PressMediaPlay();
        return new(true, $"Включаю исполнителя {artistName}: {title}");
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

    private static bool TryResolveKnownWebsite(string target, out string url)
    {
        var normalized = target.ToLowerInvariant()
            .Replace("ё", "е")
            .Replace("официальный сайт", "")
            .Trim(' ', ',', '.', '!', '?');

        var sites = new (string[] Aliases, string Url)[]
        {
            (new[] { "яндекс музыка", "яндекс музыку", "yandex music" }, "https://music.yandex.ru/"),
            (new[] { "рутуб", "rutube" }, "https://rutube.ru/"),
            (new[] { "ютуб", "youtube" }, "https://www.youtube.com/"),
            (new[] { "вконтакте", "в контакте", "вк", "vk" }, "https://vk.com/"),
            (new[] { "телеграм", "telegram" }, "https://web.telegram.org/"),
            (new[] { "яндекс", "yandex" }, "https://ya.ru/"),
            (new[] { "гугл", "google" }, "https://www.google.com/"),
            (new[] { "твич", "twitch" }, "https://www.twitch.tv/"),
            (new[] { "кион", "kion" }, "https://kion.ru/"),
            (new[] { "кинопоиск", "kinopoisk" }, "https://www.kinopoisk.ru/"),
            (new[] { "озон", "ozon" }, "https://www.ozon.ru/"),
            (new[] { "вайлдберриз", "wildberries" }, "https://www.wildberries.ru/")
        };

        foreach (var site in sites)
        {
            if (!site.Aliases.Any(alias => normalized.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                                           normalized.Contains(alias, StringComparison.OrdinalIgnoreCase))) continue;
            url = site.Url;
            return true;
        }

        url = "";
        return false;
    }

    private static bool ContainsAny(string value, params string[] options) =>
        options.Any(option => value.Contains(option, StringComparison.OrdinalIgnoreCase));
}
