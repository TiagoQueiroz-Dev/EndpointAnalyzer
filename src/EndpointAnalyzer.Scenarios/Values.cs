using System.Globalization;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Valores padrão (válidos e legíveis) de cada variável e a formatação para o payload e as pré-condições.</summary>
public static class Values
{
    /// <summary>DateTime.MinValue / default(DateTime), em dias a partir de hoje.</summary>
    public const int MinDateDays = -1_000_000;

    public const string DefaultGuid = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    public const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    /// <summary>Data de referência do "hoje" dos cenários.</summary>
    public static DateTime Today { get; set; } = DateTime.Today;

    public static Value Default(Var v) => v.Kind switch
    {
        VarKind.Bool => new Value { Bool = true },
        VarKind.Int => new Value { Number = 1 },
        VarKind.Decimal => new Value { Number = v.Origin == VarOrigin.Input ? 10 : 100 },
        // Payload: amanhã (datas costumam não poder estar no passado); estado: hoje.
        VarKind.Date => new Value { Number = v.Origin == VarOrigin.Input ? 1 : 0 },
        VarKind.Enum => new Value { Number = v.EnumMembers is { Count: > 0 } members ? members[0].Value : 0 },
        VarKind.Guid => new Value { Text = DefaultGuid },
        VarKind.String => Text(SampleText(v), v),
        VarKind.Collection => new Value { Length = 1 },
        VarKind.Other => new Value { Text = "08:00:00" },
        _ => new Value(),
    };

    public static int DefaultNumber(Var v, Measure measure) => measure == Measure.Length
        ? v.Kind == VarKind.String ? SampleText(v).Length : 1
        : (int)(Default(v).Number ?? 0);

    public static Value Text(string text, Var v) => new()
    {
        Text = text,
        Length = text.Length,
        FormatOk = IsEmailField(v) ? text.Contains('@') : null,
        WhitespaceOnly = text.Length > 0 && string.IsNullOrWhiteSpace(text),
    };

    public static bool IsEmailField(Var v) => Name(v).Contains("email", StringComparison.OrdinalIgnoreCase);

    private static string Name(Var v) => v.Field?.Member ?? v.Member ?? v.Display;

    /// <summary>Texto de exemplo pelo nome do campo (placa, e-mail, CPF...).</summary>
    public static string SampleText(Var v)
    {
        var name = Name(v).ToLowerInvariant();
        if (name.Contains("email")) return "usuario@exemplo.com";
        if (name.Contains("placa")) return "ABC1D23";
        if (name.Contains("cnpj")) return "11222333000181";
        if (name.Contains("cpf")) return "52998224725";
        if (name.Contains("cep")) return "01001000";
        if (name.Contains("telefone") || name.Contains("celular") || name.Contains("fone") || name.Contains("phone")) return "11999999999";
        if (name.Contains("url") || name.Contains("site") || name.Contains("link")) return "https://exemplo.com";
        if (name.Contains("senha") || name.Contains("password")) return "Senha@123";
        if (name.Contains("uf") && name.Length <= 3) return "SP";
        if (name.Contains("nome") || name.Contains("name")) return "Nome de teste";
        if (name.Contains("codigo") || name.Contains("code")) return "ABC123";
        if (name.Contains("motivo") || name.Contains("observ") || name.Contains("descri") || name.Contains("coment")) return "Texto de teste";
        return "texto";
    }

    /// <summary>Texto com o comprimento pedido: o exemplo, ou "abcdefghij" repetido (fácil de contar).</summary>
    public static string Fill(string sample, int length, bool email = false)
    {
        if (length <= 0) return "";
        if (length == sample.Length) return sample;
        if (email && length >= 5)
        {
            const string domain = "@exemplo.com";
            return length > domain.Length ? Repeat(length - domain.Length) + domain : Repeat(length - 2) + "@x";
        }
        return length < sample.Length ? sample[..length] : Repeat(length);
    }

    private static string Repeat(int length)
    {
        const string block = "abcdefghij";
        return string.Concat(Enumerable.Repeat(block, length / block.Length + 1))[..length];
    }

    public static DateTime Date(decimal days) =>
        days <= MinDateDays ? DateTime.MinValue : Today.AddDays((double)days);

    /// <summary>Valor como aparece na descrição do cenário: "\"ABC1D23\"", "5", "2026-10-03", "Cancelada".</summary>
    public static string Format(Var v, Value value)
    {
        if (value.IsNull) return "null";
        switch (v.Kind)
        {
            case VarKind.Bool:
                return value.Bool == false ? "false" : "true";
            case VarKind.Date:
                return value.Number is { } days ? DateText(days) : "?";
            case VarKind.Enum:
                return EnumName(v, value) ?? value.Number?.ToString(CultureInfo.InvariantCulture) ?? "?";
            case VarKind.String:
            {
                var text = value.Text ?? "";
                return text.Length > 40 ? $"\"{text[..20]}…\" ({text.Length} caracteres)" : $"\"{text}\"";
            }
            case VarKind.Guid or VarKind.Other:
                return value.Text ?? "?";
            case VarKind.Collection:
                return $"{value.Length ?? 0} item(ns)";
            case VarKind.Object:
                return "objeto";
            default:
                return value.Number?.ToString(CultureInfo.InvariantCulture) ?? "?";
        }
    }

    public static string DateText(decimal days) => days <= MinDateDays
        ? "0001-01-01 (DateTime.MinValue)"
        : $"{Date(days):yyyy-MM-dd} ({RelativeDay(days)})";

    public static string RelativeDay(decimal days) => days switch
    {
        <= MinDateDays => "data mínima",
        0 => "hoje",
        1 => "amanhã",
        -1 => "ontem",
        > 0 => $"hoje + {days} dias",
        _ => $"hoje - {-days} dias",
    };

    public static string? EnumName(Var v, Value value) =>
        v.EnumMembers?.FirstOrDefault(m => m.Value == value.Number)?.Name is { } name ? $"{v.TypeName}.{name}" : null;
}
