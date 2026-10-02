using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointAnalyzer.AI.ClaudeCode;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static partial class ProcessRunner
{
    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Executa o processo, envia <paramref name="input"/> no stdin e aguarda a saída.</summary>
    /// <exception cref="ClaudeCodeNotInstalledException">Executável não encontrado.</exception>
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo info, string? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Start(info);

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"O Claude Code não respondeu em {timeout.TotalMinutes:0} minuto(s).");
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }

    public static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new ClaudeCodeNotInstalledException(info.FileName);
        }
        catch (Win32Exception)
        {
            throw new ClaudeCodeNotInstalledException(info.FileName);
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Remove sequências ANSI/OSC (cores, hyperlinks de terminal).</summary>
    public static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}

public class ClaudeCodeNotInstalledException(string executable)
    : Exception($"Claude Code não encontrado ('{executable}'). Instale em https://claude.com/claude-code e tente novamente.");
