using Confluent.Kafka;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace IoTGateway.Infrastructure.Resilience;

public sealed class KafkaResiliencePipeline
{
    private readonly ILogger<KafkaResiliencePipeline> _logger;

    public KafkaResiliencePipeline(ILogger<KafkaResiliencePipeline> logger)
    {
        _logger = logger;
    }

    public ResiliencePipeline<DeliveryResult<Null, string>> Build()
    {
        return new ResiliencePipelineBuilder<DeliveryResult<Null, string>>()
            .AddRetry(new RetryStrategyOptions<DeliveryResult<Null, string>>
            {
                ShouldHandle = new PredicateBuilder<DeliveryResult<Null, string>>()
                    .Handle<ProduceException<Null, string>>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(100),
                BackoffType = DelayBackoffType.Exponential,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Kafka produce retry {Attempt}/{MaxAttempts} after {DelayMs}ms",
                        args.AttemptNumber, 3, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(4))
            .Build();
    }
}
