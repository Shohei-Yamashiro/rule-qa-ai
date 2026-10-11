namespace RuleQA.Core;

/// <summary>
/// The signed-in user. The demo has no sign-in and always acts as an administrator; the screens still check
/// <see cref="IsAdmin"/> so that a real sign-in can be added later without changing them.
/// </summary>
public sealed record CurrentUser(string Name, bool IsAdmin)
{
    public static readonly CurrentUser DemoAdmin = new("管理者（デモ）", true);
}
