using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using IoTHunter.Shared.Domain;
using IoTHunter.Shared.Infrastructure;
using IoTGateway.Infrastructure.Options;
using IoTGateway.Infrastructure.Resilience;
using OpenTelemetry;
using Polly;
using OpenTelemetry.Context.Propagation;

namespace IoTGateway.Infrastructure.Messaging;

public sealed class KafkaProducerService : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("IoTGateway.Kafka");
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly IProducer<Null, string> _producer;
    private readonly ResiliencePipeline<DeliveryResult<Null, string>> _pipeline;
    private readonly ILogger<KafkaProducerService> _logger;

    public KafkaProducerService(
        KafkaOptions options,
        KafkaResiliencePipeline pipeline,
        ILogger<KafkaProducerService> logger)
    {
        _pipeline = pipeline.Build();
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = options.MaxInFlight,
            MessageSendMaxRetries = 3,
            MessageTimeoutMs = options.MessageTimeoutMs,
            ClientId = options.ClientId,
            SocketKeepaliveEnable = true
        };

        _producer = new ProducerBuilder<Null, string>(config).Build();
        _logger.LogInformation("Kafka producer initialized: {BootstrapServers} client={ClientId}",
            options.BootstrapServers, options.ClientId);
    }

    public async Task<DeliveryResult<Null, string>> ProduceAsync(
        string topic, TelemetryEnvelope envelope, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(envelope, SerializerSetup.TightOptions);
        var message = new Message<Null, string> { Value = json, Headers = [] };

        var parentActivity = Activity.Current;
        using var activity = ActivitySource.StartActivity(
            "Kafka.Produce", ActivityKind.Producer, parentActivity?.Context ?? default);

        // ADR-004: Inject W3C Trace Context into Kafka Headers
        Propagator.Inject(
            new PropagationContext(activity?.Context ?? default, Baggage.Current),
            message.Headers,
            (headers, key, value) => headers.Add(key, Encoding.UTF8.GetBytes(value)));

        // Business metadata headers
        message.Headers.Add("event_id", Encoding.UTF8.GetBytes(envelope.EventId));
        message.Headers.Add("schema_version", Encoding.UTF8.GetBytes(envelope.SchemaVersion.ToString()));
        message.Headers.Add("reliability_level", Encoding.UTF8.GetBytes(((int)envelope.ReliabilityLevel).ToString()));

        activity?.SetTag("deviceId", envelope.DeviceId);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _pipeline.ExecuteAsync(
                async innerCt => await _producer.ProduceAsync(topic, message, innerCt), ct);

            stopwatch.Stop();
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination", topic);
            activity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            activity?.SetTag("messaging.kafka.offset", result.Offset.Value);

            _logger.LogInformation(
                "Kafka ack {EventId} topic={Topic} partition={Partition} offset={Offset} latency={LatencyMs}ms",
                envelope.EventId, topic, result.Partition.Value, result.Offset.Value, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex,
                "Kafka produce failed {EventId} topic={Topic} latency={LatencyMs}ms",
                envelope.EventId, topic, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public void Ping()
    {
        using var admin = new DependentAdminClientBuilder(_producer.Handle).Build();
        admin.GetMetadata(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
