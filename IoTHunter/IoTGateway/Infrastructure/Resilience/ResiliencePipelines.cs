using Confluent.Kafka;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace IoTGateway.Infrastructure.Resilience;

internal static class ResiliencePipelines
{
    public static ResiliencePipeline<DeliveryResult<Null, string>> BuildKafkaPipeline()
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
                    Console.WriteLine(
                        "[Polly] Kafka produce retry {Attempt}/3 after {DelayMs}ms",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(4))
            .Build();
    }
}
