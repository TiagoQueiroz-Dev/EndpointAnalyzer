using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Runtime;

public interface IAppRunner
{
    /// <summary>Compila o projeto do endpoint, executa o serviço em localhost e aguarda ele responder.</summary>
    Task<RunningApp> StartAsync(LoadedSolution solution, EndpointInfo endpoint, Action<string> log, CancellationToken cancellationToken = default);
}

public class RuntimeStartException(string message) : Exception(message);

/// <summary>Serviço em execução usado pela validação. Ao descartar, encerra o processo e apaga a pasta do build temporário.</summary>
public sealed class RunningApp : IAsyncDisposable
{
    private readonly Process _process;
    private readonly OutputBuffer _output;
    private readonly string _buildDirectory;
    private readonly EventHandler _onExit;

    internal RunningApp(Uri baseUrl, Process process, OutputBuffer output, TimeSpan requestTimeout, string buildDirectory)
    {
        BaseUrl = baseUrl;
        _process = process;
        _output = output;
        _buildDirectory = buildDirectory;
        Client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = baseUrl, Timeout = requestTimeout };
        // Se o analisador cair, o serviço iniciado não fica órfão nem deixa a pasta do build para trás.
        _onExit = (_, _) => Shutdown();
        AppDomain.CurrentDomain.ProcessExit += _onExit;
    }

    public Uri BaseUrl { get; }

    public HttpClient Client { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>Últimas linhas do console do serviço (diagnóstico de falhas).</summary>
    public string OutputTail => _output.Tail();

    public ValueTask DisposeAsync()
    {
        AppDomain.CurrentDomain.ProcessExit -= _onExit;
        Client.Dispose();
        Shutdown();
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Encerra o processo e espera ele sair (o executável fica travado até lá) antes de apagar a pasta.</summary>
    private void Shutdown()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        TempBuild.Delete(_buildDirectory);
    }
}

/// <summary>
/// Pasta exclusiva do build de uma execução (%TEMP%/EndpointAnalyzer/runtime/{pid do analisador}-{id}): o serviço roda
/// dessa cópia e não disputa o bin/ com a instância que o usuário estiver executando.
/// </summary>
internal static class TempBuild
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "runtime");

    public static string Create()
    {
        DeleteOrphans();
        var directory = Path.Combine(Root, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Apaga a pasta inteira; tenta de novo enquanto o Windows libera os arquivos do processo encerrado.</summary>
    public static void Delete(string directory)
    {
        for (var attempt = 1; Directory.Exists(directory); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(300);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    /// <summary>Pastas de analisadores que caíram sem limpar (processo dono não existe mais).</summary>
    private static void DeleteOrphans()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var owner = Path.GetFileName(directory).Split('-')[0];
            if (int.TryParse(owner, out var pid) && !IsRunning(pid)) Delete(directory);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class OutputBuffer
{
    private const int MaxLines = 80;
    private readonly Queue<string> _lines = new();

    public void Add(string? line)
    {
        if (line is null) return;
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
    }

    public string Tail(int lines = 25)
    {
        lock (_lines) return string.Join("\n", _lines.TakeLast(lines));
    }
}

/// <summary>
/// AppRunner: <c>dotnet build</c> do projeto numa pasta temporária exclusiva e execução dessa cópia em
/// http://localhost:{porta livre}. A pasta é apagada quando o serviço é encerrado.
/// Ambiente, configuração e conexão com o banco são os do próprio projeto (responsabilidade do usuário).
/// </summary>
public class AppRunner(RuntimeOptions options) : IAppRunner
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public async Task<RunningApp> StartAsync(LoadedSolution solution, EndpointInfo endpoint, Action<string> log, CancellationToken cancellationToken = default)
    {
        var projectPath = ProjectPath(solution, endpoint);
        var projectDir = Path.GetDirectoryName(projectPath)!;

        // Build fora do bin/ do projeto: a instância que o usuário estiver rodando trava os arquivos de lá.
        var buildDirectory = TempBuild.Create();
        RunningApp? app = null;
        try
        {
            log($"Compilando {Path.GetFileName(projectPath)} em pasta temporária...");
            // Validação do token e análise podem subir o mesmo projeto ao mesmo tempo: um build por vez (o obj/ é compartilhado).
            (int ExitCode, string Output) build;
            await BuildLock.WaitAsync(cancellationToken);
            try
            {
                build = await RunToEndAsync("dotnet", ["build", projectPath, "-c", "Debug", "-o", buildDirectory, "-nologo", "-v", "q"],
                    projectDir, TimeSpan.FromMinutes(10), cancellationToken);
            }
            finally
            {
                BuildLock.Release();
            }
            if (build.ExitCode != 0)
                throw new RuntimeStartException($"Falha ao compilar {Path.GetFileName(projectPath)}:\n{Tail(build.Output, 20)}");

            var output = OutputFile(solution, projectPath, buildDirectory);
            var url = $"http://localhost:{FreePort()}";
            var executable = OperatingSystem.IsWindows() ? Path.ChangeExtension(output, ".exe") : Path.ChangeExtension(output, null);
            var info = File.Exists(executable) ? new ProcessStartInfo(executable) : new ProcessStartInfo("dotnet") { ArgumentList = { output } };
            info.ArgumentList.Add("--urls");
            info.ArgumentList.Add(url);
            // Igual à IDE: a pasta do projeto é o content root (appsettings e arquivos lidos por caminho relativo).
            info.WorkingDirectory = projectDir;
            // O ambiente do analisador (ex.: Development do launchSettings dele) não vai para o serviço:
            // ele sobe como subiria executado direto.
            info.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            info.Environment.Remove("DOTNET_ENVIRONMENT");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;

            var console = new OutputBuffer();
            Process process;
            try
            {
                process = Process.Start(info) ?? throw new RuntimeStartException($"Não foi possível executar {info.FileName}.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new RuntimeStartException($"Não foi possível executar {info.FileName}: {e.Message}");
            }
            app = new RunningApp(new Uri(url + "/"), process, console, TimeSpan.FromSeconds(options.RequestTimeoutSeconds), buildDirectory);
            process.OutputDataReceived += (_, e) => console.Add(e.Data);
            process.ErrorDataReceived += (_, e) => console.Add(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            log($"Executando {Path.GetFileName(info.FileName == "dotnet" ? output : executable)} em {url}...");
            await WaitReadyAsync(app, TimeSpan.FromSeconds(options.StartupTimeoutSeconds), cancellationToken);
            log("Serviço disponível.");
            return app;
        }
        catch
        {
            // Sem serviço no ar, a pasta temporária não sobrevive à falha (o descarte do app encerra o processo e a apaga).
            if (app is not null) await app.DisposeAsync();
            else TempBuild.Delete(buildDirectory);
            throw;
        }
    }

    /// <summary>Qualquer resposta HTTP (inclusive 404) indica que o serviço está atendendo.</summary>
    private static async Task WaitReadyAsync(RunningApp app, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        using var probe = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = app.BaseUrl, Timeout = TimeSpan.FromSeconds(5) };
        string? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (app.HasExited)
                throw new RuntimeStartException($"O serviço encerrou durante a inicialização:\n{app.OutputTail}");
            try
            {
                using var response = await probe.GetAsync("", cancellationToken);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = e.Message;
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new RuntimeStartException($"O serviço não respondeu em {timeout.TotalSeconds:0}s ({lastError}).\n{app.OutputTail}");
    }

    /// <summary>Runtime:Project, se informado; senão o projeto onde o endpoint foi encontrado.</summary>
    private string ProjectPath(LoadedSolution solution, EndpointInfo endpoint)
    {
        if (!string.IsNullOrWhiteSpace(options.Project))
        {
            var configured = Path.GetFullPath(Path.IsPathRooted(options.Project) ? options.Project : Path.Combine(solution.RootDirectory, options.Project));
            return File.Exists(configured) ? configured : throw new RuntimeStartException($"Projeto configurado em Runtime:Project não encontrado: {configured}");
        }

        return solution.Solution.Projects.FirstOrDefault(p => p.Name == endpoint.Project)?.FilePath is { } path && File.Exists(path)
            ? path
            : throw new RuntimeStartException($"Projeto do endpoint ({endpoint.Project}) não encontrado na solução.");
    }

    /// <summary>Dll gerada pelo build na pasta temporária ({Assembly}.dll).</summary>
    private static string OutputFile(LoadedSolution solution, string projectPath, string buildDirectory)
    {
        var project = solution.Solution.Projects.FirstOrDefault(p => p.FilePath is { } f
            && string.Equals(Path.GetFullPath(f), projectPath, StringComparison.OrdinalIgnoreCase));
        var name = (project?.OutputFilePath is { } output ? Path.GetFileNameWithoutExtension(output)
            : project?.AssemblyName ?? Path.GetFileNameWithoutExtension(projectPath)) + ".dll";
        var file = Path.Combine(buildDirectory, name);
        return File.Exists(file) ? file : throw new RuntimeStartException($"{name} não encontrado em {buildDirectory} depois do build.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Tail(string text, int lines) => string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines));

    private static async Task<(int ExitCode, string Output)> RunToEndAsync(string file, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new RuntimeStartException($"Não foi possível executar {file}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new RuntimeStartException($"{file} não terminou em {timeout.TotalMinutes:0} minuto(s).");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
