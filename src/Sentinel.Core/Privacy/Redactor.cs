using System.Text.RegularExpressions;

namespace Sentinel.Core.Privacy;

/// <summary>
/// Removes personally identifying information from free text (logs, reports, AI context, support bundles).
/// Conservative by design: it prefers over-redaction to leaking.
/// </summary>
public static partial class Redactor
{
    private static readonly string UserName = Environment.UserName;
    private static readonly string MachineName = Environment.MachineName;
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{1,4}\b")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\Users\\[^\\\s""']+")]
    private static partial Regex UsersPath();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"S-1-5-21-\d+-\d+-\d+-\d+")]
    private static partial Regex Sid();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var s = text;
        if (!string.IsNullOrEmpty(UserProfile)) s = s.Replace(UserProfile, @"%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        s = UsersPath().Replace(s, @"C:\Users\<user>");
        s = Email().Replace(s, "<email>");
        s = Mac().Replace(s, "<mac>");
        s = Guid().Replace(s, "<guid>");
        s = Sid().Replace(s, "<sid>");
        s = Ipv4().Replace(s, m => IsVersionLike(m.Value) ? m.Value : "<ip>");
        s = Ipv6().Replace(s, "<ipv6>");
        if (UserName.Length >= 3) s = Regex.Replace(s, $@"\b{Regex.Escape(UserName)}\b", "<user>", RegexOptions.IgnoreCase);
        if (MachineName.Length >= 3) s = Regex.Replace(s, $@"\b{Regex.Escape(MachineName)}\b", "<computer>", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>Masks an identifier for display, keeping a short suffix for recognition.</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var v = value.Trim();
        return v.Length <= 4 ? "••••" : new string('•', Math.Min(8, v.Length - 4)) + v[^4..];
    }

    // Four-part dotted numbers in driver/file versions (e.g. 31.0.15.4601) are not IP addresses.
    private static bool IsVersionLike(string candidate)
    {
        var parts = candidate.Split('.');
        return parts.Length == 4 && parts.Any(p => int.TryParse(p, out var n) && n > 255);
    }

    /// <summary>Strips control characters and bounds length for untrusted strings from WMI, device metadata, event logs.</summary>
    public static string SanitizeUntrusted(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\t')) continue;
            if (ch is '\u202E' or '\u202D' or '\u200E' or '\u200F') continue; // bidi overrides
            buffer[n++] = ch;
        }
        var s = new string(buffer[..n]).Trim();
        return s.Length > maxLength ? s[..maxLength] + "…" : s;
    }
}
