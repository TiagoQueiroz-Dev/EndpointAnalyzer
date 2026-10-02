using System.Numerics;
using Microsoft.Z3;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Encontra valores que satisfazem as restrições de um cenário. O núcleo é um solver de domínios próprio
/// (intervalos, nulidade, comprimento, enum) com busca nas disjunções; o Z3 só entra quando alguma restrição
/// compara variáveis entre si ou usa aritmética (peso * quantidade &lt;= limite).
/// </summary>
public sealed class ConstraintSolver
{
    public int Z3Calls { get; private set; }

    /// <summary>Erro ao carregar/usar o Z3 (biblioteca nativa ausente, por exemplo).</summary>
    public string? Z3Error { get; private set; }

    public Assignment? Solve(IEnumerable<Pred> goals)
    {
        var nnf = goals.Select(g => Evaluator.Nnf(g)).ToList();
        if (nnf.SelectMany(Evaluator.Atoms).Any(a => a is CmpPred { IsComplex: true }))
        {
            Z3Calls++;
            return SolveZ3(nnf);
        }
        return new DomainSolver().Solve(nnf);
    }

    private Assignment? SolveZ3(List<Pred> goals)
    {
        try
        {
            // Os decimais do modelo podem ser frações (100/51): a resposta só vale se as restrições continuam
            // verdadeiras com o valor arredondado; senão, tenta de novo com os decimais inteiros.
            foreach (var integral in new[] { false, true })
                if (new Z3Encoder(integral).Solve(goals) is { } assignment && goals.All(g => Evaluator.Eval(g, assignment) != false))
                    return assignment;
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or Z3Exception or EntryPointNotFoundException)
        {
            Z3Error = ex.Message;
            return null;
        }
    }

    /// <summary>Texto que cumpre comprimento, "só espaços" e formato pedidos.</summary>
    public static string MakeText(Var v, int length, bool? whitespace, bool? format)
    {
        if (whitespace == true) return new string(' ', Math.Max(1, length));
        if (format == false) return Values.Fill("email-invalido", length);
        return Values.Fill(Values.SampleText(v), length, email: format == true || (format is null && Values.IsEmailField(v)));
    }
}

/// <summary>Domínio de uma variável durante a busca.</summary>
internal sealed class Domain
{
    public bool? Null;
    public decimal? Lo, Hi;
    public bool LoOpen, HiOpen;
    public HashSet<decimal> Excluded = [];
    public bool? Bool;
    public string? Text;
    public HashSet<string> NotText = [];
    public bool? Whitespace;
    public bool? Format;
    public bool? Defined;

    public Domain Clone() => new()
    {
        Null = Null, Lo = Lo, Hi = Hi, LoOpen = LoOpen, HiOpen = HiOpen, Excluded = [.. Excluded], Bool = Bool,
        Text = Text, NotText = [.. NotText], Whitespace = Whitespace, Format = Format, Defined = Defined,
    };
}

internal abstract record Prim;
internal sealed record PNull(Var Var, bool IsNull) : Prim;
internal sealed record PNum(Var Var, string Op, decimal Value) : Prim;
internal sealed record PText(Var Var, string Value, bool Equal) : Prim;
internal sealed record PBool(Var Var, bool Value) : Prim;
internal sealed record PWhitespace(Var Var, bool Value) : Prim;
internal sealed record PFormat(Var Var, bool Value) : Prim;
internal sealed record PDefined(Var Var, bool Value) : Prim;
internal sealed record PAssume(string Key, bool Value) : Prim;

internal sealed class DomainSolver
{
    private const int Budget = 50_000;
    private int _steps;

    private sealed class State
    {
        public Dictionary<Var, Domain> Domains = [];
        public Dictionary<string, bool> Assumptions = [];

        public State Clone() => new()
        {
            Domains = Domains.ToDictionary(d => d.Key, d => d.Value.Clone()),
            Assumptions = new(Assumptions),
        };

        public Domain Of(Var v) => Domains.TryGetValue(v, out var d) ? d : Domains[v] = new Domain();
    }

    public Assignment? Solve(List<Pred> goals)
    {
        var state = Search(new State(), goals);
        return state is null ? null : Finalize(state);
    }

    private State? Search(State state, List<Pred> pending)
    {
        if (++_steps > Budget) return null;
        if (pending.Count == 0) return state;

        // Literais primeiro (podam a busca), disjunções por último.
        var index = pending.FindIndex(p => p is not OrPred);
        if (index < 0) index = 0;
        var goal = pending[index];
        var rest = new List<Pred>(pending);
        rest.RemoveAt(index);

        switch (goal)
        {
            case ConstPred c:
                return c.Value ? Search(state, rest) : null;
            case AndPred and:
                return Search(state, [.. and.Items, .. rest]);
            case OrPred or:
                // Payload preenchido primeiro: o ramo "null" só quando o resto não dá.
                foreach (var item in or.Items.OrderBy(i => MakesNull(i) ? 1 : 0))
                    if (Search(state.Clone(), [item, .. rest]) is { } found) return found;
                return null;
        }

        var positive = goal is not NotPred;
        var atom = goal is NotPred n ? n.Inner : goal;
        foreach (var alternative in Alternatives(atom, positive))
        {
            var next = state.Clone();
            if (alternative.All(p => Apply(next, p)) && Search(next, rest) is { } found) return found;
        }
        return null;
    }

    private static bool MakesNull(Pred p) => p switch
    {
        NullPred => true,
        AndPred and => and.Items.Any(MakesNull),
        _ => false,
    };

    /// <summary>Formas de tornar o literal verdadeiro (semântica "lifted" do C# para nulos); a alternativa null por último.</summary>
    private static IEnumerable<List<Prim>> Alternatives(Pred atom, bool positive)
    {
        switch (atom)
        {
            case NullPred np:
                if (positive && !np.Var.Nullable) yield break;
                yield return [.. Parents(np.Var), new PNull(np.Var, positive)];
                break;

            case BlankPred bp:
            {
                var v = bp.Var;
                if (positive)
                {
                    yield return [.. Parents(v), new PNull(v, false), new PNum(v, "==", 0)];
                    if (bp.Whitespace) yield return [.. Parents(v), new PNull(v, false), new PNum(v, ">=", 1), new PWhitespace(v, true)];
                    if (v.Nullable) yield return [.. Parents(v), new PNull(v, true)];
                }
                else
                {
                    List<Prim> alt = [.. Parents(v), new PNull(v, false), new PNum(v, ">=", 1)];
                    if (bp.Whitespace) alt.Add(new PWhitespace(v, false));
                    yield return alt;
                }
                break;
            }

            case BoolPred b:
                yield return [.. Parents(b.Var), new PNull(b.Var, false), new PBool(b.Var, positive)];
                if (!positive && b.Var.Nullable) yield return [.. Parents(b.Var), new PNull(b.Var, true)];
                break;

            case FormatPred f:
                yield return [.. Parents(f.Var), new PNull(f.Var, false), new PFormat(f.Var, positive)];
                break;

            case EnumDefinedPred e:
                yield return [.. Parents(e.Var), new PNull(e.Var, false), new PDefined(e.Var, positive)];
                break;

            case OpaquePred o:
                yield return [new PAssume(o.Key, positive)];
                break;

            case CmpPred cmp when Normalize(cmp) is { } normalized:
                foreach (var alt in CmpAlternatives(normalized.Var, normalized.Op, normalized.Const, normalized.ExactNow, positive))
                    yield return [.. Parents(normalized.Var.Var), .. alt];
                break;

            default:
                // Não traduzível aqui (ModelState sem expansão, comparação de texto por ordem...): vira suposição.
                yield return [new PAssume(atom.Key, positive)];
                break;
        }
    }

    private static IEnumerable<Prim> Parents(Var v)
    {
        for (var p = v.Parent; p is not null; p = p.Parent) yield return new PNull(p, false);
    }

    private sealed record Normalized(VarTerm Var, string Op, ConstTerm Const, bool ExactNow);

    /// <summary>Variável à esquerda e constante à direita.</summary>
    private static Normalized? Normalize(CmpPred cmp)
    {
        static ConstTerm? AsConst(Term t) => t switch
        {
            ConstTerm c => c,
            NowTerm n => ConstTerm.Of(n.OffsetDays),
            _ => null,
        };
        var exact = cmp.Left is NowTerm { Exact: true } || cmp.Right is NowTerm { Exact: true };
        if (cmp.Left is VarTerm l && AsConst(cmp.Right) is { } r) return new(l, cmp.Op, r, exact);
        if (cmp.Right is VarTerm r2 && AsConst(cmp.Left) is { } l2) return new(r2, Swap(cmp.Op), l2, exact);
        return null;
    }

    private static string Swap(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };

    private static string Negate(string op) => op switch { "==" => "!=", "!=" => "==", "<" => ">=", "<=" => ">", ">" => "<=", ">=" => "<", _ => op };

    private static IEnumerable<List<Prim>> CmpAlternatives(VarTerm vt, string op, ConstTerm c, bool exactNow, bool positive)
    {
        var v = vt.Var;
        if (c.IsNull)
        {
            var isNull = (op == "==") == positive;
            if (isNull && !v.Nullable) yield break;
            yield return [new PNull(v, isNull)];
            yield break;
        }

        var effective = positive ? op : Negate(op);

        if (c.Bool is { } b && v.Kind == VarKind.Bool && vt.Measure == Measure.Value)
        {
            if (effective is "==" or "!=") yield return [new PNull(v, false), new PBool(v, (effective == "==") == b)];
        }
        else if (c.Text is { } text && vt.Measure == Measure.Value)
        {
            if (effective is "==" or "!=") yield return [new PNull(v, false), new PText(v, text, effective == "==")];
            else yield return [new PAssume($"cmp:{v.Key}{effective}{text}", true)];
        }
        else if (c.Number is { } number)
        {
            List<Prim> alt = [new PNull(v, false), new PNum(v, effective, number)];
            // Comparação com DateTime.Now: hoje 00:00 não é um limite seguro.
            if (exactNow && effective != "!=") alt.Add(new PNum(v, "!=", number));
            yield return alt;
        }

        // null != x é verdadeiro; null == x e as comparações com null são falsas (alternativa por último).
        if (v.Nullable && (effective == "!=" || (!positive && op is "<" or "<=" or ">" or ">=")))
            yield return [new PNull(v, true)];
    }

    private static bool Apply(State state, Prim prim)
    {
        switch (prim)
        {
            case PAssume a:
                if (state.Assumptions.TryGetValue(a.Key, out var current) && current != a.Value) return false;
                state.Assumptions[a.Key] = a.Value;
                return true;

            case PNull n:
            {
                var d = state.Of(n.Var);
                if (d.Null is { } isNull && isNull != n.IsNull) return false;
                d.Null = n.IsNull;
                return true;
            }

            case PBool b:
            {
                var d = state.Of(b.Var);
                if (d.Bool is { } value && value != b.Value) return false;
                d.Bool = b.Value;
                return true;
            }

            case PWhitespace w:
            {
                var d = state.Of(w.Var);
                if (d.Whitespace is { } value && value != w.Value) return false;
                if (w.Value && d.Format == true) return false;
                d.Whitespace = w.Value;
                return Feasible(w.Var, d);
            }

            case PFormat f:
            {
                var d = state.Of(f.Var);
                if (d.Format is { } value && value != f.Value) return false;
                if (f.Value && d.Whitespace == true) return false;
                d.Format = f.Value;
                return true;
            }

            case PDefined e:
            {
                var d = state.Of(e.Var);
                if (d.Defined is { } value && value != e.Value) return false;
                d.Defined = e.Value;
                return Feasible(e.Var, d);
            }

            case PText t:
            {
                var d = state.Of(t.Var);
                if (t.Equal)
                {
                    if (d.Text is { } text && text != t.Value) return false;
                    if (d.NotText.Contains(t.Value)) return false;
                    d.Text = t.Value;
                }
                else
                {
                    if (d.Text == t.Value) return false;
                    d.NotText.Add(t.Value);
                }
                return Feasible(t.Var, d);
            }

            case PNum num:
            {
                var d = state.Of(num.Var);
                switch (num.Op)
                {
                    case "==":
                        Tighten(d, num.Value, false, lower: true);
                        Tighten(d, num.Value, false, lower: false);
                        break;
                    case "!=": d.Excluded.Add(num.Value); break;
                    case ">": Tighten(d, num.Value, true, lower: true); break;
                    case ">=": Tighten(d, num.Value, false, lower: true); break;
                    case "<": Tighten(d, num.Value, true, lower: false); break;
                    case "<=": Tighten(d, num.Value, false, lower: false); break;
                }
                return Feasible(num.Var, d);
            }
        }
        return false;
    }

    private static void Tighten(Domain d, decimal value, bool open, bool lower)
    {
        if (lower)
        {
            if (d.Lo is null || value > d.Lo || (value == d.Lo && open)) { d.Lo = value; d.LoOpen = open; }
        }
        else if (d.Hi is null || value < d.Hi || (value == d.Hi && open)) { d.Hi = value; d.HiOpen = open; }
    }

    /// <summary>Dimensão numérica: comprimento (string, coleção) ou o próprio valor.</summary>
    private static bool UsesLength(Var v) => v.Kind is VarKind.String or VarKind.Collection;

    private static (decimal? Lo, decimal? Hi) Bounds(Var v, Domain d)
    {
        var integral = UsesLength(v) || v.IsIntegral(Measure.Value);
        decimal? lo = d.Lo, hi = d.Hi;
        if (lo is { } l) lo = integral ? (d.LoOpen ? Math.Floor(l) + 1 : Math.Ceiling(l)) : (d.LoOpen ? l + 0.01m : l);
        if (hi is { } h) hi = integral ? (d.HiOpen ? Math.Ceiling(h) - 1 : Math.Floor(h)) : (d.HiOpen ? h - 0.01m : h);
        if (UsesLength(v)) lo = Math.Max(lo ?? 0, d.Whitespace == true ? 1 : 0);
        if (d.Text is { } text && UsesLength(v))
        {
            lo = Math.Max(lo ?? 0, text.Length);
            hi = Math.Min(hi ?? text.Length, text.Length);
        }
        return (lo, hi);
    }

    private static bool Feasible(Var v, Domain d) => Pick(v, d, Values.DefaultNumber(v, UsesLength(v) ? Measure.Length : Measure.Value)) is not null;

    /// <summary>Valor da dimensão numérica mais próximo do padrão (no limite, quando o padrão está fora).</summary>
    private static decimal? Pick(Var v, Domain d, decimal preferred)
    {
        var (lo, hi) = Bounds(v, d);
        if (lo > hi) return null;

        if (v.Kind == VarKind.Enum && !UsesLength(v) && v.EnumMembers is { Count: > 0 } members)
        {
            bool InRange(decimal x) => (lo is null || x >= lo) && (hi is null || x <= hi) && !d.Excluded.Contains(x);
            if (d.Defined != false)
            {
                var candidates = members.Select(m => (decimal)m.Value).Where(InRange).ToList();
                return candidates.Count == 0 ? null : candidates.OrderBy(x => Math.Abs(x - preferred)).ThenBy(x => x).First();
            }
            var defined = members.Select(m => (decimal)m.Value).ToHashSet();
            var options = new[] { defined.Max() + 1, defined.Min() - 1, lo ?? 0, hi ?? 0, 99 }.Where(x => InRange(x) && !defined.Contains(x)).ToList();
            return options.Count == 0 ? null : options.OrderBy(x => Math.Abs(x - preferred)).First();
        }

        var integral = UsesLength(v) || v.IsIntegral(Measure.Value);
        var value = preferred;
        if (lo is { } l && value < l) value = l;
        if (hi is { } h && value > h) value = h;
        if (integral) value = Math.Round(value);

        var step = integral ? 1m : 0.01m;
        for (var i = 0; i < 2000; i++)
        {
            foreach (var candidate in i == 0 ? [value] : new[] { value + step * i, value - step * i })
                if ((lo is null || candidate >= lo) && (hi is null || candidate <= hi) && !d.Excluded.Contains(candidate))
                    return candidate;
        }
        return null;
    }

    private static Assignment Finalize(State state)
    {
        var assignment = new Assignment();
        foreach (var (key, value) in state.Assumptions) assignment.Assumptions[key] = value;

        foreach (var (v, d) in state.Domains)
        {
            if (d.Null == true)
            {
                assignment.Values[v] = Value.Null;
                continue;
            }

            var preferred = Values.DefaultNumber(v, UsesLength(v) ? Measure.Length : Measure.Value);
            switch (v.Kind)
            {
                case VarKind.Bool:
                    assignment.Values[v] = new Value { Bool = d.Bool ?? true };
                    break;
                case VarKind.String:
                {
                    var length = (int)(Pick(v, d, preferred) ?? preferred);
                    var text = d.Text ?? ConstraintSolver.MakeText(v, length, d.Whitespace, d.Format);
                    while (d.NotText.Contains(text)) text = text.Length > 0 ? text[..^1] + (text[^1] == 'x' ? 'y' : 'x') : "x";
                    assignment.Values[v] = new Value
                    {
                        Text = text,
                        Length = text.Length,
                        WhitespaceOnly = d.Whitespace == true,
                        FormatOk = d.Format ?? (Values.IsEmailField(v) ? text.Contains('@') : null),
                    };
                    break;
                }
                case VarKind.Collection:
                    assignment.Values[v] = new Value { Length = (int)(Pick(v, d, preferred) ?? 1) };
                    break;
                case VarKind.Guid:
                {
                    var text = d.Text ?? (d.NotText.Contains(Values.DefaultGuid) ? "1b4e28ba-2fa1-11d2-883f-0016d3cca427" : Values.DefaultGuid);
                    assignment.Values[v] = new Value { Text = text };
                    break;
                }
                case VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Enum:
                    assignment.Values[v] = new Value { Number = Pick(v, d, preferred) ?? preferred };
                    break;
                default:
                    assignment.Values[v] = Values.Default(v);
                    break;
            }
        }
        return assignment;
    }
}

/// <summary>Tradução das restrições para o Z3 (Optimize), preferindo os valores padrão (soft constraints).</summary>
internal sealed class Z3Encoder(bool integralDecimals = false)
{
    private Context _ctx = null!;
    private readonly Dictionary<string, Expr> _consts = [];
    private readonly HashSet<Var> _vars = [];
    private readonly Dictionary<Var, List<(string Text, BoolExpr Flag)>> _texts = [];
    private readonly Dictionary<string, BoolExpr> _assumptions = [];

    public Assignment? Solve(List<Pred> goals)
    {
        using var ctx = new Context();
        _ctx = ctx;
        var opt = ctx.MkOptimize();
        foreach (var goal in goals) opt.Assert(Encode(goal));

        foreach (var v in _vars.ToList()) Domain(opt, v);
        foreach (var flags in _texts.Values)
            for (var i = 0; i < flags.Count; i++)
                for (var j = i + 1; j < flags.Count; j++)
                    if (flags[i].Text != flags[j].Text) opt.Assert(ctx.MkNot(ctx.MkAnd(flags[i].Flag, flags[j].Flag)));
        // Preferências em ordem de prioridade (o Z3 combina os objetivos na ordem em que aparecem):
        // valores não negativos no payload, decimais inteiros, o valor padrão e, por fim, o mais perto dele.
        foreach (var v in _vars.Where(v => v.Origin == VarOrigin.Input && _consts.ContainsKey($"num|{v.Key}") && v.Kind is VarKind.Int or VarKind.Decimal))
            opt.AssertSoft(_ctx.MkGe(Num(v), v.Kind == VarKind.Decimal ? _ctx.MkReal(0) : _ctx.MkInt(0)), 1, "nonneg");
        foreach (var v in _vars) Soft(opt, v);
        foreach (var v in _vars.Where(v => _consts.ContainsKey($"num|{v.Key}") && v.Kind is VarKind.Int or VarKind.Decimal))
        {
            var x = Num(v);
            var preferred = Values.DefaultNumber(v, Measure.Value);
            ArithExpr d = v.Kind == VarKind.Decimal ? _ctx.MkReal(preferred) : _ctx.MkInt(preferred);
            opt.MkMinimize(_ctx.MkITE(_ctx.MkGe(x, d), _ctx.MkSub(x, d), _ctx.MkSub(d, x)));
        }

        if (opt.Check() != Status.SATISFIABLE) return null;
        return Read(opt.Model);
    }

    private BoolExpr Encode(Pred p) => p switch
    {
        ConstPred c => _ctx.MkBool(c.Value),
        AndPred and => _ctx.MkAnd(and.Items.Select(Encode)),
        OrPred or => _ctx.MkOr(or.Items.Select(Encode)),
        NotPred n => _ctx.MkAnd(_ctx.MkNot(Core(n.Inner)), Parents(n.Inner)),
        _ => _ctx.MkAnd(Core(p), Parents(p)),
    };

    private BoolExpr Parents(Pred atom) => _ctx.MkAnd(Evaluator.VarsOf(atom)
        .SelectMany(v => Chain(v))
        .Select(parent => _ctx.MkNot(IsNull(parent)))
        .Prepend(_ctx.MkTrue()));

    private static IEnumerable<Var> Chain(Var v)
    {
        for (var p = v.Parent; p is not null; p = p.Parent) yield return p;
    }

    private BoolExpr Core(Pred atom)
    {
        switch (atom)
        {
            case NullPred n:
                return IsNull(n.Var);
            case BlankPred b:
                return b.Whitespace
                    ? _ctx.MkOr(IsNull(b.Var), _ctx.MkEq(Len(b.Var), _ctx.MkInt(0)), _ctx.MkAnd(_ctx.MkGe(Len(b.Var), _ctx.MkInt(1)), Flag("ws", b.Var)))
                    : _ctx.MkOr(IsNull(b.Var), _ctx.MkEq(Len(b.Var), _ctx.MkInt(0)));
            case BoolPred b:
                return _ctx.MkAnd(_ctx.MkNot(IsNull(b.Var)), Flag("bool", b.Var));
            case FormatPred f:
                return _ctx.MkAnd(_ctx.MkNot(IsNull(f.Var)), Flag("fmt", f.Var));
            case EnumDefinedPred e:
                return _ctx.MkAnd(_ctx.MkNot(IsNull(e.Var)), Flag("def", e.Var));
            case CmpPred c:
                return Compare(c);
            default:
                return Assume(atom.Key);
        }
    }

    private BoolExpr Compare(CmpPred c)
    {
        var vars = CmpPred.Vars(c.Left).Concat(CmpPred.Vars(c.Right)).Select(v => v.Var).Distinct().ToList();
        var notNull = _ctx.MkAnd(vars.Where(v => v.Nullable).Select(v => _ctx.MkNot(IsNull(v))).Prepend(_ctx.MkTrue()));

        // Texto e bool: só igualdade.
        if ((c.Left, c.Right) is (VarTerm { Measure: Measure.Value } tv, ConstTerm { Text: { } text }) && c.Op is "==" or "!=")
        {
            var eq = _ctx.MkAnd(notNull, TextFlag(tv.Var, text));
            return c.Op == "==" ? eq : _ctx.MkNot(eq);
        }
        if ((c.Left, c.Right) is (VarTerm { Var.Kind: VarKind.Bool } bv, ConstTerm { Bool: { } b }) && c.Op is "==" or "!=")
        {
            var eq = _ctx.MkAnd(notNull, _ctx.MkEq(Flag("bool", bv.Var), _ctx.MkBool(b)));
            return c.Op == "==" ? eq : _ctx.MkNot(eq);
        }

        var left = Arith(c.Left);
        var right = Arith(c.Right);
        if (left is null || right is null) return Assume(c.Key);
        if (left is RealExpr || right is RealExpr)
        {
            left = left as RealExpr ?? _ctx.MkInt2Real((IntExpr)left);
            right = right as RealExpr ?? _ctx.MkInt2Real((IntExpr)right);
        }

        BoolExpr comparison = c.Op switch
        {
            "==" => _ctx.MkEq(left, right),
            "!=" => _ctx.MkNot(_ctx.MkEq(left, right)),
            "<" => _ctx.MkLt(left, right),
            "<=" => _ctx.MkLe(left, right),
            ">" => _ctx.MkGt(left, right),
            ">=" => _ctx.MkGe(left, right),
            _ => _ctx.MkTrue(),
        };
        // Semântica "lifted": com algum operando null, "!=" é verdadeiro e o resto é falso.
        return c.Op == "!=" ? _ctx.MkOr(_ctx.MkNot(notNull), comparison) : _ctx.MkAnd(notNull, comparison);
    }

    private ArithExpr? Arith(Term t)
    {
        switch (t)
        {
            case ConstTerm { Number: { } n }:
                return n == Math.Truncate(n) ? _ctx.MkInt((long)n) : _ctx.MkReal(Rational(n));
            case NowTerm now:
                return _ctx.MkInt(now.OffsetDays);
            case VarTerm { Measure: Measure.Length } vt:
                return Len(vt.Var);
            case VarTerm vt when vt.Var.IsNumeric:
                return Num(vt.Var);
            case ArithTerm a:
            {
                var l = Arith(a.Left);
                var r = Arith(a.Right);
                if (l is null || r is null) return null;
                if (l is RealExpr || r is RealExpr)
                {
                    l = l as RealExpr ?? _ctx.MkInt2Real((IntExpr)l);
                    r = r as RealExpr ?? _ctx.MkInt2Real((IntExpr)r);
                }
                return a.Op switch
                {
                    "+" => _ctx.MkAdd(l, r),
                    "-" => _ctx.MkSub(l, r),
                    "*" => _ctx.MkMul(l, r),
                    "/" => _ctx.MkDiv(l, r),
                    "%" when l is IntExpr li && r is IntExpr ri => _ctx.MkMod(li, ri),
                    _ => null,
                };
            }
        }
        return null;
    }

    private static string Rational(decimal n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private T Const<T>(string name, Func<string, T> create) where T : Expr
    {
        if (!_consts.TryGetValue(name, out var expr)) _consts[name] = expr = create(name);
        return (T)expr;
    }

    private BoolExpr IsNull(Var v)
    {
        _vars.Add(v);
        return Const($"null|{v.Key}", n => _ctx.MkBoolConst(n));
    }

    private BoolExpr Flag(string kind, Var v)
    {
        _vars.Add(v);
        return Const($"{kind}|{v.Key}", n => _ctx.MkBoolConst(n));
    }

    private IntExpr Len(Var v)
    {
        _vars.Add(v);
        return Const($"len|{v.Key}", n => _ctx.MkIntConst(n));
    }

    private ArithExpr Num(Var v)
    {
        _vars.Add(v);
        return v.Kind == VarKind.Decimal
            ? Const<ArithExpr>($"num|{v.Key}", n => _ctx.MkRealConst(n))
            : Const<ArithExpr>($"num|{v.Key}", n => _ctx.MkIntConst(n));
    }

    private BoolExpr TextFlag(Var v, string text)
    {
        _vars.Add(v);
        var flag = Const($"txt|{v.Key}|{text}", n => _ctx.MkBoolConst(n));
        if (!_texts.TryGetValue(v, out var list)) _texts[v] = list = [];
        if (!list.Any(t => t.Text == text)) list.Add((text, flag));
        return flag;
    }

    private BoolExpr Assume(string key)
    {
        if (!_assumptions.TryGetValue(key, out var flag)) _assumptions[key] = flag = _ctx.MkBoolConst($"assume|{key}");
        return flag;
    }

    /// <summary>Restrições do tipo: não anulável, comprimento ≥ 0, enum só com membros (salvo "fora do enum").</summary>
    private void Domain(Optimize opt, Var v)
    {
        if (!v.Nullable) opt.Assert(_ctx.MkNot(IsNull(v)));
        if (v.Kind is VarKind.String or VarKind.Collection)
        {
            opt.Assert(_ctx.MkGe(Len(v), _ctx.MkInt(0)));
            if (_consts.ContainsKey($"ws|{v.Key}")) opt.Assert(_ctx.MkImplies(Flag("ws", v), _ctx.MkGe(Len(v), _ctx.MkInt(1))));
        }
        if (v.Kind == VarKind.Enum && v.EnumMembers is { Count: > 0 } members && _consts.ContainsKey($"num|{v.Key}"))
        {
            var isMember = _ctx.MkOr(members.Select(m => _ctx.MkEq(Num(v), _ctx.MkInt(m.Value))));
            opt.Assert(_ctx.MkEq(Flag("def", v), isMember));
            // Sem pedir "fora do enum", o valor é um membro.
            opt.AssertSoft(Flag("def", v), 10, "enum");
        }
    }

    private void Soft(Optimize opt, Var v)
    {
        opt.AssertSoft(_ctx.MkNot(IsNull(v)), 1, "default");
        if (_consts.ContainsKey($"num|{v.Key}"))
        {
            var preferred = Values.DefaultNumber(v, Measure.Value);
            opt.AssertSoft(_ctx.MkEq(Num(v), v.Kind == VarKind.Decimal ? _ctx.MkReal(preferred) : _ctx.MkInt(preferred)), 1, "default");
            // Decimais "redondos" são mais legíveis no payload e não sofrem com arredondamento.
            if (v.Kind == VarKind.Decimal)
            {
                var isInteger = _ctx.MkIsInteger((RealExpr)Num(v));
                if (integralDecimals) opt.Assert(isInteger);
                else opt.AssertSoft(isInteger, 2, "integral");
            }
        }
        if (_consts.ContainsKey($"len|{v.Key}"))
            opt.AssertSoft(_ctx.MkEq(Len(v), _ctx.MkInt(Values.DefaultNumber(v, Measure.Length))), 1, "default");
        if (_consts.ContainsKey($"ws|{v.Key}")) opt.AssertSoft(_ctx.MkNot(Flag("ws", v)), 1, "default");
    }

    private Assignment Read(Model model)
    {
        var assignment = new Assignment { UsedZ3 = true };
        bool True(string name) => _consts.TryGetValue(name, out var e) && model.Eval(e, true).IsTrue;

        foreach (var (key, flag) in _assumptions) assignment.Assumptions[key] = model.Eval(flag, true).IsTrue;

        foreach (var v in _vars)
        {
            if (True($"null|{v.Key}")) { assignment.Values[v] = Value.Null; continue; }
            var fallback = Values.Default(v);
            switch (v.Kind)
            {
                case VarKind.Bool:
                    assignment.Values[v] = new Value { Bool = _consts.ContainsKey($"bool|{v.Key}") ? True($"bool|{v.Key}") : fallback.Bool };
                    break;
                case VarKind.String:
                {
                    var length = _consts.TryGetValue($"len|{v.Key}", out var len) ? (int)ToDecimal(model.Eval(len, true)) : Values.DefaultNumber(v, Measure.Length);
                    var chosen = _texts.GetValueOrDefault(v)?.FirstOrDefault(t => model.Eval(t.Flag, true).IsTrue).Text;
                    bool? format = _consts.ContainsKey($"fmt|{v.Key}") ? True($"fmt|{v.Key}") : null;
                    var ws = True($"ws|{v.Key}");
                    var text = chosen ?? ConstraintSolver.MakeText(v, length, ws, format);
                    assignment.Values[v] = new Value { Text = text, Length = text.Length, WhitespaceOnly = ws, FormatOk = format ?? (Values.IsEmailField(v) ? text.Contains('@') : null) };
                    break;
                }
                case VarKind.Collection:
                    assignment.Values[v] = new Value { Length = _consts.TryGetValue($"len|{v.Key}", out var count) ? (int)ToDecimal(model.Eval(count, true)) : 1 };
                    break;
                case VarKind.Guid:
                    assignment.Values[v] = new Value { Text = _texts.GetValueOrDefault(v)?.FirstOrDefault(t => model.Eval(t.Flag, true).IsTrue).Text ?? Values.DefaultGuid };
                    break;
                case VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Enum:
                    assignment.Values[v] = new Value
                    {
                        Number = _consts.TryGetValue($"num|{v.Key}", out var num) ? ToDecimal(model.Eval(num, true)) : fallback.Number,
                    };
                    break;
                default:
                    assignment.Values[v] = fallback;
                    break;
            }
        }
        return assignment;
    }

    /// <summary>Duas casas quando o valor é exato assim; senão, 10 casas (a verificação depois confirma as restrições).</summary>
    private static decimal Fraction(decimal value) => Math.Round(value, 2) == value ? Math.Round(value, 2) : Math.Round(value, 10);

    private static decimal ToDecimal(Expr e) => e switch
    {
        IntNum i => (decimal)i.BigInteger,
        RatNum r => Fraction((decimal)r.BigIntNumerator / (decimal)r.BigIntDenominator),
        _ => decimal.TryParse(e.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
    };
}
