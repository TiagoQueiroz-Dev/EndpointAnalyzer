using System.Globalization;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

public enum VarKind { Bool, Int, Decimal, Date, String, Enum, Guid, Object, Collection, Other }

/// <summary>De onde vem o valor: payload, estado consultado (banco, serviço) ou contexto da requisição (usuário, configuração).</summary>
public enum VarOrigin { Input, State, Context }

public sealed record EnumMember(string Name, long Value);

/// <summary>
/// Variável das restrições: um campo do payload ("in:request.Placa"), o resultado de uma consulta
/// ("st:IVeiculoRepository.ObterPorId(in:request.VeiculoId)") ou um membro dele ("...#Disponivel").
/// </summary>
public sealed class Var
{
    public required string Key { get; init; }

    public required VarKind Kind { get; init; }

    public required VarOrigin Origin { get; init; }

    /// <summary>Pode ser null (Nullable&lt;T&gt;, referência anotada ou sem contexto de nulidade, resultado de consulta).</summary>
    public bool Nullable { get; init; }

    /// <summary>Como aparece no código: "request.Placa", "veiculo.Disponivel".</summary>
    public required string Display { get; init; }

    public InputField? Field { get; init; }

    /// <summary>Consulta de onde vem o valor (estado/contexto).</summary>
    public StateRoot? Root { get; init; }

    /// <summary>Membro relativo à raiz ("Disponivel", "Endereco.Cep"); nulo para a própria raiz.</summary>
    public string? Member { get; init; }

    /// <summary>Objeto que contém este membro: precisa existir para o membro ser lido.</summary>
    public Var? Parent { get; init; }

    public IReadOnlyList<EnumMember>? EnumMembers { get; init; }

    /// <summary>Nome do tipo (sem namespace), para a descrição das pré-condições.</summary>
    public string TypeName { get; init; } = "";

    public bool IsNumeric => Kind is VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Enum;

    /// <summary>A dimensão numérica é inteira (valor, comprimento, quantidade, dias).</summary>
    public bool IsIntegral(Measure measure) => measure == Measure.Length || Kind is VarKind.Int or VarKind.Date or VarKind.Enum;

    public override string ToString() => Key;
}

/// <summary>Consulta ao estado (repositório, serviço, DbContext) ou valor do contexto (usuário logado, campo da classe).</summary>
public sealed class StateRoot
{
    public required string Key { get; init; }

    /// <summary>Tipo do valor retornado: "Veiculo", "bool".</summary>
    public required string TypeName { get; init; }

    /// <summary>Texto da chamada em partes: texto fixo ou variável do payload (substituída pelo valor do cenário).</summary>
    public required IReadOnlyList<object> Template { get; init; }

    public VarOrigin Origin { get; init; } = VarOrigin.State;

    public SourceReference? Source { get; init; }

    /// <summary>Consulta direta ao estado (repositório, DbContext, código sem fonte), e não um service de negócio.</summary>
    public bool Boundary { get; init; }

    /// <summary>Repositório, DbContext ou EF Core (não só código sem fonte).</summary>
    public bool Repository { get; init; }

    public string Render(Assignment? assignment) => string.Concat(Template.Select(part => part switch
    {
        Var v when assignment is not null => Values.Format(v, assignment.Get(v)),
        Var v => v.Display,
        _ => part.ToString(),
    }));
}

/// <summary>Valor ou comprimento (string) / quantidade (coleção).</summary>
public enum Measure { Value, Length }

public abstract record Term;

public sealed record VarTerm(Var Var, Measure Measure = Measure.Value) : Term;

/// <summary>Constante: número (inclui enum e datas em dias a partir de hoje), texto, bool ou null.</summary>
public sealed record ConstTerm(decimal? Number, string? Text, bool? Bool, bool IsNull, string Display) : Term
{
    public static ConstTerm Null { get; } = new(null, null, null, true, "null");

    public static ConstTerm Of(decimal number, string? display = null) => new(number, null, null, false, display ?? number.ToString(CultureInfo.InvariantCulture));

    public static ConstTerm Of(bool value) => new(null, null, value, false, value ? "true" : "false");

    public static ConstTerm Of(string text, string? display = null) => new(null, text, null, false, display ?? $"\"{text}\"");
}

/// <summary>DateTime.Today/Now (+ dias). Exact: Now/UtcNow (tem hora), então hoje 00:00 não é limite seguro.</summary>
public sealed record NowTerm(int OffsetDays, bool Exact) : Term;

public sealed record ArithTerm(string Op, Term Left, Term Right) : Term;

/// <summary>Predicado sobre as variáveis. Text é a expressão como no código (forma positiva).</summary>
public abstract record Pred
{
    public abstract string Key { get; }

    public virtual string Text => Key;

    public static Pred True { get; } = new ConstPred(true);

    public static Pred False { get; } = new ConstPred(false);

    public static Pred Not(Pred p) => p switch
    {
        ConstPred c => c.Value ? False : True,
        NotPred n => n.Inner,
        _ => new NotPred(p),
    };

    public static Pred And(IEnumerable<Pred> items)
    {
        var list = new List<Pred>();
        foreach (var item in items)
        {
            if (item is ConstPred { Value: true }) continue;
            if (item is ConstPred { Value: false }) return False;
            if (item is AndPred and) list.AddRange(and.Items);
            else list.Add(item);
        }
        var distinct = list.DistinctBy(p => p.Key).ToList();
        return distinct.Count switch { 0 => True, 1 => distinct[0], _ => new AndPred(distinct) };
    }

    public static Pred And(params Pred[] items) => And((IEnumerable<Pred>)items);

    public static Pred Or(IEnumerable<Pred> items)
    {
        var list = new List<Pred>();
        foreach (var item in items)
        {
            if (item is ConstPred { Value: false }) continue;
            if (item is ConstPred { Value: true }) return True;
            if (item is OrPred or) list.AddRange(or.Items);
            else list.Add(item);
        }
        var distinct = list.DistinctBy(p => p.Key).ToList();
        return distinct.Count switch { 0 => False, 1 => distinct[0], _ => new OrPred(distinct) };
    }

    public static Pred Or(params Pred[] items) => Or((IEnumerable<Pred>)items);
}

public sealed record ConstPred(bool Value) : Pred
{
    public override string Key => Value ? "true" : "false";
}

public sealed record AndPred(IReadOnlyList<Pred> Items) : Pred
{
    public override string Key => $"and({string.Join(",", Items.Select(i => i.Key))})";

    public override string Text => string.Join(" && ", Items.Select(i => i is OrPred ? $"({i.Text})" : i.Text));
}

public sealed record OrPred(IReadOnlyList<Pred> Items) : Pred
{
    public override string Key => $"or({string.Join(",", Items.Select(i => i.Key))})";

    public override string Text => string.Join(" || ", Items.Select(i => i is AndPred ? $"({i.Text})" : i.Text));
}

public sealed record NotPred(Pred Inner) : Pred
{
    public override string Key => $"not({Inner.Key})";

    /// <summary>Negação legível: "x == null" → "x != null", "a &lt; b" → "a &gt;= b", "!x" → "x".</summary>
    public override string Text
    {
        get
        {
            if (Inner is OpaquePred or AndPred or OrPred) return $"!({Inner.Text})";
            try
            {
                var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression(Inner.Text);
                if (!parsed.ContainsDiagnostics) return Scanner.SyntaxConditions.Negate(parsed);
            }
            catch (ArgumentException) { }
            return $"!({Inner.Text})";
        }
    }
}

public sealed record CmpPred(Term Left, string Op, Term Right, string Code) : Pred
{
    public override string Key => $"cmp({TermKey(Left)}{Op}{TermKey(Right)})";

    public override string Text => Code;

    public static string TermKey(Term t) => t switch
    {
        VarTerm v => v.Measure == Measure.Length ? $"len({v.Var.Key})" : v.Var.Key,
        ConstTerm c => c.IsNull ? "null" : c.Number?.ToString(CultureInfo.InvariantCulture) ?? (c.Bool is { } b ? (b ? "true" : "false") : $"'{c.Text}'"),
        NowTerm n => $"now{(n.Exact ? "!" : "")}{n.OffsetDays:+0;-0;+0}",
        ArithTerm a => $"({TermKey(a.Left)}{a.Op}{TermKey(a.Right)})",
        _ => "?",
    };

    /// <summary>Mais de uma variável ou aritmética: vai para o Z3.</summary>
    public bool IsComplex => Vars(Left).Concat(Vars(Right)).Count() > 1 || Left is ArithTerm || Right is ArithTerm;

    public static IEnumerable<VarTerm> Vars(Term t) => t switch
    {
        VarTerm v => [v],
        ArithTerm a => Vars(a.Left).Concat(Vars(a.Right)),
        _ => [],
    };
}

public sealed record NullPred(Var Var, string Code) : Pred
{
    public override string Key => $"null({Var.Key})";

    public override string Text => Code;
}

/// <summary>string.IsNullOrEmpty (Whitespace = false) ou string.IsNullOrWhiteSpace.</summary>
public sealed record BlankPred(Var Var, bool Whitespace, string Code) : Pred
{
    public override string Key => $"blank{(Whitespace ? "ws" : "")}({Var.Key})";

    public override string Text => Code;
}

public sealed record BoolPred(Var Var, string Code) : Pred
{
    public override string Key => $"bool({Var.Key})";

    public override string Text => Code;
}

/// <summary>Formato válido (e-mail).</summary>
public sealed record FormatPred(Var Var, string Format, string Code) : Pred
{
    public override string Key => $"fmt:{Format}({Var.Key})";

    public override string Text => Code;
}

/// <summary>Valor é um membro definido do enum (Enum.IsDefined, IsInEnum).</summary>
public sealed record EnumDefinedPred(Var Var, string Code) : Pred
{
    public override string Key => $"defined({Var.Key})";

    public override string Text => Code;
}

/// <summary>ModelState.IsValid: nenhuma validação de entrada violada (expandido pelo gerador).</summary>
public sealed record ModelValidPred : Pred
{
    public override string Key => "modelvalid";

    public override string Text => "ModelState.IsValid";
}

/// <summary>Condição que não foi possível traduzir: vira suposição do cenário.</summary>
public sealed record OpaquePred(string Id, string Code) : Pred
{
    public override string Key => $"opaque({Id})";

    public override string Text => Code;
}

/// <summary>Valor concreto de uma variável num cenário.</summary>
public sealed class Value
{
    public bool IsNull { get; init; }

    /// <summary>Número, valor do enum ou data (dias a partir de hoje).</summary>
    public decimal? Number { get; init; }

    /// <summary>Comprimento (string) ou quantidade de itens (coleção).</summary>
    public int? Length { get; init; }

    public string? Text { get; init; }

    public bool? Bool { get; init; }

    public bool WhitespaceOnly { get; init; }

    public bool? FormatOk { get; init; }

    public static Value Null { get; } = new() { IsNull = true };
}

/// <summary>Valores de um cenário: variáveis resolvidas, suposições (condições opacas) e se o Z3 foi usado.</summary>
public sealed class Assignment
{
    public Dictionary<Var, Value> Values { get; } = [];

    public Dictionary<string, bool> Assumptions { get; } = [];

    public bool UsedZ3 { get; set; }

    public Value Get(Var v)
    {
        if (!Values.TryGetValue(v, out var value))
            Values[v] = value = Scenarios.Values.Default(v);
        return value;
    }
}

/// <summary>Avaliação com três valores (true, false, null = não dá para saber).</summary>
public static class Evaluator
{
    public static bool? Eval(Pred p, Assignment a)
    {
        switch (p)
        {
            case ConstPred c:
                return c.Value;
            case NotPred n:
                return !Eval(n.Inner, a);
            case AndPred and:
            {
                var unknown = false;
                foreach (var item in and.Items)
                {
                    var r = Eval(item, a);
                    if (r == false) return false;
                    if (r is null) unknown = true;
                }
                return unknown ? null : true;
            }
            case OrPred or:
            {
                var unknown = false;
                foreach (var item in or.Items)
                {
                    var r = Eval(item, a);
                    if (r == true) return true;
                    if (r is null) unknown = true;
                }
                return unknown ? null : false;
            }
            case NullPred np:
                return ParentMissing(np.Var, a) ? true : a.Get(np.Var).IsNull;
            case BlankPred bp:
            {
                if (ParentMissing(bp.Var, a)) return null;
                var v = a.Get(bp.Var);
                return v.IsNull || (v.Length ?? v.Text?.Length ?? 0) == 0 || (bp.Whitespace && v.WhitespaceOnly);
            }
            case BoolPred b:
            {
                if (ParentMissing(b.Var, a)) return null;
                var v = a.Get(b.Var);
                return v.IsNull ? null : v.Bool;
            }
            case FormatPred f:
            {
                var v = a.Get(f.Var);
                return v.IsNull ? null : v.FormatOk ?? true;
            }
            case EnumDefinedPred e:
            {
                var v = a.Get(e.Var);
                if (v.IsNull || v.Number is null || e.Var.EnumMembers is not { } members) return null;
                return members.Any(m => m.Value == v.Number);
            }
            case OpaquePred o:
                return a.Assumptions.TryGetValue(o.Key, out var assumed) ? assumed : null;
            case CmpPred cmp:
                return Compare(cmp, a);
            default:
                return null;
        }
    }

    /// <summary>Membro de um objeto que não existe: a leitura não acontece (o código lançaria NullReferenceException).</summary>
    private static bool ParentMissing(Var v, Assignment a)
    {
        for (var p = v.Parent; p is not null; p = p.Parent)
            if (a.Get(p).IsNull) return true;
        return false;
    }

    private static bool? Compare(CmpPred cmp, Assignment a)
    {
        foreach (var vt in CmpPred.Vars(cmp.Left).Concat(CmpPred.Vars(cmp.Right)))
            if (ParentMissing(vt.Var, a)) return null;

        var left = Operand(cmp.Left, a);
        var right = Operand(cmp.Right, a);
        if (left.Unknown || right.Unknown) return null;

        // Semântica "lifted" do C#: null == null; null != x; comparações com null são falsas.
        if (left.IsNull || right.IsNull)
        {
            var both = left.IsNull && right.IsNull;
            return cmp.Op switch { "==" => both, "!=" => !both, _ => false };
        }

        if (left.Number is { } l && right.Number is { } r)
            return cmp.Op switch
            {
                "==" => l == r, "!=" => l != r, "<" => l < r, "<=" => l <= r, ">" => l > r, ">=" => l >= r, _ => null,
            };
        if (left.Bool is { } lb && right.Bool is { } rb)
            return cmp.Op switch { "==" => lb == rb, "!=" => lb != rb, _ => null };
        if (left.Text is { } lt && right.Text is { } rt)
            return cmp.Op switch { "==" => lt == rt, "!=" => lt != rt, _ => null };
        return null;
    }

    private readonly record struct Operation(bool IsNull, decimal? Number, string? Text, bool? Bool, bool Unknown = false);

    private static Operation Operand(Term t, Assignment a)
    {
        switch (t)
        {
            case ConstTerm c:
                return new(c.IsNull, c.Number, c.Text, c.Bool);
            case NowTerm n:
                return new(false, n.OffsetDays, null, null);
            case VarTerm vt:
            {
                var v = a.Get(vt.Var);
                if (v.IsNull) return new(true, null, null, null);
                if (vt.Measure == Measure.Length) return new(false, v.Length ?? v.Text?.Length, null, null);
                return new(false, v.Number, v.Text, v.Bool);
            }
            case ArithTerm ar:
            {
                var l = Operand(ar.Left, a);
                var r = Operand(ar.Right, a);
                if (l.Unknown || r.Unknown || l.Number is null || r.Number is null) return new(l.IsNull || r.IsNull, null, null, null, Unknown: !(l.IsNull || r.IsNull));
                decimal? value = ar.Op switch
                {
                    "+" => l.Number + r.Number,
                    "-" => l.Number - r.Number,
                    "*" => l.Number * r.Number,
                    "/" => r.Number == 0 ? null : l.Number / r.Number,
                    "%" => r.Number == 0 ? null : l.Number % r.Number,
                    _ => null,
                };
                return value is null ? new(false, null, null, null, Unknown: true) : new(false, value, null, null);
            }
            default:
                return new(false, null, null, null, Unknown: true);
        }
    }

    /// <summary>Forma normal de negação: Not só nos átomos.</summary>
    public static Pred Nnf(Pred p, bool negate = false)
    {
        switch (p)
        {
            case ConstPred c:
                return c.Value ^ negate ? Pred.True : Pred.False;
            case NotPred n:
                return Nnf(n.Inner, !negate);
            case AndPred and:
                return negate ? Pred.Or(and.Items.Select(i => Nnf(i, true))) : Pred.And(and.Items.Select(i => Nnf(i)));
            case OrPred or:
                return negate ? Pred.And(or.Items.Select(i => Nnf(i, true))) : Pred.Or(or.Items.Select(i => Nnf(i)));
            default:
                return negate ? new NotPred(p) : p;
        }
    }

    /// <summary>Átomos (sem And/Or/Not) do predicado.</summary>
    public static IEnumerable<Pred> Atoms(Pred p) => p switch
    {
        AndPred and => and.Items.SelectMany(Atoms),
        OrPred or => or.Items.SelectMany(Atoms),
        NotPred n => Atoms(n.Inner),
        ConstPred => [],
        _ => [p],
    };

    /// <summary>Partes de uma conjunção (com a polaridade).</summary>
    public static IEnumerable<Pred> Conjuncts(Pred p) => p switch
    {
        AndPred and => and.Items.SelectMany(Conjuncts),
        ConstPred { Value: true } => [],
        _ => [p],
    };

    public static IEnumerable<Var> VarsOf(Pred p) => Atoms(p).SelectMany(atom => atom switch
    {
        CmpPred c => CmpPred.Vars(c.Left).Concat(CmpPred.Vars(c.Right)).Select(v => v.Var),
        NullPred n => [n.Var],
        BlankPred b => [b.Var],
        BoolPred b => [b.Var],
        FormatPred f => [f.Var],
        EnumDefinedPred e => [e.Var],
        _ => [],
    }).Distinct();
}
