namespace Ancify.SBM.Shared.Model;

public class AuthContext
{
    public string? UserId { get; set; }
    public List<string> Roles { get; set; } = [];
    public string? Scope { get; set; }
    public bool Success { get; set; }
    public object? SessionData { get; set; }

    public AuthContext()
    {
        Success = false;
    }

    public AuthContext(string userId, List<string>? roles = null, string? scope = null, object? sessionData = null)
    {
        UserId = userId;
        Roles = roles ?? [];
        Scope = scope;
        Success = true;
        SessionData = sessionData;
    }
}
