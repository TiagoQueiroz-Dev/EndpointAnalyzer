using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

public partial class ScenarioGenerator
{
    internal sealed partial class Run
    {
        internal ScenarioMaterialization ValidateRequest(string id, ScenarioRequest request)
        {
            if (!_entries.TryGetValue(id, out var entry)) return ScenarioMaterialization.Fail(id, "Cenário inexistente.");
            var assignment = new Assignment();
            var errors = new List<string>();
            var unknown = new HashSet<Var>();
            var mismatch = false;
            foreach (var root in _input.Roots)
            {
                JsonNode? node;
                var present = true;
                if (root.Location == InputLocations.Body) node = request.Body;
                else
                {
                    var values = root.Location switch
                    {
                        InputLocations.Route => request.Route,
                        InputLocations.Query => request.Query,
                        InputLocations.Header => request.Headers,
                        _ => null,
                    };
                    present = values?.TryGetValue(root.Name, out _) == true;
                    node = present ? JsonValue.Create(values![root.Name]) : null;
                }
                ReadField(root, node, present, root.Location != InputLocations.Body);
            }
            if (errors.Count > 0) return ScenarioMaterialization.Fail(id, string.Join(" ", errors));
            if (entry.MismatchField is not null)
            {
                if (!mismatch) return ScenarioMaterialization.Fail(id, "O campo precisa continuar com o tipo JSON incompatível previsto pelo cenário.");
            }
            else
            {
                foreach (var constraint in entry.Constraints.Select(Expand))
                {
                    var result = ExactEval(constraint, assignment, unknown);
                    if (result != true)
                        return ScenarioMaterialization.Fail(id, result == false
                            ? $"O payload contradiz a condição do cenário: {constraint.Text}."
                            : $"Não é possível comprovar a condição do cenário sem estado atual ou suporte à expressão: {constraint.Text}.");
                }
                // O solver só verifica compatibilidade; nenhuma variável de estado resolvida por ele vira prova.
                // O solver modela datas como dias inteiros; a avaliação exata acima já verificou as horas.
                var pins = assignment.Values.Where(p => p.Key.Kind != VarKind.Date || p.Value.Number is not { } days || days == decimal.Truncate(days))
                    .Select(p => new Pin(p.Key, p.Value, PinPred(p.Key, p.Value), "manual", null)).ToList();
                if (_solver.Solve([.. entry.Constraints.Select(Expand), .. Preds(pins)]) is null)
                    return ScenarioMaterialization.Fail(id, "O payload não satisfaz as restrições do cenário.");
            }
            return new ScenarioMaterialization
            {
                ScenarioId = id,
                Success = true,
                Request = request,
                Expected = entry.Scenario.Expected,
                Assignment = assignment,
                // Não há baseline ou fatos atuais neste fluxo. Isolamento fica indisponível.
                UnverifiedState = ConstrainedVars(entry).Where(v => v.Origin != VarOrigin.Input).Select(v => v.Display).ToList(),
            };

            void ReadField(InputField field, JsonNode? node, bool present, bool textBinding)
            {
                var variable = _vars.Input(field);
                if (!present && HasInitializer(field))
                {
                    unknown.Add(variable);
                    return;
                }
                if (node is null)
                {
                    // Membros ausentes recebem o default do CLR, não os valores sintéticos do gerador.
                    if (present && field.Type.IsValueType && !field.Nullable)
                        errors.Add($"{field.Path}: null não é compatível com {field.TypeDisplay}.");
                    assignment.Values[variable] = field.Nullable || !field.Type.IsValueType ? Value.Null : ClrDefault(field);
                    foreach (var child in field.Children) ReadField(child, null, false, textBinding);
                    return;
                }
                var converted = ReadValue(field, node, textBinding);
                if (converted is null)
                {
                    if (field == entry.MismatchField && present) mismatch = true;
                    else errors.Add($"{field.Path}: tipo ou valor JSON incompatível com {field.TypeDisplay}.");
                    return;
                }
                assignment.Values[variable] = converted;
                if (node is JsonObject obj)
                {
                    foreach (var child in field.Children)
                    {
                        var members = obj.Where(p => string.Equals(p.Key, child.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (members.Count > 1) errors.Add($"{child.Path}: propriedade duplicada no JSON.");
                        ReadField(child, members.FirstOrDefault().Value, members.Count > 0, textBinding);
                    }
                }
                // A representação simbólica de coleções guarda o tamanho, não todos os elementos.
                if (field.Element is not null)
                {
                    // Valida tipos de todos os itens, sem tratar um item como representante comprovado da lista.
                    foreach (var item in (JsonArray)node) ReadField(field.Element, item, true, textBinding);
                    foreach (var element in field.Element.Flatten()) unknown.Add(_vars.Input(element));
                }
            }
        }

        private static bool HasInitializer(InputField field) => field.Property?.DeclaringSyntaxReferences
            .Any(r => r.GetSyntax() is PropertyDeclarationSyntax { Initializer: not null }) == true;

        private static Value ClrDefault(InputField field) => field.Kind switch
        {
            VarKind.Bool => new Value { Bool = false },
            VarKind.Int or VarKind.Decimal or VarKind.Enum => new Value { Number = 0 },
            VarKind.Date => new Value { Number = Values.MinDateDays },
            VarKind.Guid => new Value { Text = Values.EmptyGuid },
            _ => Value.Null,
        };

        private Value? ReadValue(InputField field, JsonNode node, bool textBinding)
        {
            var kind = node.GetValueKind();
            var text = kind == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
            var variable = _vars.Input(field);
            switch (field.Kind)
            {
                case VarKind.Object:
                    return node is JsonObject ? new Value() : null;
                case VarKind.Collection:
                    return node is JsonArray array ? new Value { Length = array.Count } : null;
                case VarKind.String:
                    return kind == JsonValueKind.String ? Values.Text(text, variable) : null;
                case VarKind.Bool:
                    return (textBinding || kind is JsonValueKind.True or JsonValueKind.False) && bool.TryParse(text, out var flag)
                        ? new Value { Bool = flag } : null;
                case VarKind.Int or VarKind.Decimal:
                    if ((!textBinding && kind != JsonValueKind.Number) || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
                    if (field.Kind == VarKind.Int && (n != decimal.Truncate(n) || !FitsInteger(field, n))) return null;
                    return new Value { Number = n };
                case VarKind.Enum:
                    if (kind == JsonValueKind.Number || textBinding)
                    {
                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                            && field.Type is INamedTypeSymbol { EnumUnderlyingType: { } underlying }
                            && FitsInteger(underlying.SpecialType, number)) return new Value { Number = number };
                    }
                    if (kind == JsonValueKind.String && (_solution.EnumsAsStrings || textBinding)
                        && field.EnumMembers?.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)) is { } member)
                        return new Value { Number = member.Value };
                    return null;
                case VarKind.Guid:
                    return kind == JsonValueKind.String && Guid.TryParseExact(text, "D", out var guid) ? new Value { Text = guid.ToString() } : null;
                case VarKind.Date:
                    if (kind != JsonValueKind.String) return null;
                    try
                    {
                        var date = field.Type.Name switch
                        {
                            "DateOnly" => JsonSerializer.Deserialize<DateOnly>(node.ToJsonString()).ToDateTime(TimeOnly.MinValue),
                            "DateTimeOffset" => JsonSerializer.Deserialize<DateTimeOffset>(node.ToJsonString()).LocalDateTime,
                            _ => JsonSerializer.Deserialize<DateTime>(node.ToJsonString()),
                        };
                        return new Value { Number = date.Year <= 1 ? Values.MinDateDays : (decimal)(date - DateTime.Today).TotalDays };
                    }
                    catch (JsonException) { return null; }
                default:
                    return null; // Não inventa semântica para um tipo não modelado.
            }
        }

        private static bool FitsInteger(InputField field, decimal n) => FitsInteger(field.Type.SpecialType, n);

        private static bool FitsInteger(SpecialType type, decimal n) => type switch
        {
            SpecialType.System_Byte => n is >= byte.MinValue and <= byte.MaxValue,
            SpecialType.System_SByte => n is >= sbyte.MinValue and <= sbyte.MaxValue,
            SpecialType.System_Int16 => n is >= short.MinValue and <= short.MaxValue,
            SpecialType.System_UInt16 => n is >= ushort.MinValue and <= ushort.MaxValue,
            SpecialType.System_Int32 => n is >= int.MinValue and <= int.MaxValue,
            SpecialType.System_UInt32 => n is >= uint.MinValue and <= uint.MaxValue,
            SpecialType.System_Int64 => n is >= long.MinValue and <= long.MaxValue,
            SpecialType.System_UInt64 => n is >= ulong.MinValue and <= ulong.MaxValue,
            _ => false,
        };

        // Assignment.Get cria defaults; por isso só chamamos Evaluator com variáveis efetivamente conhecidas.
        private static bool? ExactEval(Pred pred, Assignment assignment, HashSet<Var> unknown) => pred switch
        {
            ConstPred c => c.Value,
            NotPred n => !ExactEval(n.Inner, assignment, unknown),
            AndPred a => And(a.Items.Select(p => ExactEval(p, assignment, unknown))),
            OrPred o => !And(o.Items.Select(p => !ExactEval(p, assignment, unknown))),
            _ => Evaluator.VarsOf(pred).SelectMany(Lineage).Any(v => v.Origin != VarOrigin.Input || unknown.Contains(v) || !assignment.Values.ContainsKey(v))
                ? null : ExactAtom(pred, assignment),
        };

        private static bool? ExactAtom(Pred pred, Assignment assignment)
        {
            if (pred is FormatPred format)
                return format.Format == "email" && assignment.Values[format.Var].Text is { } text
                    ? new EmailAddressAttribute().IsValid(text) : null;
            if (pred is CmpPred comparison)
                pred = comparison with { Left = CurrentTime(comparison.Left), Right = CurrentTime(comparison.Right) };
            return Evaluator.Eval(pred, assignment);
        }

        private static Term CurrentTime(Term term) => term switch
        {
            NowTerm { Exact: true } now => ConstTerm.Of((decimal)(DateTime.Now - DateTime.Today).TotalDays + now.OffsetDays),
            ArithTerm arithmetic => arithmetic with { Left = CurrentTime(arithmetic.Left), Right = CurrentTime(arithmetic.Right) },
            _ => term,
        };

        private static bool? And(IEnumerable<bool?> values)
        {
            var unknown = false;
            foreach (var value in values)
            {
                if (value == false) return false;
                if (value is null) unknown = true;
            }
            return unknown ? null : true;
        }
    }
}
