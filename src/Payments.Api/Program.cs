using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Application;
using Payments.Api.Configuration;
using Payments.Api.Infrastructure;
using Payments.Api.Infrastructure.EventStore;
using Payments.Api.ReadModel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PaymentsDbContext>(o =>
{
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
});

builder.Services.AddScoped<IPaymentEventStore, PaymentEventStore>();
builder.Services.AddScoped<PaymentProjector>();

// Bind and validate RabbitMQ configuration
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var rabbitMqOptions = builder.Configuration
    .GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

builder.Services.AddMediatR(typeof(InitiatePaymentHandler).Assembly);

// MassTransit + RabbitMQ + Transactional Outbox (EF Core)
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();

    x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(1);
        o.UsePostgres();
        o.UseBusOutbox(); // critical: Publish writes to outbox within the DbContext transaction
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, checked((ushort)rabbitMqOptions.Port), rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);
        });

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Log RabbitMQ connection details for diagnostics
var logger = app.Logger;
logger.LogInformation(
    "RabbitMQ Configuration: Host={Host}, Port={Port}, VirtualHost={VirtualHost}",
    rabbitMqOptions.Host,
    rabbitMqOptions.Port,
    rabbitMqOptions.VirtualHost);

app.UseSwagger();
app.UseSwaggerUI();

// CQRS command endpoint
app.MapPost("/payments/initiate", async (IMediator mediator, InitiatePaymentCommand cmd, CancellationToken ct) =>
{
    await mediator.Send(cmd, ct);
    return Results.Accepted();
});

// Query endpoint (read model)
app.MapGet("/payments/{id:guid}", async (PaymentsDbContext db, Guid id, CancellationToken ct) =>
{
    var row = await db.PaymentsRead.SingleOrDefaultAsync(x => x.PaymentId == id, ct);
    return row is null ? Results.NotFound() : Results.Ok(row);
});

app.Run();
