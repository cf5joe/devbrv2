using System.Text.RegularExpressions;

namespace DevBR.Discovery.Support;

/// <summary>
/// Recognizes common credential shapes so they are excluded by default and never stored in the
/// catalog. Detection is best effort: "nothing recognized" never means "secret-free".
/// </summary>
public static partial class SecretDetector
{
    private static readonly string[] SecretNameParts =
    [
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "APIKEY", "API_KEY", "ACCESS_KEY", "PRIVATE_KEY",
        "CREDENTIAL", "CONNECTION_STRING", "CONNSTR", "CLIENT_SECRET", "AUTH", "_PAT", "SAS_KEY", "SESSION_KEY",
    ];

    public static bool IsSecretName(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper is "AUTHOR" or "AUTHORS" || upper.EndsWith("_PATH", StringComparison.Ordinal) || upper.EndsWith("_DIR", StringComparison.Ordinal) || upper.EndsWith("_HOME", StringComparison.Ordinal))
        {
            return false;
        }

        return SecretNameParts.Any(part => upper.Contains(part, StringComparison.Ordinal));
    }

    public static bool LooksLikeSecretValue(string? value)
        => !string.IsNullOrEmpty(value) && SecretValuePattern().IsMatch(value);

    public static bool IsSecret(string name, string? value) => IsSecretName(name) || LooksLikeSecretValue(value);

    /// <summary>Removes credentials embedded in URLs, e.g. https://user:token@host → https://user:***@host.</summary>
    public static string RedactUrl(string url)
    {
        var redacted = UrlUserInfoPattern().Replace(url, match =>
        {
            var user = match.Groups["user"].Value;
            var hasPassword = match.Groups["password"].Success;
            if (hasPassword)
            {
                return $"{match.Groups["scheme"].Value}{user}:***@";
            }

            return LooksLikeSecretValue(user) || user.Length >= 20 ? $"{match.Groups["scheme"].Value}***@" : match.Value;
        });

        return QueryTokenPattern().Replace(redacted, "${key}=***");
    }

    public static bool UrlHasCredentials(string url) => RedactUrl(url) != url;

    [GeneratedRegex(@"(ghp_|gho_|ghu_|ghs_|ghr_|github_pat_)[A-Za-z0-9_]{16,}|\bsk-(ant-|proj-)?[A-Za-z0-9_\-]{16,}|\bxox[abpr]-[A-Za-z0-9\-]{10,}|\bAKIA[0-9A-Z]{16}\b|-----BEGIN [A-Z ]*PRIVATE KEY-----|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.|\bAIza[0-9A-Za-z_\-]{30,}|\bglpat-[A-Za-z0-9_\-]{16,}|\bnpm_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex SecretValuePattern();

    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.\-]*://)(?<user>[^:@/\s]+)(:(?<password>[^@/\s]*))?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();

    [GeneratedRegex(@"(?<key>[?&](access_token|token|api_key|apikey|key|sig|signature|password))=[^&\s#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryTokenPattern();
}
