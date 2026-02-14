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

var rabbitMqOptions = builder.Configuration
    .GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

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
