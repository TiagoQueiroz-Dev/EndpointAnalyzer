using System.Text.Json;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.Core.Interfaces;

namespace EndpointAnalyzer.Application;

/// <summary>Conta cadastrada de um provedor: o login fica no próprio diretório (CLAUDE_CONFIG_DIR no Claude, CODEX_HOME no Codex).</summary>
public sealed record AiAccount(string Id, string? Label, string Directory);

/// <summary>Estado de disponibilidade de uma conta na interface.</summary>
public static class AiAccountStates
{
    public const string Available = "disponivel";
    public const string InUse = "em-uso";
    public const string NoUsage = "sem-uso";
    public const string Authenticate = "autenticar";
    public const string LoggedOut = "desconectado";

    /// <summary>Claude logado com o Console (chave de API): não entra na ordem do plano mensal.</summary>
    public const string Console = "console";
}

/// <param name="Plan">Plano da conta (Claude: pro, max...; Codex: plus, pro...).</param>
/// <param name="Until">Até quando a conta não é tentada (limite de uso ou sessão expirada).</param>
/// <param name="Usage">Uso do plano, quando o status já o traz (Codex); o do Claude é lido à parte.</param>
public sealed record AiAccountState(string Id, string? Label, string Display, int Order, bool CliInstalled, bool LoggedIn,
    bool UsesSubscription, string? Email, string? Plan, string? OrgName, string? AuthMethod, bool LoginPending,
    string State, string? Message, DateTimeOffset? Until, AiUsage? Usage = null, string? LoginError = null);

/// <summary>Uma conta e os serviços do login dela.</summary>
public abstract class AiAccountServices(AiAccount account, string provider)
{
    public AiAccount Account { get; internal set; } = account;

    public string Provider => provider;

    /// <summary>Chave no <see cref="AiAvailability"/> ("claude:principal", "codex:a1b2c3d4").</summary>
    public string Key => $"{provider}:{Account.Id}";

    /// <summary>Apelido, e-mail ou "Conta 2" (nessa ordem).</summary>
    public string Display(string? email, int order) =>
        Account.Label ?? email ?? (Account.Id == AiAccountRegistry<AiAccountServices>.PrimaryId ? "Conta principal" : $"Conta {order}");
}

/// <summary>
/// Contas de um provedor, na ordem de uso. Cada uma tem o próprio diretório de login, então entrar ou sair de uma não afeta
/// as outras. A primeira reaproveita o login que o projeto já tinha (diretório configurado do provedor). A lista fica em
/// {pasta das contas}/accounts.json; as contas adicionais ganham uma subpasta ali.
/// </summary>
public abstract class AiAccountRegistry<TServices> where TServices : AiAccountServices
{
    public const string PrimaryId = "principal";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _accountsDirectory;
    private readonly string _primaryDirectory;
    private readonly object _gate = new();
    private readonly Dictionary<string, TServices> _services = [];
    private List<AiAccount> _accounts;

    protected AiAccountRegistry(string provider, string accountsDirectory, string primaryDirectory, AiAvailability availability)
    {
        Provider = provider;
        _accountsDirectory = accountsDirectory;
        _primaryDirectory = primaryDirectory;
        Availability = availability;
        _accounts = Load() ?? [new AiAccount(PrimaryId, null, primaryDirectory)];
    }

    public string Provider { get; }

    protected AiAvailability Availability { get; }

    private string StoreFile => Path.Combine(_accountsDirectory, "accounts.json");

    /// <summary>Serviços da conta; <paramref name="primaryDirectory"/>: o diretório configurado do provedor (login já existente).</summary>
    protected abstract TServices Create(AiAccount account, bool primaryDirectory);

    /// <summary>Sai da conta (na remoção; uma falha aqui não impede remover).</summary>
    protected abstract Task LogoutAsync(TServices services, CancellationToken cancellationToken);

    /// <summary>Contas na ordem de uso.</summary>
    public IReadOnlyList<TServices> All
    {
        get { lock (_gate) return _accounts.Select(ServicesOf).ToList(); }
    }

    /// <summary>A conta pedida ou, sem id, a primeira da ordem.</summary>
    /// <exception cref="KeyNotFoundException">Conta não cadastrada (404).</exception>
    public TServices Get(string? id)
    {
        lock (_gate)
        {
            var name = AiProviders.DisplayName(Provider);
            var account = string.IsNullOrWhiteSpace(id) ? _accounts.FirstOrDefault()
                : _accounts.FirstOrDefault(a => a.Id == id) ?? throw new KeyNotFoundException($"Conta do {name} não encontrada: {id}");
            return ServicesOf(account ?? throw new KeyNotFoundException($"Nenhuma conta do {name} cadastrada. Adicione uma conta."));
        }
    }

    /// <summary>Posição na ordem de uso (1, 2...).</summary>
    public int OrderOf(TServices services)
    {
        lock (_gate) return _accounts.FindIndex(a => a.Id == services.Account.Id) + 1;
    }

    /// <summary>Cadastra uma conta (ainda sem login), no fim da ordem.</summary>
    public TServices Add(string? label)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var account = new AiAccount(id, Clean(label), Path.Combine(_accountsDirectory, id));
            _accounts.Add(account);
            Save();
            return ServicesOf(account);
        }
    }

    public void Rename(string id, string? label)
    {
        lock (_gate)
        {
            var services = Get(id);
            services.Account = services.Account with { Label = Clean(label) };
            _accounts[_accounts.FindIndex(a => a.Id == id)] = services.Account;
            Save();
        }
    }

    /// <summary>Nova ordem de uso: os mesmos ids, sem repetir nem faltar.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            if (ids.Count != _accounts.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => _accounts.All(a => a.Id != id)))
                throw new ArgumentException("Informe todas as contas cadastradas, uma vez cada, na nova ordem.");
            _accounts = ids.Select(id => _accounts.First(a => a.Id == id)).ToList();
            Save();
        }
    }

    /// <summary>Sai da conta e a retira da ordem. O login só é apagado do disco nas pastas criadas para as contas adicionais.</summary>
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        if (Availability.InUse(services.Key))
            throw new InvalidOperationException("A conta está em uso por uma análise em andamento: aguarde a conclusão ou cancele a análise.");
        try { await LogoutAsync(services, cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        lock (_gate)
        {
            _accounts.RemoveAll(a => a.Id == id);
            _services.Remove(id);
            Save();
        }
        Availability.Clear(services.Key);
        if (Managed(services.Account.Directory) && Directory.Exists(services.Account.Directory))
            try { Directory.Delete(services.Account.Directory, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Limite atingido bloqueia a conta até a renovação. Uso disponível só desfaz um bloqueio por limite numa leitura nova
    /// (o plano renovou antes do previsto): um valor em cache, anterior à falha, não libera a conta.
    /// </summary>
    protected void ApplyUsage(TServices services, AiUsage? usage, bool fresh)
    {
        if (usage?.LimitReached == true)
        {
            if (Availability.Blocked(services.Key)?.Reason != AiUnavailableReason.UsageLimit)
                Availability.Block(services.Key, AiUnavailableReason.UsageLimit, $"Limite de uso do {AiProviders.DisplayName(Provider)} atingido.", Renewal(usage));
        }
        else if (fresh && usage is not null && Availability.Blocked(services.Key)?.Reason == AiUnavailableReason.UsageLimit)
            Availability.Clear(services.Key);
    }

    /// <summary>Quando o limite atingido renova (a janela cheia que renova por último).</summary>
    private static DateTimeOffset? Renewal(AiUsage usage) =>
        new[] { usage.Primary, usage.Secondary }.Where(w => w is { UsedPercent: >= 100, ResetsAt: not null }).Select(w => w!.ResetsAt).Max()
        ?? new[] { usage.Primary, usage.Secondary }.Where(w => w?.ResetsAt is not null).Select(w => w!.ResetsAt).Min();

    /// <summary>Disponível, em uso, sem uso, entre de novo, desconectada (ou conta do Console, no Claude).</summary>
    protected string StateOf(TServices services, bool loggedIn, bool usesSubscription)
    {
        var block = Availability.Blocked(services.Key);
        return !loggedIn ? AiAccountStates.LoggedOut
            : block?.Reason == AiUnavailableReason.Authentication ? AiAccountStates.Authenticate
            : block is not null ? AiAccountStates.NoUsage
            : !usesSubscription ? AiAccountStates.Console
            : Availability.InUse(services.Key) ? AiAccountStates.InUse
            : AiAccountStates.Available;
    }

    private TServices ServicesOf(AiAccount account)
    {
        if (_services.TryGetValue(account.Id, out var existing)) return existing;
        return _services[account.Id] = Create(account, SamePath(account.Directory, _primaryDirectory));
    }

    private bool Managed(string directory)
    {
        var root = Path.GetFullPath(_accountsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? Clean(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 80)];

    private List<AiAccount>? Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return null;
            var stored = JsonSerializer.Deserialize<StoredAccounts>(File.ReadAllText(StoreFile), JsonOptions);
            return stored?.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Directory))
                .DistinctBy(a => a.Id).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Grava num arquivo temporário e troca: uma falha no meio não corrompe a lista.</summary>
    private void Save()
    {
        Directory.CreateDirectory(_accountsDirectory);
        var temp = StoreFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredAccounts(1, _accounts), JsonOptions));
        File.Move(temp, StoreFile, overwrite: true);
    }

    private sealed record StoredAccounts(int Version, List<AiAccount> Accounts);
}

/// <summary>
/// Opções que valem para os dois provedores: com <see cref="ContinueWithOtherProvider"/>, ao esgotar as contas do provedor
/// escolhido, a análise continua nas contas do outro (Claude ↔ Codex). Fica em ai-settings.json.
/// </summary>
public sealed class AiProviderSettings(string file)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private Stored? _stored;

    public bool ContinueWithOtherProvider
    {
        get { lock (_gate) return Current.ContinueWithOtherProvider; }
        set
        {
            lock (_gate)
            {
                _stored = Current with { ContinueWithOtherProvider = value };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(_stored, JsonOptions));
                File.Move(file + ".tmp", file, overwrite: true);
            }
        }
    }

    private Stored Current => _stored ??= Load();

    private Stored Load()
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(file), JsonOptions) ?? new Stored() : new Stored();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Stored();
        }
    }

    private sealed record Stored(bool ContinueWithOtherProvider = true);
}
