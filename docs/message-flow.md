# Message Flow Documentation

This document provides a detailed, step-by-step explanation of the end-to-end message flow in the event-driven financial architecture, covering the transactional outbox + RabbitMQ + inbox/idempotency pipeline.

## Table of Contents

1. [Analytics.Worker Startup](#analyticsworker-startup)
2. [Payments.Api Startup](#paymentsapi-startup)
3. [Request-Time Flow: POST /payments/initiate](#request-time-flow-post-paymentsinitiate)
4. [Post-Commit Outbox Dispatch](#post-commit-outbox-dispatch)
5. [Consumer Flow: Analytics.Worker](#consumer-flow-analyticsworker)
6. [Expected Artifacts at Each Stage](#expected-artifacts-at-each-stage)
7. [Troubleshooting](#troubleshooting)

---

## Analytics.Worker Startup

The Analytics.Worker is a .NET 9 Worker Service that consumes integration events from RabbitMQ and processes them with idempotency guarantees.

### Startup Sequence

1. **Configuration Binding and Validation**
   - `Program.cs` line 18-21: Binds `RabbitMq` section from `appsettings.json` to `RabbitMqOptions`
   - Validates data annotations (e.g., `[Required]` attributes on `Host`, `Port`, `Username`, `Password`)
   - Validation occurs on startup via `ValidateOnStart()`
   - Line 28-35: Manual validation runs early to fail fast if configuration is invalid
   - **Expected config**: Host (localhost or host.docker.internal), Port (5672), Username (demo), Password (demo)

2. **Database Context Registration**
   - Line 12-15: Registers `InboxDbContext` with PostgreSQL connection string
   - Connection string points to database `demo` with schema `analytics`
   - **Expected table**: `analytics.inbox` (created via EF migrations)

3. **MassTransit Configuration**
   - Line 37-61: Configures MassTransit with RabbitMQ transport
   - Registers `PaymentInitiatedConsumer` consumer (line 39)
   - Configures RabbitMQ host with validated options (line 43-47)
   - **Critical**: Queue declaration happens here

4. **Queue Declaration**
   - Line 49: `cfg.ReceiveEndpoint("analytics-payment-initiated", ...)` declares:
     - **Main queue**: `analytics-payment-initiated`
     - **Error queue (DLQ)**: `analytics-payment-initiated_error` (created automatically by MassTransit)
   - Queue declaration happens when the worker connects to RabbitMQ
   - **Important**: If the worker is not running, the queue won't exist, and messages from Payments.Api will fail to publish

5. **Retry Configuration**
   - Line 54-58: Configures message retry policy with exponential backoff:
     - First retry after 200ms
     - Second retry after 1s
     - Third retry after 5s
   - After all retries are exhausted, message moves to `analytics-payment-initiated_error` queue

6. **Startup Logging**
   - Line 67-72: Logs RabbitMQ connection details and queue endpoint
   - **Expected log lines**:
     ```
     RabbitMQ Configuration: Host=localhost, Port=5672, VirtualHost=/
     Analytics Worker starting, listening on endpoint: analytics-payment-initiated
     ```

### Expected Artifacts After Startup

- **Database**: `analytics.inbox` table exists (empty)
- **RabbitMQ**: Queue `analytics-payment-initiated` is declared and ready
- **RabbitMQ**: Error queue `analytics-payment-initiated_error` is declared
- **Logs**: Connection info logged to console

---

## Payments.Api Startup

The Payments.Api is a .NET 9 Web API that processes payment commands using event sourcing and publishes integration events via the transactional outbox pattern.

### Startup Sequence

1. **Configuration Binding and Validation**
   - `Program.cs` line 21-24: Binds `RabbitMq` section from `appsettings.json` to `RabbitMqOptions`
   - Validates data annotations on startup
   - Line 27-38: Manual validation runs early to fail fast
   - **Expected config**: Same as Analytics.Worker (Host, Port, Username, Password)

2. **Database Context Registration**
   - Line 12-15: Registers `PaymentsDbContext` with PostgreSQL connection string
   - Connection string points to database `demo`
   - **Expected schemas/tables**: 
     - `eventstore.events` (event store)
     - `readmodel.payments_read` (query model)
     - MassTransit outbox tables (see below)

3. **Domain Services Registration**
   - Line 17-18: Registers `PaymentEventStore` and `PaymentProjector`
   - Line 40: Registers MediatR with command handlers

4. **MassTransit + RabbitMQ Setup**
   - Line 43-64: Configures MassTransit with RabbitMQ and Entity Framework Outbox
   - Line 45: Sets kebab-case endpoint name formatter for queue names
   - **This is where the magic happens**: Outbox configuration

5. **Entity Framework Outbox Configuration**
   - Line 47-52: Configures EF Core outbox with PostgreSQL backend
   - **Key settings**:
     - `QueryDelay = TimeSpan.FromSeconds(1)`: Outbox poller checks for pending messages every 1 second
     - `UsePostgres()`: Uses PostgreSQL-specific outbox implementation
     - **`UseBusOutbox()` (line 51)**: **CRITICAL** - This means `IPublishEndpoint.Publish()` writes to the outbox table **within the current DbContext transaction**, NOT directly to RabbitMQ
   - **Outbox tables created by MassTransit** (via `PaymentsDbContext.OnModelCreating`):
     - `OutboxMessage`: Stores pending messages to be published
     - `OutboxState`: Tracks outbox delivery state
     - `InboxState`: Tracks consumed messages (not used in Payments.Api but required by MassTransit)

6. **RabbitMQ Connection Setup**
   - Line 54-63: Configures RabbitMQ host connection
   - Uses validated `RabbitMqOptions` for host, port, credentials
   - `ConfigureEndpoints(context)`: Auto-configures endpoints for producers (not consumers in this service)

7. **API Endpoints Registration**
   - Line 83-94: Maps HTTP endpoints:
     - `POST /payments/initiate`: Command endpoint (uses MediatR)
     - `GET /payments/{id}`: Query endpoint (reads from `readmodel.payments_read`)

8. **Startup Logging**
   - Line 73-77: Logs RabbitMQ connection details
   - **Expected log line**:
     ```
     RabbitMQ Configuration: Host=localhost, Port=5672, VirtualHost=/
     ```

### Expected Artifacts After Startup

- **Database tables**:
  - `eventstore.events` (empty)
  - `readmodel.payments_read` (empty)
  - `OutboxMessage` (empty)
  - `OutboxState` (empty)
  - `InboxState` (empty)
- **RabbitMQ**: Connection established (no queues declared by Payments.Api; queues are declared by consumers)
- **Web Server**: Running on http://localhost:5000 (or configured port)
- **Swagger UI**: Available at http://localhost:5000/swagger

---

## Request-Time Flow: POST /payments/initiate

This section describes the complete flow when a client sends a payment initiation request.

### Request Example

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

### Detailed Flow

**1. API Endpoint Receives Request** (`Program.cs` line 83-87)
   - HTTP POST to `/payments/initiate`
   - Request body deserialized to `InitiatePaymentCommand`
   - Command dispatched to MediatR: `mediator.Send(cmd, ct)`

**2. Command Handler Invoked** (`InitiatePaymentHandler.cs` line 30)
   - `InitiatePaymentHandler.Handle()` method called
   - **Critical**: All database operations happen within a single transaction

**3. Transaction Begins** (line 33)
   - `await _db.Database.BeginTransactionAsync(ct)`
   - **Transaction boundary starts here**
   - Everything from this point until commit is atomic

**4. Load Existing Events** (line 35-36)
   - `_store.LoadAsync(paymentId)` queries `eventstore.events` table
   - Filters by `StreamId = "payment-{paymentId}"`
   - Returns current version and historical events
   - For a new payment: version = 0, history = empty list

**5. Rehydrate Aggregate** (line 36)
   - `PaymentAggregate.Rehydrate(history)` reconstructs aggregate state from events
   - For new payment: creates empty aggregate

**6. Execute Business Logic** (line 38)
   - `agg.Initiate(...)` validates and creates domain event
   - **Domain event created**: `PaymentInitiated` (in-memory, not yet persisted)
   - Event added to aggregate's uncommitted events list

**7. Persist Domain Events to Event Store** (line 40-42)
   - `_store.AppendAsync(...)` writes events to `eventstore.events`
   - Creates `EventRecord` with:
     - `StreamId`: "payment-{paymentId}"
     - `StreamVersion`: incremented version (1 for new payment)
     - `EventType`: Full type name (e.g., "Payments.Api.Domain.PaymentInitiated")
     - `PayloadJson`: Serialized event data
     - `OccurredAt`: Timestamp
     - `CorrelationId`: Request correlation ID
   - `_db.SaveChangesAsync()` called (line 50 of `PaymentEventStore.cs`)
   - **IMPORTANT**: This `SaveChanges()` does NOT commit the transaction; it stages the insert within the transaction
   - **Write to DB**: `eventstore.events` table (WITHIN transaction, not committed yet)

**8. Update Read Model** (line 44-45)
   - Loop through new events and call `_projector.ProjectAsync(ev)`
   - For `PaymentInitiated` event (`PaymentProjector.cs` line 17-35):
     - Checks if payment already exists in `readmodel.payments_read` (idempotency)
     - If not exists: Inserts new row with status "Initiated"
     - Calls `_db.SaveChangesAsync()` (line 33)
   - **Write to DB**: `readmodel.payments_read` table (WITHIN transaction, not committed yet)

**9. Publish Integration Events** (line 48-63)
   - Loop through new events
   - For `PaymentInitiated` domain event:
     - Creates `PaymentInitiatedIntegration` message with unique `MessageId`
     - Calls `_publish.Publish(new PaymentInitiatedIntegration(...), ct)`
   - **CRITICAL**: Because `UseBusOutbox()` is enabled (see Payments.Api startup):
     - `Publish()` does NOT send to RabbitMQ immediately
     - Instead, it writes to the `OutboxMessage` table within the SAME transaction
     - Message stored with metadata (MessageId, MessageType, Payload, EnqueueTime, etc.)
   - **Write to DB**: `OutboxMessage` table (WITHIN transaction, not committed yet)

**10. Commit Transaction** (line 65)
   - `await tx.CommitAsync(ct)`
   - **ALL writes committed atomically**:
     - `eventstore.events`: 1 row inserted
     - `readmodel.payments_read`: 1 row inserted
     - `OutboxMessage`: 1 row inserted
   - If any write fails, entire transaction rolls back
   - **This is the transactional outbox pattern**: Message is stored in the DB, not in RabbitMQ (yet)

**11. Clear Uncommitted Events** (line 66)
   - `agg.ClearUncommitted()`: Clears in-memory uncommitted event list

**12. Return Response** (line 86 of `Program.cs`)
   - Returns HTTP 202 Accepted to client
   - Client receives response BEFORE message is published to RabbitMQ

### Summary: What's Written and When

| Stage | Table/System | Write Type | Committed? |
|-------|--------------|------------|------------|
| Before Commit | `eventstore.events` | INSERT domain event | ❌ Not yet |
| Before Commit | `readmodel.payments_read` | INSERT read model row | ❌ Not yet |
| Before Commit | `OutboxMessage` | INSERT integration event | ❌ Not yet |
| **After Commit** | **All 3 tables** | **All writes** | ✅ **Committed atomically** |
| After Commit | RabbitMQ | Nothing yet | ❌ Not yet |

**Key Insight**: At the end of the request, the message is in the database outbox, but **NOT yet in RabbitMQ**. The outbox background processor will publish it in the next step.

---

## Post-Commit Outbox Dispatch

After the transaction commits, MassTransit's outbox background service takes over to publish messages to RabbitMQ.

### Outbox Polling Behavior

1. **Background Service**
   - MassTransit starts a background `IHostedService` for outbox processing
   - Runs continuously while the application is running

2. **Polling Interval** (Configured in `Program.cs` line 49)
   - `QueryDelay = TimeSpan.FromSeconds(1)`
   - Every 1 second, the outbox processor queries the `OutboxMessage` table for pending messages
   - Query: `SELECT * FROM OutboxMessage WHERE SentTime IS NULL ORDER BY EnqueueTime`

3. **Message Retrieval**
   - Outbox processor fetches messages that haven't been sent yet
   - For our example: 1 message of type `PaymentInitiatedIntegration`

4. **Publish to RabbitMQ**
   - For each outbox message:
     - Deserializes message payload
     - Publishes to RabbitMQ using MassTransit's publish endpoint
     - MassTransit determines routing based on message type
     - For `PaymentInitiatedIntegration`: Published to exchange corresponding to message type
     - RabbitMQ routes to queue(s) bound to that exchange

5. **Queue/Exchange Behavior**
   - MassTransit uses topic-based routing
   - Creates exchange named after message type (e.g., `Shared.Contracts:PaymentInitiatedIntegration`)
   - Analytics.Worker's queue `analytics-payment-initiated` is bound to this exchange
   - Message is delivered to the queue

6. **Update Outbox State**
   - After successful publish to RabbitMQ:
     - Updates `OutboxMessage` row: Sets `SentTime` to current timestamp
     - Updates `OutboxState` for tracking
   - Message is now marked as sent and won't be processed again

7. **Typical Timing**
   - With `QueryDelay = 1 second`, expect messages to be published to RabbitMQ within 1-2 seconds after transaction commit
   - Actual timing depends on system load and RabbitMQ availability

### Expected Observable Behavior

**Immediately after request (within milliseconds)**:
- Database contains: event, read model row, outbox message
- RabbitMQ: No message yet
- Client: Receives 202 Accepted response

**Within 1-2 seconds after request**:
- Outbox processor publishes message to RabbitMQ
- RabbitMQ: Message appears in `analytics-payment-initiated` queue
- Database: `OutboxMessage.SentTime` is updated

**Within seconds after that**:
- Analytics.Worker consumes message (see next section)

### What If Outbox Fails to Publish?

- Outbox processor will retry indefinitely (with internal retries)
- If RabbitMQ is down: Messages accumulate in `OutboxMessage` table
- When RabbitMQ comes back: Outbox processor automatically publishes backlog
- **Guaranteed delivery**: Messages will eventually be published once RabbitMQ is available

---

## Consumer Flow: Analytics.Worker

This section describes what happens when Analytics.Worker receives a message from RabbitMQ.

### Message Consumption Flow

**1. Message Delivered from RabbitMQ**
   - RabbitMQ delivers message from `analytics-payment-initiated` queue to Analytics.Worker
   - MassTransit invokes `PaymentInitiatedConsumer.Consume()` method

**2. Idempotency Check** (`PaymentInitiatedConsumer.cs` line 24)
   - Extract `MessageId` from message (line 21)
   - Query `analytics.inbox` table: `WHERE MessageId = {msg.MessageId}`
   - **If message already exists** (line 24):
     - **Duplicate detected**: Message has been processed before
     - Log: `"Duplicate ignored MessageId={MessageId} Corr={Corr}"`
     - **Return immediately** without processing (line 28)
     - **Acknowledge to RabbitMQ**: Message removed from queue
   - **If message doesn't exist**: Continue to step 3

**3. Insert Inbox Entry** (line 31-32)
   - Create new `InboxEntry` with:
     - `MessageId`: Unique message identifier
     - `ProcessedAt`: Current timestamp
   - Add to `analytics.inbox` DbSet
   - Call `SaveChangesAsync()` to persist
   - **Write to DB**: `analytics.inbox` table (1 row inserted)
   - **This write happens BEFORE business logic**: Guarantees idempotency even if business logic fails

**4. Process Business Logic** (line 34-35)
   - Log analytics processing: `"ANALYTICS processed PaymentId={PaymentId} Amount={Amount} Corr={Corr}"`
   - In real system: Would perform analytics calculations, update dashboards, send notifications, etc.
   - **Current implementation**: Just logs (simulates analytics processing)

**5. Success Path**
   - Method returns successfully
   - MassTransit acknowledges message to RabbitMQ
   - Message removed from `analytics-payment-initiated` queue
   - **Flow complete**

### Error Path: Retries and DLQ

**If an exception is thrown** (e.g., line 38 uncommented):

**1. First Failure**
   - Exception thrown during processing
   - MassTransit catches exception
   - **Does NOT acknowledge** message to RabbitMQ
   - Retry policy kicks in (configured in `Program.cs` line 54-58)

**2. First Retry (after 200ms)**
   - MassTransit redelivers message to consumer
   - Consumer attempts processing again
   - If successful: Message acknowledged and removed
   - If fails again: Next retry

**3. Second Retry (after 1 second from first retry)**
   - Consumer attempts processing again
   - If successful: Message acknowledged and removed
   - If fails again: Next retry

**4. Third Retry (after 5 seconds from second retry)**
   - Consumer attempts processing again
   - If successful: Message acknowledged and removed
   - If fails again: **All retries exhausted**

**5. Move to Dead Letter Queue**
   - After all retries exhausted, MassTransit moves message to `analytics-payment-initiated_error` queue
   - Message acknowledged from main queue (removed)
   - Message published to error queue with fault details
   - **Requires manual intervention** to inspect and replay

**6. Inspect Failed Message**
   - Open RabbitMQ Management UI: http://localhost:15672 (demo/demo)
   - Navigate to Queues → `analytics-payment-initiated_error`
   - View message details: Payload, headers, exception details
   - Options:
     - Fix code and redeploy
     - Manually move message back to `analytics-payment-initiated` for retry
     - Purge message if it's invalid

### Idempotency Guarantees

**Scenario 1: Message delivered twice due to network issues**
- First delivery: Inbox entry created, business logic runs, message acknowledged
- Second delivery: Inbox check finds existing entry, processing skipped, message acknowledged
- **Result**: Business logic runs exactly once

**Scenario 2: Processing fails after inbox insert**
- Inbox entry inserted, then exception thrown
- Message NOT acknowledged, goes to retry
- Retry: Inbox check finds existing entry, processing skipped
- **Result**: Business logic runs zero times (failed before completion), but inbox entry exists preventing future processing

**Scenario 3: Processing fails before inbox insert**
- Exception thrown before `SaveChangesAsync()` on line 32
- No inbox entry created
- Retries will attempt processing again
- Eventually succeeds or goes to DLQ
- **Result**: Retries are safe; inbox entry only created on successful path

**Important**: Inbox entry is inserted BEFORE business logic (line 31-32) to ensure idempotency even if business logic fails after the insert.

### Expected Log Output

**Successful processing** (new message):
```
ANALYTICS processed PaymentId=11111111-1111-1111-1111-111111111111 Amount=2500 Corr=corr-123
```

**Duplicate message**:
```
Duplicate ignored MessageId={guid} Corr=corr-123
```

**Failed processing** (with simulated failure enabled):
```
[Error] Exception processing message: Simulated failure
[Retry] Attempt 1 of 3...
[Retry] Attempt 2 of 3...
[Retry] Attempt 3 of 3...
[Error] Moving message to error queue: analytics-payment-initiated_error
```

---

## Expected Artifacts at Each Stage

This section provides a comprehensive list of expected database tables, RabbitMQ queues, and log entries at each stage of the message flow.

### Stage 1: After System Startup (Before Any Requests)

**Database Tables (PostgreSQL)**:
- `eventstore.events` - Empty
- `readmodel.payments_read` - Empty  
- `OutboxMessage` - Empty
- `OutboxState` - Empty
- `InboxState` - Empty
- `analytics.inbox` - Empty

**RabbitMQ Queues**:
- `analytics-payment-initiated` - Declared, 0 messages
- `analytics-payment-initiated_error` - Declared, 0 messages

**Logs (Payments.Api)**:
```
RabbitMQ Configuration: Host=localhost, Port=5672, VirtualHost=/
```

**Logs (Analytics.Worker)**:
```
RabbitMQ Configuration: Host=localhost, Port=5672, VirtualHost=/
Analytics Worker starting, listening on endpoint: analytics-payment-initiated
```

### Stage 2: After POST /payments/initiate Request (Transaction Committed, Before Outbox Dispatch)

**Database Tables**:
- `eventstore.events` - 1 row:
  - StreamId: "payment-11111111-1111-1111-1111-111111111111"
  - StreamVersion: 1
  - EventType: "Payments.Api.Domain.PaymentInitiated"
  - PayloadJson: {"PaymentId":"11111111...", "Amount":2500, "Currency":"DKK", "UserId":"user-42", ...}
  - CorrelationId: "corr-123"

- `readmodel.payments_read` - 1 row:
  - PaymentId: 11111111-1111-1111-1111-111111111111
  - Status: "Initiated"
  - Amount: 2500
  - Currency: "DKK"
  - UserId: "user-42"
  - UpdatedAt: <timestamp>

- `OutboxMessage` - 1 row:
  - MessageId: <guid>
  - MessageType: "Shared.Contracts:PaymentInitiatedIntegration"
  - Payload: Serialized integration event
  - EnqueueTime: <timestamp>
  - SentTime: NULL (not sent yet)

- `analytics.inbox` - Empty (message not consumed yet)

**RabbitMQ Queues**:
- `analytics-payment-initiated` - 0 messages (outbox not dispatched yet)
- `analytics-payment-initiated_error` - 0 messages

**Logs (Payments.Api)**: None (beyond internal MassTransit logging)

### Stage 3: After Outbox Dispatch (1-2 seconds later)

**Database Tables**:
- `eventstore.events` - Unchanged (1 row)
- `readmodel.payments_read` - Unchanged (1 row)
- `OutboxMessage` - 1 row (updated):
  - SentTime: <timestamp> (NOW SET)
- `analytics.inbox` - Still empty (message in RabbitMQ, not consumed yet)

**RabbitMQ Queues**:
- `analytics-payment-initiated` - 1 message (ready for consumption)
- `analytics-payment-initiated_error` - 0 messages

**Logs**: None specific (internal MassTransit outbox processing)

### Stage 4: After Message Consumed Successfully

**Database Tables**:
- `eventstore.events` - Unchanged (1 row)
- `readmodel.payments_read` - Unchanged (1 row)
- `OutboxMessage` - Unchanged (1 row, SentTime set)
- `analytics.inbox` - 1 row:
  - MessageId: <guid>
  - ProcessedAt: <timestamp>

**RabbitMQ Queues**:
- `analytics-payment-initiated` - 0 messages (consumed and acknowledged)
- `analytics-payment-initiated_error` - 0 messages

**Logs (Analytics.Worker)**:
```
ANALYTICS processed PaymentId=11111111-1111-1111-1111-111111111111 Amount=2500 Corr=corr-123
```

### Stage 5 (Error Scenario): After Failed Message Processing (All Retries Exhausted)

**Database Tables**:
- `eventstore.events` - Unchanged (1 row)
- `readmodel.payments_read` - Unchanged (1 row)
- `OutboxMessage` - Unchanged (1 row, SentTime set)
- `analytics.inbox` - Empty OR 1 row (depends on when failure occurred)

**RabbitMQ Queues**:
- `analytics-payment-initiated` - 0 messages (message moved)
- `analytics-payment-initiated_error` - 1 message (with fault details)

**Logs (Analytics.Worker)**:
```
[Error] Exception processing message: <exception details>
[Retry] Attempt 1 of 3...
[Retry] Attempt 2 of 3...
[Retry] Attempt 3 of 3...
[Error] Message moved to error queue after retry exhaustion
```

---

## Troubleshooting

This section addresses common issues and how to diagnose and fix them.

### Issue 1: RabbitMQ Queue Doesn't Appear

**Symptom**: 
- POST to `/payments/initiate` succeeds (202 Accepted)
- Message written to `OutboxMessage` table
- But outbox processor logs errors about queue not found or publish failures

**Root Cause**:
- Analytics.Worker declares the `analytics-payment-initiated` queue on startup
- If worker is not running, queue doesn't exist
- Payments.Api tries to publish, but RabbitMQ rejects because queue/binding doesn't exist

**Diagnosis**:
1. Check if Analytics.Worker is running: `docker ps` or process list
2. Check RabbitMQ Management UI (http://localhost:15672) → Queues tab
3. Look for `analytics-payment-initiated` queue
4. If missing: Worker is not running or failed to start

**Solution**:
1. Start Analytics.Worker: `dotnet run --project src/Analytics.Worker`
2. Verify in logs: "Analytics Worker starting, listening on endpoint: analytics-payment-initiated"
3. Verify in RabbitMQ UI: Queue now appears
4. Outbox processor will automatically retry and publish pending messages

### Issue 2: Outbox Messages Not Delivered

**Symptom**:
- POST request succeeds
- `eventstore.events` and `readmodel.payments_read` have rows
- `OutboxMessage` table has message with `SentTime = NULL`
- Message never appears in RabbitMQ queue

**Root Cause**:
- `UseBusOutbox()` not enabled or misconfigured
- RabbitMQ connection failure
- Outbox background service not running

**Diagnosis**:
1. Check `Payments.Api/Program.cs` line 51: Verify `UseBusOutbox()` is called
2. Check Payments.Api logs for RabbitMQ connection errors
3. Query database: `SELECT * FROM "OutboxMessage" WHERE "SentTime" IS NULL`
4. Check if rows are accumulating (indicates outbox processor not running or failing)

**Solution**:
1. Verify `UseBusOutbox()` is in the configuration
2. Verify RabbitMQ is running: `docker ps | grep rabbitmq`
3. Check RabbitMQ logs: `docker logs <rabbitmq-container>`
4. Restart Payments.Api to reinitialize outbox processor

### Issue 3: Wrong Database Inspected

**Symptom**:
- Claims tables are empty or don't exist
- But system appears to be working

**Root Cause**:
- Connecting to wrong database in database client
- Using `postgres` database instead of `demo` database
- Using wrong schema name

**Diagnosis**:
1. Check connection string in `appsettings.json`: `Database=demo`
2. In database client, verify connected to database: `demo`
3. Verify schema names:
   - `eventstore` schema for events table
   - `readmodel` schema for payments_read table
   - `public` schema for MassTransit outbox tables
   - `analytics` schema for inbox table

**Solution**:
1. Connect to correct database: `demo`
2. Use fully qualified table names:
   - `eventstore.events`
   - `readmodel.payments_read`
   - `public."OutboxMessage"` (note quotes for case-sensitive names)
   - `analytics.inbox`

### Issue 4: Docker vs Localhost Hostname

**Symptom**:
- Services running in Docker cannot connect to PostgreSQL or RabbitMQ
- Error: "Connection refused" or "No such host"

**Root Cause**:
- Using `localhost` in connection strings
- `localhost` inside Docker container refers to container itself, not host machine

**Diagnosis**:
1. Check where services are running: Docker vs host
2. Check connection strings for `localhost`
3. Try connecting manually from container: `ping localhost` vs `ping host.docker.internal`

**Solution**:
1. If services run in Docker: Change `Host=localhost` to `Host=host.docker.internal`
2. Update `appsettings.json`:
   ```json
   "ConnectionStrings": {
     "Postgres": "Host=host.docker.internal;Port=5432;Database=demo;Username=demo;Password=demo"
   },
   "RabbitMq": {
     "Host": "host.docker.internal",
     ...
   }
   ```
3. Restart services

### Issue 5: Handler Not Committing Transaction

**Symptom**:
- POST request succeeds (202 Accepted)
- But no data in any database tables
- Logs show no errors

**Root Cause**:
- Transaction started but not committed
- Exception thrown after transaction started but caught somewhere
- `BeginTransactionAsync()` called but `CommitAsync()` missing or not reached

**Diagnosis**:
1. Check `InitiatePaymentHandler.cs` line 33 and 65
2. Add logging before and after `CommitAsync()`
3. Check for exceptions in logs
4. Verify transaction is not rolled back

**Solution**:
1. Verify `CommitAsync()` is called in handler (line 65)
2. Verify no exceptions thrown before commit
3. Check for early returns or missing await statements
4. Add logging to trace execution path

### Issue 6: Messages Going to Error Queue Unexpectedly

**Symptom**:
- Messages appear in `analytics-payment-initiated_error` queue
- No obvious errors in consumer code

**Root Cause**:
- Consumer throwing exceptions (even if not visible)
- Timeout during processing
- Database connection issues
- Deserialization failures

**Diagnosis**:
1. Check Analytics.Worker logs for exceptions
2. Check RabbitMQ Management UI → `analytics-payment-initiated_error` → Get Message
3. Look at message headers for fault details: `MT-Fault-Message`, `MT-Fault-StackTrace`
4. Check consumer code for any logic that might throw

**Solution**:
1. Fix exception in consumer code
2. Add try-catch with logging to identify issue
3. Check database connectivity from worker
4. Verify message contract matches between producer and consumer
5. After fix: Manually replay message from error queue:
   - Get message from `analytics-payment-initiated_error`
   - Publish to `analytics-payment-initiated`
   - Or use RabbitMQ Shovel plugin

### Issue 7: Duplicate Messages Being Processed

**Symptom**:
- Same message processed multiple times
- Duplicate log entries
- Business logic side effects happening twice

**Root Cause**:
- Inbox check not working correctly
- MessageId not preserved correctly
- Database transaction not committing inbox entry

**Diagnosis**:
1. Check `analytics.inbox` table for duplicate MessageIds
2. Add logging in consumer to trace inbox check (line 24)
3. Verify MessageId is preserved through the pipeline
4. Check if `SaveChangesAsync()` on line 32 is succeeding

**Solution**:
1. Verify inbox check logic in `PaymentInitiatedConsumer.cs` line 24
2. Verify `InboxEntry.MessageId` is a unique key (check migrations)
3. Add exception handling around `SaveChangesAsync()` to catch failures
4. Consider adding transaction around inbox insert and business logic

### Issue 8: Configuration Validation Failures

**Symptom**:
- Service fails to start
- Error: "RabbitMQ configuration validation failed: ..."

**Root Cause**:
- Missing or invalid configuration in `appsettings.json`
- Wrong data types (e.g., Port as string instead of int)
- Required fields missing

**Diagnosis**:
1. Check startup logs for validation error details
2. Verify `appsettings.json` structure matches `RabbitMqOptions` class
3. Check for required fields: Host, Port, Username, Password

**Solution**:
1. Fix `appsettings.json` based on error message
2. Ensure all required fields are present
3. Verify data types (Port should be number, not string)
4. Example valid config:
   ```json
   "RabbitMq": {
     "Host": "localhost",
     "Port": 5672,
     "Username": "demo",
     "Password": "demo",
     "VirtualHost": "/"
   }
   ```

---

## Summary: Key Takeaways

1. **UseBusOutbox() is Critical**: This setting ensures `Publish()` writes to the outbox table within the transaction, not directly to RabbitMQ. This is the core of the transactional outbox pattern.

2. **Three-Phase Commit**: 
   - Phase 1: Write to DB within transaction (events, read model, outbox)
   - Phase 2: Commit transaction atomically
   - Phase 3: Background process publishes from outbox to RabbitMQ

3. **Idempotency is Essential**: The inbox pattern ensures consumers can safely handle duplicate messages, which is necessary for at-least-once delivery guarantees.

4. **Start Worker First**: Always start Analytics.Worker before testing, as it declares the queue. Without the queue, Payments.Api cannot publish.

5. **Inspect Both DB and RabbitMQ**: Troubleshooting requires checking both database tables (for outbox/inbox state) and RabbitMQ queues (for message state).

6. **Retries + DLQ = Resilience**: Automatic retries handle transient failures; DLQ captures permanent failures for manual inspection.

7. **Timing Expectations**: 
   - Request completes in milliseconds
   - Outbox publishes within 1-2 seconds
   - Consumer processes within seconds
   - Total end-to-end: 2-5 seconds typically

---

## Further Reading

- [MassTransit Documentation - Entity Framework Outbox](https://masstransit.io/documentation/configuration/middleware/outbox)
- [Outbox Pattern Explained](https://microservices.io/patterns/data/transactional-outbox.html)
- [Inbox Pattern / Idempotent Consumer](https://microservices.io/patterns/communication-style/idempotent-consumer.html)
- [RabbitMQ Management UI](http://localhost:15672) (demo/demo)
- [Architecture Documentation](architecture.md) - High-level architecture overview

---

*Document Version: 1.0*  
*Last Updated: 2026-02-14*
