using System.Diagnostics;
using System.Text;

namespace EndpointAnalyzer.Api;

/// <summary>
/// Abre a janela nativa "Abrir arquivo" do Windows na máquina onde a API está rodando.
/// O navegador não informa o caminho completo de um arquivo escolhido; como o analisador roda localmente,
/// quem abre a janela é o servidor.
/// </summary>
public static class FileDialog
{
    /// <summary>Caminho escolhido, ou null se o usuário cancelou.</summary>
    /// <exception cref="PlatformNotSupportedException">Fora do Windows.</exception>
    public static async Task<string?> OpenSolutionAsync(string? initialPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A seleção de arquivo pelo explorador só está disponível no Windows.");

        var initialDirectory = InitialDirectory(initialPath);
        var initialFile = initialPath is not null && File.Exists(initialPath) ? Path.GetFileName(initialPath) : "";

        // Janela "TopMost" como dona do diálogo: sem ela o diálogo pode abrir atrás do navegador.
        var script = $$"""
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            Add-Type -AssemblyName System.Windows.Forms
            $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false; WindowState = 'Minimized' }
            $dialog = New-Object System.Windows.Forms.OpenFileDialog
            $dialog.Title = 'Selecionar solução ou projeto .NET'
            $dialog.Filter = 'Soluções e projetos .NET (*.sln;*.slnx;*.csproj)|*.sln;*.slnx;*.csproj|Todos os arquivos (*.*)|*.*'
            $dialog.InitialDirectory = '{{Escape(initialDirectory)}}'
            $dialog.FileName = '{{Escape(initialFile)}}'
            $dialog.RestoreDirectory = $true
            if ($dialog.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($dialog.FileName) }
            $owner.Dispose()
            """;

        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível abrir o explorador de arquivos.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        var path = (await output).Trim();
        return path.Length == 0 ? null : path;
    }

    private static string InitialDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory)) return directory;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // Aspas simples do PowerShell: ' vira ''.
    private static string Escape(string value) => value.Replace("'", "''");
}
