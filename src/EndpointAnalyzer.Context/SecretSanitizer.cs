using System.Text.RegularExpressions;

namespace EndpointAnalyzer.Context;

/// <summary>
/// Remove valores sensíveis (senhas, tokens, connection strings, chaves) antes de enviar código para a IA.
/// </summary>
public static partial class SecretSanitizer
{
    private const string Mask = "***";

    public static string Sanitize(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;

        code = PrivateKeyRegex().Replace(code, "-----PRIVATE KEY REMOVIDA-----");
        code = ConnectionStringRegex().Replace(code, "\"***connection string removida***\"");
        code = SensitiveAssignmentRegex().Replace(code, m => $"{m.Groups["name"].Value}\"{Mask}\"");
        code = KeyValueRegex().Replace(code, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
        code = BearerRegex().Replace(code, $"Bearer {Mask}");
        code = JwtRegex().Replace(code, Mask);
        code = ApiKeyRegex().Replace(code, Mask);
        code = AwsKeyRegex().Replace(code, Mask);
        return code;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyRegex();

    // "Server=...;Database=...;Password=..."
    [GeneratedRegex(@"""[^""\r\n]*(?:Server|Data Source|Host|AccountKey|SharedAccessKey)\s*=[^""\r\n]*""", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();

    // Senha = "abc", ApiKey: "abc", private const string Token = "abc"
    [GeneratedRegex(@"(?<name>\b\w*(?:password|passwd|senha|secret|segredo|token|apikey|api_key|connectionstring|privatekey|credential)\w*\s*[=:]\s*)""[^""\r\n]*""", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveAssignmentRegex();

    // password=abc; pwd=abc
    [GeneratedRegex(@"(?<key>\b(?:password|pwd|secret|token|apikey|api_key|access_key|client_secret)\b)(?<sep>\s*=\s*)[^;""'\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]{16,}=*")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();
}
