using System.Text.Json.Serialization;

namespace PQA.Web.Models;

public sealed class UserSession
{
    [JsonIgnore] public string Password { get; init; } = "";
    public string PersonName { get; init; } = "";
    public string Department { get; init; } = "";
    public bool IsSupervisor { get; init; }
}

public sealed record LoginRequest(string Password);
public sealed record LoginResponse(string Token, UserSession User);

public sealed class WorkflowDocument
{
    public string CaseNumber { get; set; } = "";
    public Dictionary<string, string?> Fields { get; set; } = new();
    public Dictionary<string, string?> Details { get; set; } = new();
    public List<Dictionary<string, string?>> Items { get; set; } = new();
    public List<AttachmentInfo> Attachments { get; set; } = new();
}

public sealed record AttachmentInfo(int Sequence, string FileName);
