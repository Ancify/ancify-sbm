namespace Ancify.SBM.Shared.Model;

public class AuthContext
{
    public string? UserId { get; set; }
    public List<string> Roles { get; set; } = [];
    public bool Success { get; set; }
    public object? SessionData { get; set; }

    public AuthContext()
    {
        Success = false;
    }

    public AuthContext(string userId, List<string>? roles = null, object? sessionData = null)
    {
        UserId = userId;
        Roles = roles ?? [];
        Success = true;
        SessionData = sessionData;
    }
}
