# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

IoTHunter is a .NET 10 cloud-native dual-protocol (HTTP + MQTT-over-WebSocket) IoT data gateway for smart wristbands. It uses Kafka as the reliability boundary with projected CQRS for storage.

## Build & Run Commands

```bash
# Build (0 warnings, 0 errors required)
dotnet build

# Run individual services (ports: IoTGateway=5080, BackendProcessor=5081)
dotnet run --project IoTGateway/IoTGateway.csproj
dotnet run --project BackendProcessor/BackendProcessor.csproj
dotnet run --project DeviceSimulator/DeviceSimulator.csproj

# Infrastructure (Docker Desktop required)
docker compose -f docker-compose.infra.yml up -d
docker compose -f docker-compose.infra.yml down -v   # full cleanup including volumes

# Verify services
curl http://localhost:5080/health/ready
curl http://localhost:5080/metrics
curl http://localhost:5081/health/ready
curl http://localhost:5081/metrics

# Add a NuGet package (always use dotnet add, never hand-edit versions)
dotnet add IoTGateway/IoTGateway.csproj package PackageName
```

## Architecture

**Solution layout** (`IoTHunter.slnx` solution file):

| Project | Type | Responsibility |
|---|---|---|
| `IoTHunter.Shared` | Class library | Domain models (`TelemetryEnvelope`, `ReliabilityLevel`), Serilog/OTel infrastructure |
| `IoTGateway` | ASP.NET Core Web API | Dual-protocol ingress. HTTP + MQTT WebSocket. Validates, authenticates, writes to Kafka. **Never touches a database.** Kafka ack = success boundary (returns 202). No Kafka = returns 503. |
| `BackendProcessor` | ASP.NET Core Web API + Workers | CQRS queries + 3 independent Kafka consumer workers (persistence → PG/TimescaleDB, latest-value projection → Redis, timeseries projection → TimescaleDB/InfluxDB/ClickHouse). Each has its own consumer group and commits offsets independently. |
| `DeviceSimulator` | .NET Generic Host | Dual-mode load testing tool. `--protocol http|mqtt`, supports ordinary telemetry and critical events. |

**Data flow**: Device → IoTGateway (HTTP/MQTT) → Kafka → BackendProcessor workers → PostgreSQL/Redis/TimescaleDB

**Reliability levels** (`IoTHunter.Shared.Domain.ReliabilityLevel`):
- `BestEffort` (0) — ordinary telemetry, QoS 0 acceptable
- `AtLeastOnce` (1) — ordinary telemetry with confirmation
- `Critical` (2) — must be QoS 1 / Kafka ack, no silent downgrade

### Gateway Internal Structure (Step 2)

```
IoTGateway/
├── Contracts/TelemetryRequest.cs        # Ingress DTO with DataAnnotations validation
├── Controllers/TelemetryController.cs   # POST /api/v1/telemetry, POST /api/v1/events/critical
└── Infrastructure/
    ├── Options/KafkaOptions.cs, MqttOptions.cs
    ├── Messaging/
    │   ├── KafkaProducerService.cs       # Acks.All, EnableIdempotence, W3C Trace Context injection
    │   └── TelemetryEnvelopeMapper.cs    # Request → TelemetryEnvelope mapping
    ├── Mqtt/MqttIngestionService.cs      # BackgroundService, shared subscriptions, regex topic parsing
    ├── Resilience/ResiliencePipelines.cs # Polly v8: 3 retries (100→200→400ms), 4s timeout
    ├── Metrics/GatewayMetrics.cs         # 4 custom meters (requests, rejected, latency, mqtt)
    ├── Health/KafkaHealthCheck.cs, MqttHealthCheck.cs
    └── SerializerSetup.cs               # Tight JSON options shared by HTTP + MQTT
```

## Hard Architectural Rules (ADRs)

These are non-negotiable decisions from the architecture document:

- **ADR-001**: No Consul. K8s DNS + Service only.
- **ADR-002**: Gateway must never reference any database driver. Kafka is the only persistence downstream.
- **ADR-003**: `EnableAutoCommit = false` for all Kafka consumers. Each projection worker has independent consumer group and commits after its own successful write.
- **ADR-004**: OpenTelemetry W3C Trace Context must be injected/extracted in Kafka headers via `Propagators.DefaultTextMapPropagator`.
- **ADR-005**: `event_id` is the universal idempotency key. PG uses `ON CONFLICT (event_id) DO NOTHING`. Redis latest-value updates must compare `recorded_at` or `sequence` to reject stale data.
- **ADR-007**: Never use `PublishSingleFile` + `linux-musl`. Always multi-file publish.
- **ADR-018**: PG writes use batch `INSERT ... ON CONFLICT (event_id) DO NOTHING`. No raw COPY.

## Pitfalls & Conventions

- **`Message<Null, string>.Headers` is null by default** — always initialize with `Headers = []` when creating Kafka messages.
- **OTel custom Meter must be registered** — add `.AddMeter("MeterName")` in `.WithMetrics()` or custom metrics won't appear in `/metrics`.
- **BackgroundService + DI injection**: use `AddSingleton<T>()` + `AddHostedService(sp => sp.GetRequiredService<T>())` so other services can inject `T`. `AddHostedService<T>()` alone only registers it as `IHostedService`.
- **.NET 10 record validation**: DataAnnotations (`[Required]`, `[Range]`, `[MinLength]`) must be on constructor parameters, not `[property: X]`. JSON attributes (`[JsonPropertyName]`) stay on properties.
- **MQTTnet v5.x API**: Use `MqttClientFactory` (not `MqttFactory`), `P.Load` (not `P.PayloadSegment`). `MqttClientFactory` is in the main `MQTTnet` package — `MQTTnet.Extensions.ManagedClient` is incompatible.
- **Confluent.Kafka `EnableIdempotence=true`** requires PID acquisition on first produce. Kafka must be fully started before the first request, or the producer will fail with "Coordinator load in progress". On restart, kill Gateway and restart fresh.
- **Polly.Core v8**: `ResiliencePipeline<T>.ExecuteAsync(Func<CancellationToken, ValueTask<T>>)` — only `CancellationToken` overload, no `CancellationToken+State` overload.

## Current Project State

**Step 1 complete.** Solution skeleton, shared infrastructure, OTel/Serilog observability.

**Step 2 complete.** IoTGateway dual-protocol ingress with Kafka ack boundary:
- HTTP `POST /api/v1/telemetry` → 202 (Kafka ack) / 503 (Kafka unavailable)
- HTTP `POST /api/v1/events/critical` → 202 / 503, reliability_level=2
- MQTT WebSocket shared subscriptions: `$share/gateway-group/device/+/telemetry`, `device/+/event/critical`
- Kafka headers: `event_id`, `schema_version`, `reliability_level`
- Polly resilience: 3 retries (100→200→400ms), 4s total timeout
- Health checks: `/health/ready` covers Kafka + MQTT connectivity
- Prometheus metrics: `gateway_requests_total`, `gateway_rejected_total`, `gateway_kafka_ack_latency_ms`, `mqtt_messages_total`
- Docker infra: Kafka 7.6.0 + Zookeeper + Mosquitto 2.0.18 (MQTT:1883, WebSocket:8083)
- All 6 end-to-end tests passed

**Known issue**: W3C `traceparent` not appearing in Kafka headers. Root cause: `ActivitySource("IoTGateway.Kafka")` has no OTel listener → `StartActivity` returns null → `activity?.Context ?? default` gives default context → W3C propagator skips injection. Fix pending.

**Next**: Step 3 — BackendProcessor with 3 Kafka consumer workers.

Note: `OpenTelemetry.Exporter.Prometheus.AspNetCore` was installed with `--prerelease` (only 1.15.3-beta.1 is available; no stable release yet).

## Key Dependencies

- **Messaging**: Confluent.Kafka 2.14, MQTTnet 5.1 (gateway + simulator)
- **Persistence**: Npgsql 10.0 (BackendProcessor), StackExchange.Redis 2.12 (BackendProcessor)
- **Resilience**: Polly.Core 8.6 (gateway)
- **Observability**: OpenTelemetry 1.15, Serilog 4.3
- **Runtime**: .NET 10 (SDK pinned via `global.json` to 10.0.202, `rollForward: latestFeature`)
- **Quality**: `Directory.Build.props` sets `TreatWarningsAsErrors=true`, `AnalysisLevel=latest`, `Nullable=enable` across all projects

## Reference Documents

- `1、架构最终确定版.md` — Complete architecture spec V3.0 with all 21 ADRs and 8-step execution plan
- `2、第一步开发计划.md` — Detailed Step 1 implementation guide with exact code and commands
- `3、第二步开发计划.md` — Step 2 implementation guide (gateway ingress layer)
