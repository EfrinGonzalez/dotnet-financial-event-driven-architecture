# EventDrivenComplete (.NET 9)

This repository is a reference implementation demonstrating:

- **Event Sourcing** (PostgreSQL event store table `eventstore.events`)
- **CQRS with MediatR** (commands via `IMediator`)
- **Event-driven projections** (read model in `readmodel.payments_read`)
- **Integration events** via **RabbitMQ** (MassTransit)
- **Transactional Outbox** (MassTransit EF Outbox with `UseBusOutbox()`)
- **Inbox idempotency** in consumer (`analytics.inbox`)
- **Retries + DLQ** (MassTransit retry + RabbitMQ `*_error` queues)

## Architecture

For a detailed overview of the system architecture, including container-level diagrams and explanations of key patterns (Event Sourcing, CQRS, Outbox, Inbox, Retries/DLQ), see the [Architecture Documentation](docs/architecture.md).

For a detailed, step-by-step explanation of the end-to-end message flow through the transactional outbox + RabbitMQ + inbox/idempotency pipeline, including startup sequences, request handling, outbox dispatch, consumer processing, expected artifacts at each stage, and comprehensive troubleshooting guidance, see the [Message Flow Documentation](docs/message-flow.md).

## Prerequisites

- .NET SDK 9
- Docker (for Postgres + RabbitMQ)
- Optional: `dotnet-ef` tool for migrations

## Start infrastructure

```bash
docker compose -f infra/docker-compose.yml up -d
```

RabbitMQ UI: http://localhost:15672 (demo/demo)

## Restore & build

```bash
dotnet restore
dotnet build
```

## Apply migrations

Install EF tool once:

```bash
dotnet tool install --global dotnet-ef
```

Payments API schema:

```bash
dotnet ef migrations add InitPayments -p src/Payments.Api -s src/Payments.Api -c Payments.Api.Infrastructure.PaymentsDbContext
dotnet ef database update -p src/Payments.Api -s src/Payments.Api -c Payments.Api.Infrastructure.PaymentsDbContext
```

Analytics worker inbox schema:

```bash
dotnet ef migrations add InitAnalyticsInbox -p src/Analytics.Worker -s src/Analytics.Worker -c Analytics.Worker.Inbox.InboxDbContext
dotnet ef database update -p src/Analytics.Worker -s src/Analytics.Worker -c Analytics.Worker.Inbox.InboxDbContext
```

## Run

**Terminal A**:

```bash
dotnet run --project src/Analytics.Worker
```

**Terminal B**:

```bash
dotnet run --project src/Payments.Api
```

Swagger is enabled; check the console for the bound URL (typically `http://localhost:5000/swagger`).

## Test

Create a payment:

```bash
curl -X POST http://localhost:5000/payments/initiate \
  -H "Content-Type: application/json" \
  -d '{
    "paymentId":"11111111-1111-1111-1111-111111111111",
    "amount":2500,
    "currency":"DKK",
    "userId":"user-42",
    "correlationId":"corr-123"
  }'
```

Query the read model:

```bash
curl http://localhost:5000/payments/11111111-1111-1111-1111-111111111111
```

## What to look at

- **Event store**: `eventstore.events` contains append-only domain events.
- **Read model**: `readmodel.payments_read` contains query-optimized state.
- **Outbox**: MassTransit outbox tables (created by migrations) store pending integration messages.
- **RabbitMQ**: queue `analytics-payment-initiated` receives `PaymentInitiatedIntegration`.
- **Idempotency**: `analytics.inbox` prevents duplicate side-effects.
- **DLQ**: if you force failures in the consumer, messages end up in `analytics-payment-initiated_error`.

## Troubleshooting

### RabbitMQ queue doesn’t appear
Start the worker first (it declares the queue), then post a payment.

### Outbox messages not delivered
Confirm `UseBusOutbox()` is enabled in `Payments.Api/Program.cs` and that RabbitMQ is running.

### EF migrations issues
Make sure to run migrations with the correct project (`-p`), startup (`-s`), and context (`-c`).

---
DB Troubleshooting in Docker

Trobleshoot in case docker container is not in the same network.
Insted of using localhost for the host, use the followin:
Use this setup going forward:

- **Host name/address:** host.docker.internal
- **Port:** 5432 (or whatever host port you publish)
- **Maintenance database:** demo (or postgres)
- **Username/Password:** demo / demo