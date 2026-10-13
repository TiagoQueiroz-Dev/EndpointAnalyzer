using System.Collections.Concurrent;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

/// <summary>Conta indisponível: o motivo e até quando ela não é tentada de novo.</summary>
public sealed record AiAccountBlock(AiUnavailableReason Reason, string Message, DateTimeOffset Since, DateTimeOffset Until);

/// <summary>
/// Estado das contas da IA entre as análises (Claude por conta, Codex): quais estão sem uso disponível ou com a sessão
/// expirada — para não repetir chamadas numa conta que já recusou — e quais estão em uso por uma análise em andamento.
/// O bloqueio vale até a renovação do limite (quando conhecida) ou por <see cref="DefaultBlock"/>.
/// </summary>
public sealed class AiAvailability(TimeProvider? time = null)
{
    public static readonly TimeSpan DefaultBlock = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, AiAccountBlock> _blocks = new();
    private readonly ConcurrentDictionary<string, int> _active = new();

    public DateTimeOffset Now => _time.GetUtcNow();

    public void Block(string key, AiUnavailableReason reason, string message, DateTimeOffset? until = null)
    {
        var now = Now;
        _blocks[key] = new AiAccountBlock(reason, message, now, until is { } at && at > now ? at : now + DefaultBlock);
    }

    public void Clear(string key) => _blocks.TryRemove(key, out _);

    /// <summary>Bloqueio em vigor (o que venceu é descartado).</summary>
    public AiAccountBlock? Blocked(string key)
    {
        if (!_blocks.TryGetValue(key, out var block)) return null;
        if (block.Until > Now) return block;
        _blocks.TryRemove(KeyValuePair.Create(key, block));
        return null;
    }

    public bool IsAvailable(string key) => Blocked(key) is null;

    public bool InUse(string key) => _active.TryGetValue(key, out var count) && count > 0;

    internal void Activate(string key) => _active.AddOrUpdate(key, 1, (_, count) => count + 1);

    internal void Deactivate(string key) => _active.AddOrUpdate(key, 0, (_, count) => Math.Max(0, count - 1));
}

/// <summary>Conta candidata na ordem de uso: uma conta Claude, a API do Claude ou o Codex.</summary>
/// <param name="Key">Identifica a conta no <see cref="AiAvailability"/> ("claude:principal", "codex").</param>
/// <param name="Label">Como a conta aparece na interface e no relatório (e-mail ou apelido).</param>
public sealed record AiCandidate(string Key, string Provider, string? AccountId, string Label, Func<IAiProvider> Create);

/// <summary>
/// IA da análise com troca automática de conta. As chamadas são autocontidas (instruções, contexto do código,
/// evidências e resultados parciais vão no próprio pedido), então, quando a conta em uso atinge o limite de uso ou perde a
/// sessão, só a chamada que falhou é repetida, com o mesmo pedido, na próxima conta disponível: o que a análise já
/// concluiu (em memória) não é refeito. Outras falhas não trocam de conta. Sem próxima conta, a falha tipada sobe
/// (<see cref="AiProviderUnavailableException"/>) e quem orquestra preserva o progresso.
/// </summary>
public sealed class FailoverAiProvider : IAiProvider, IDisposable
{
    private readonly IReadOnlyList<AiCandidate> _candidates;
    private readonly AiAvailability _availability;
    private readonly IAiProvider?[] _providers;
    private readonly Dictionary<int, AiRunAccount> _used = [];
    private readonly object _gate = new();
    private int _current;
    private int _last;
    private int _successes;
    private bool _disposed;

    private FailoverAiProvider(IReadOnlyList<AiCandidate> candidates, AiAvailability availability, int first)
    {
        _candidates = candidates;
        _availability = availability;
        _providers = new IAiProvider?[candidates.Count];
        foreach (var skipped in candidates.Take(first))
            if (availability.Blocked(skipped.Key) is { } block)
                Run.Notes.Add($"{Name(skipped)} pulada: {Describe(block)}.");
        _current = first;
        Use(first);
    }

    /// <summary>Começa pela primeira conta disponível, na ordem dada.</summary>
    /// <exception cref="AiProviderUnavailableException">Nenhuma conta disponível.</exception>
    public static FailoverAiProvider Start(IReadOnlyList<AiCandidate> candidates, AiAvailability availability)
    {
        if (candidates.Count == 0) throw new ArgumentException("Informe ao menos uma conta da IA.", nameof(candidates));
        var first = candidates.Select((c, i) => (c, i)).FirstOrDefault(x => availability.IsAvailable(x.c.Key), (null!, -1)).Item2;
        if (first < 0) throw ExhaustedError(candidates, availability);
        return new FailoverAiProvider(candidates, availability, first);
    }

    /// <summary>Um provedor só (sem outras contas): mesmo registro de uso, sem troca.</summary>
    public static FailoverAiProvider Single(IAiProvider provider) => Start(
        [new AiCandidate(provider.Provider, provider.Provider, null, AiProviders.DisplayName(provider.Provider), () => provider)], new AiAvailability());

    /// <summary>Contas usadas, trocas e notas (vai para o relatório).</summary>
    public AiRunInfo Run { get; } = new();

    /// <summary>A conta em uso mudou (troca ou esgotamento): para o acompanhamento da análise na interface.</summary>
    public event Action? Changed;

    /// <summary>Chamadas respondidas, em qualquer conta.</summary>
    public int SuccessfulCalls => Volatile.Read(ref _successes);

    public bool Exhausted
    {
        get { lock (_gate) return _current < 0; }
    }

    /// <summary>Conta em uso (ou a última usada, se todas esgotaram).</summary>
    public AiCandidate Current
    {
        get { lock (_gate) return _candidates[_current < 0 ? _last : _current]; }
    }

    public string Provider => Current.Provider;

    /// <summary>Cópia das trocas (a lista do <see cref="Run"/> muda enquanto a análise roda).</summary>
    public IReadOnlyList<AiHandoff> HandoffsSnapshot()
    {
        lock (_gate) return Run.Handoffs.ToList();
    }

    public string Model
    {
        get { lock (_gate) return ProviderAt(_current < 0 ? _last : _current).Model; }
    }

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, AnalysisSections.All, cancellationToken);

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, AnalysisSections sections, CancellationToken cancellationToken = default) =>
        CallAsync("documentação", ai => ai.AnalyzeAsync(context, sections, cancellationToken), cancellationToken);

    public Task<System.Text.Json.JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default) =>
        CallAsync(request.Purpose, ai => ai.CompleteJsonAsync(request, cancellationToken), cancellationToken);

    private async Task<T> CallAsync<T>(string step, Func<IAiProvider, Task<T>> call, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Cancelar durante a troca impede a chamada na próxima conta.
            cancellationToken.ThrowIfCancellationRequested();
            int index;
            IAiProvider provider;
            lock (_gate)
            {
                if (_current < 0) throw ExhaustedError(_candidates, _availability);
                index = _current;
                provider = ProviderAt(index);
            }
            try
            {
                var result = await call(provider);
                lock (_gate)
                {
                    _successes++;
                    _used[index].Calls++;
                }
                return result;
            }
            catch (AiProviderUnavailableException e) when (!cancellationToken.IsCancellationRequested)
            {
                Fail(index, step, e);
            }
        }
    }

    /// <summary>Bloqueia a conta e, se ela ainda é a atual (chamadas paralelas falham juntas), passa para a próxima.</summary>
    private void Fail(int index, string step, AiProviderUnavailableException e)
    {
        lock (_gate)
        {
            var failed = _candidates[index];
            _availability.Block(failed.Key, e.Reason, e.Message);
            if (_current != index) return;
            _availability.Deactivate(failed.Key);
            var next = -1;
            for (var i = index + 1; i < _candidates.Count && next < 0; i++)
                if (_availability.IsAvailable(_candidates[i].Key)) next = i;
            var to = next < 0 ? null : _candidates[next];
            Run.Handoffs.Add(new AiHandoff
            {
                At = _availability.Now, Step = step, FromProvider = failed.Provider, FromAccount = failed.Label,
                ToProvider = to?.Provider, ToAccount = to?.Label, Reason = ReasonOf(e.Reason), Message = e.Message,
            });
            _current = next;
            if (next >= 0) Use(next);
        }
        Changed?.Invoke();
    }

    private void Use(int index)
    {
        _last = index;
        _availability.Activate(_candidates[index].Key);
        if (_used.ContainsKey(index)) return;
        var candidate = _candidates[index];
        var account = new AiRunAccount { Provider = candidate.Provider, AccountId = candidate.AccountId, Label = candidate.Label, Model = ProviderAt(index).Model };
        _used[index] = account;
        Run.Accounts.Add(account);
    }

    private IAiProvider ProviderAt(int index) => _providers[index] ??= _candidates[index].Create();

    public static string ReasonOf(AiUnavailableReason reason) =>
        reason == AiUnavailableReason.UsageLimit ? AiHandoffReasons.UsageLimit : AiHandoffReasons.Authentication;

    private static string Name(AiCandidate candidate) =>
        candidate.Label == AiProviders.DisplayName(candidate.Provider) ? candidate.Label : $"{AiProviders.DisplayName(candidate.Provider)} ({candidate.Label})";

    private static string Describe(AiAccountBlock block) => block.Reason == AiUnavailableReason.UsageLimit
        ? $"limite de uso até {block.Until.ToLocalTime():dd/MM HH:mm}"
        : "sessão expirada, entre novamente";

    /// <summary>A falha de "nenhuma conta disponível" desta ordem de contas, com o estado atual de cada uma.</summary>
    public AiProviderUnavailableException Exhaustion() => ExhaustedError(_candidates, _availability);

    /// <summary>Nenhuma conta da ordem tem uso disponível: a mensagem lista cada uma e até quando.</summary>
    public static AiProviderUnavailableException ExhaustedError(IReadOnlyList<AiCandidate> candidates, AiAvailability availability)
    {
        var blocks = candidates.Select(c => (Candidate: c, Block: availability.Blocked(c.Key))).ToList();
        var reason = blocks.Any(b => b.Block?.Reason != AiUnavailableReason.Authentication) ? AiUnavailableReason.UsageLimit : AiUnavailableReason.Authentication;
        var accounts = string.Join("; ", blocks.Select(b => $"{Name(b.Candidate)}: {(b.Block is { } block ? Describe(block) : "indisponível")}"));
        return new AiProviderUnavailableException(candidates[0].Provider, reason,
            $"Nenhuma conta da IA com uso disponível para continuar ({accounts}). O progresso já feito foi preservado; "
            + "continue quando o uso renovar ou conecte outra conta.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_current >= 0) _availability.Deactivate(_candidates[_current].Key);
        }
    }
}
