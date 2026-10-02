using FluentValidation;
using SampleApi.Dtos;

namespace SampleApi.Validators;

public class CriarProgramacaoValidator : AbstractValidator<CriarProgramacaoRequest>
{
    public CriarProgramacaoValidator()
    {
        RuleFor(x => x.Data)
            .NotEmpty()
            .WithMessage("Informe a data da programação.");

        RuleFor(x => x.VeiculoId)
            .GreaterThan(0)
            .When(x => x.VeiculoId.HasValue)
            .WithMessage("Veículo inválido.");
    }
}
