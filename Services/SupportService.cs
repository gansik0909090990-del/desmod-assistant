using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesmodAssistant.Models;

namespace DesmodAssistant.Services;

public sealed class SupportService
{
    private const string OwnerHash = "423c7e2d017ce05b69a102b1c2e6a6dadc1454095635d6bb1d4d39ea500b4736";
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesmodAssistant", "Support");
    private string TicketsPath => Path.Combine(_directory, "tickets.json");
    private string RolesPath => Path.Combine(_directory, "roles.json");

    public string ResolveRole(string email)
    {
        var hash = HashEmail(email);
        if (hash == OwnerHash) return "owner";
        return LoadRoles().TryGetValue(hash, out var role) ? role : "user";
    }

    public IReadOnlyList<SupportTicket> GetTickets(UserSession session)
    {
        var tickets = LoadTickets();
        return session.CanModerate
            ? tickets.OrderByDescending(t => t.CreatedAt).ToList()
            : tickets.Where(t => t.UserEmail.Equals(session.Email, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.CreatedAt).ToList();
    }

    public SupportTicket CreateTicket(UserSession session, string subject, string description)
    {
        var tickets = LoadTickets();
        var ticket = new SupportTicket { UserEmail = session.Email, Subject = subject.Trim(), Description = description.Trim() };
        tickets.Add(ticket);
        SaveTickets(tickets);
        return ticket;
    }

    public void UpdateTicket(UserSession session, string id, string status, string reply)
    {
        if (!session.CanModerate) throw new UnauthorizedAccessException("Недостаточно прав");
        var tickets = LoadTickets();
        var ticket = tickets.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("Обращение не найдено");
        ticket.Status = status;
        ticket.ModeratorReply = reply.Trim();
        SaveTickets(tickets);
    }

    public void GrantRole(UserSession owner, string email, string role)
    {
        if (!owner.IsOwner) throw new UnauthorizedAccessException("Роли может выдавать только владелец");
        if (role is not ("user" or "moderator" or "admin")) throw new ArgumentOutOfRangeException(nameof(role));
        var roles = LoadRoles();
        roles[HashEmail(email)] = role;
        SaveRoles(roles);
    }

    private List<SupportTicket> LoadTickets() => LoadJson(TicketsPath, new List<SupportTicket>());
    private Dictionary<string, string> LoadRoles() => LoadJson(RolesPath, new Dictionary<string, string>());
    private void SaveTickets(List<SupportTicket> tickets) => SaveJson(TicketsPath, tickets);
    private void SaveRoles(Dictionary<string, string> roles) => SaveJson(RolesPath, roles);

    private static T LoadJson<T>(string path, T fallback)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback : fallback; }
        catch { return fallback; }
    }

    private void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string HashEmail(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();
}
