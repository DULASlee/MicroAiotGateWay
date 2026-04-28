# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

IoTHunter is a .NET 10 cloud-native dual-protocol (HTTP + MQTT-over-WebSocket) IoT data gateway for smart wristbands. It uses Kafka as the reliability boundary with projected CQRS for storage.

## Build & Run Commands

```bash
# Build (0 warnings, 0 errors required)
dotnet build

# Run individual services (ports: IoTGateway=5080, BackendProcessor=5081)
dotnet run --project IoTHunter/IoTGateway/IoTGateway.csproj
dotnet run --project IoTHunter/BackendProcessor/BackendProcessor.csproj
dotnet run --project IoTHunter/DeviceSimulator/DeviceSimulator.csproj

# Verify services
curl http://localhost:5080/health/ready
curl http://localhost:5080/metrics
curl http://localhost:5081/health/ready
curl http://localhost:5081/metrics

# Add a NuGet package (always use dotnet add, never hand-edit versions)
dotnet add IoTHunter/SomeProject/SomeProject.csproj package PackageName
```

## Architecture

**Solution layout** (`IoTHunter/` directory, `IoTHunter.slnx` solution file):

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

## Hard Architectural Rules (ADRs)

These are non-negotiable decisions from the architecture document:

- **ADR-001**: No Consul. K8s DNS + Service only.
- **ADR-002**: Gateway must never reference any database driver. Kafka is the only persistence downstream.
- **ADR-003**: `EnableAutoCommit = false` for all Kafka consumers. Each projection worker has independent consumer group and commits after its own successful write.
- **ADR-004**: OpenTelemetry W3C Trace Context must be injected/extracted in Kafka headers via `Propagators.DefaultTextMapPropagator`.
- **ADR-005**: `event_id` is the universal idempotency key. PG uses `ON CONFLICT (event_id) DO NOTHING`. Redis latest-value updates must compare `recorded_at` or `sequence` to reject stale data.
- **ADR-007**: Never use `PublishSingleFile` + `linux-musl`. Always multi-file publish.
- **ADR-018**: PG writes use batch `INSERT ... ON CONFLICT (event_id) DO NOTHING`. No raw COPY.

## Current Project State

**Step 1 is complete.** All four projects build with 0 warnings and 0 errors. Verification:
- `IoTGateway` on `http://localhost:5080` — `/health/ready` returns 200, `/metrics` exposes Prometheus text
- `BackendProcessor` on `http://localhost:5081` — `/health/ready` returns 200, `/metrics` exposes Prometheus text
- `DeviceSimulator` uses Generic Host + `PlaceholderWorker`, stays running (no flash exit)
- All console logs include `trace=<TraceId> span=<SpanId>` fields
- OtlpEndpoint defaults to empty string (no OTel Collector deployed yet)

Next: **Step 2** — IoTGateway dual-protocol ingress with Kafka ack boundary.

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
