using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesmodAssistant.Models;

namespace DesmodAssistant.Services;

public sealed class GoogleAuthService
{
    private readonly HttpClient _http = new();

    public async Task<UserSession> SignInAsync(string clientId, Func<string, string> roleResolver, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Сначала укажите Google OAuth Client ID в настройках");
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var port = GetFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();
        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["access_type"] = "online",
            ["prompt"] = "select_account"
        }.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var responseHtml = "<html><body style='font-family:Segoe UI;background:#0a0d16;color:white;text-align:center;padding:80px'><h2>Вход выполнен</h2><p>Можно закрыть эту вкладку и вернуться в Desmod Assistant.</p></body></html>";
        var responseBytes = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, timeout.Token);
        context.Response.Close();

        var query = context.Request.QueryString;
        if (query["state"] != state) throw new InvalidOperationException("Проверка Google state не пройдена");
        var code = query["code"] ?? throw new InvalidOperationException(query["error"] ?? "Google не вернул код входа");
        using var tokenResponse = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        }), timeout.Token);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(timeout.Token));
        var idToken = tokenDocument.RootElement.GetProperty("id_token").GetString() ?? throw new InvalidOperationException("Google не вернул ID token");
        var infoJson = await _http.GetStringAsync("https://oauth2.googleapis.com/tokeninfo?id_token=" + Uri.EscapeDataString(idToken), timeout.Token);
        using var info = JsonDocument.Parse(infoJson);
        var email = info.RootElement.GetProperty("email").GetString() ?? throw new InvalidOperationException("Email не получен");
        var verified = info.RootElement.TryGetProperty("email_verified", out var verifiedElement) && verifiedElement.GetString() == "true";
        if (!verified) throw new InvalidOperationException("Google email не подтверждён");
        var name = info.RootElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? email : email;
        return new UserSession(email, name, roleResolver(email));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
