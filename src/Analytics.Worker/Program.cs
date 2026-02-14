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

// Bind RabbitMQ configuration
var rabbitMqOptions = builder.Configuration
    .GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentInitiatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, h =>
        {
            h.Username(rabbitMqOptions.Username);
            h.Password(rabbitMqOptions.Password);

            if (rabbitMqOptions.Port != 5672)
            {
                h.UseCluster(c => c.Node($"{rabbitMqOptions.Host}:{rabbitMqOptions.Port}"));
            }
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
    "RabbitMQ Configuration: Host={Host}, Port={Port}, VirtualHost={VirtualHost}, Username={Username}",
    rabbitMqOptions.Host,
    rabbitMqOptions.Port,
    rabbitMqOptions.VirtualHost,
    rabbitMqOptions.Username);
logger.LogInformation("Analytics Worker starting, listening on endpoint: analytics-payment-initiated");

host.Run();
