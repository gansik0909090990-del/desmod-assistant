namespace DesmodAssistant.Models;

public sealed record UserSession(string Email, string Name, string Role)
{
    public bool CanModerate => Role is "owner" or "admin" or "moderator";
    public bool IsOwner => Role == "owner";
}

public sealed class SupportTicket
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string UserEmail { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Открыто";
    public string ModeratorReply { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public override string ToString() => $"[{Id}] {Status} — {Subject} · {UserEmail}";
}
