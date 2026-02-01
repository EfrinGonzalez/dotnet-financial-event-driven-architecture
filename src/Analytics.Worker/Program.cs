using Analytics.Worker.Consumers;
using Analytics.Worker.Inbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<InboxDbContext>(o =>
{
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
});

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentInitiatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var host = builder.Configuration["RabbitMq:Host"]!;
        var user = builder.Configuration["RabbitMq:Username"]!;
        var pass = builder.Configuration["RabbitMq:Password"]!;

        cfg.Host(host, "/", h =>
        {
            h.Username(user);
            h.Password(pass);
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
host.Run();
