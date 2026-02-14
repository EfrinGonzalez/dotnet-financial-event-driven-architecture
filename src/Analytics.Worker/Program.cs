using Analytics.Worker.Configuration;
using Analytics.Worker.Consumers;
using Analytics.Worker.Inbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<InboxDbContext>(o =>
{
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
});

// Bind and validate RabbitMQ configuration
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Get and validate RabbitMQ options early for MassTransit configuration
var rabbitMqOptions = builder.Configuration
    .GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

// Manually validate to ensure early detection of configuration issues
var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
var validationContext = new System.ComponentModel.DataAnnotations.ValidationContext(rabbitMqOptions);
if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(rabbitMqOptions, validationContext, validationResults, true))
{
    var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
    throw new InvalidOperationException($"RabbitMQ configuration validation failed: {errors}");
}

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentInitiatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, checked((ushort)rabbitMqOptions.Port), rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);
        });

        cfg.ReceiveEndpoint("analytics-payment-initiated", e =>
        {
            e.ConfigureConsumer<PaymentInitiatedConsumer>(context);

            // Retry; after failures, message goes to analytics-payment-initiated_error
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5)
            ));
        });
    });
});

var host = builder.Build();

// Log RabbitMQ connection details for diagnostics
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation(
    "RabbitMQ Configuration: Host={Host}, Port={Port}, VirtualHost={VirtualHost}",
    rabbitMqOptions.Host,
    rabbitMqOptions.Port,
    rabbitMqOptions.VirtualHost);
logger.LogInformation("Analytics Worker starting, listening on endpoint: analytics-payment-initiated");

host.Run();
