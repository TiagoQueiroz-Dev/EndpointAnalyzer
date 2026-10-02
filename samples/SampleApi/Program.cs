using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SampleApi.Cadastro;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Exceptions;
using SampleApi.Repositories;
using SampleApi.Services;
using SampleApi.Validators;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("sample"));

builder.Services.AddScoped<IProgramacaoService, ProgramacaoService>();
builder.Services.AddScoped<IProgramacaoRepository, ProgramacaoRepository>();
builder.Services.AddScoped<IVeiculoRepository, VeiculoRepository>();
builder.Services.AddScoped<INotificacaoService, EmailNotificacaoService>();
builder.Services.AddScoped<IValidator<CriarProgramacaoRequest>, CriarProgramacaoValidator>();
builder.Services.AddScoped<ICargaService, CargaService>();
builder.Services.AddScoped<ICargaRepository, CargaRepository>();

// Cadastro (padrões de services/repositórios genéricos)
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped<IVeiculoTipoRepository, VeiculoTipoRepository>();
builder.Services.AddScoped<IMotivoRepository, MotivoRepository>();
builder.Services.AddScoped<ICadastroVeiculoRepository, CadastroVeiculoRepository>();
builder.Services.AddScoped<IVeiculoTipoService, VeiculoTipoService>();
builder.Services.AddScoped<IMotivoService, MotivoService>();
builder.Services.AddScoped<ICadastroVeiculoService, CadastroVeiculoService>();
builder.Services.AddScoped<ICadastroVeiculoAppService, CadastroVeiculoAppService>();
builder.Services.AddScoped<IMediatorHandler, InMemoryBus>();

var app = builder.Build();

app.UseMiddleware<ExceptionMiddleware>();
app.MapControllers();

app.Run();
