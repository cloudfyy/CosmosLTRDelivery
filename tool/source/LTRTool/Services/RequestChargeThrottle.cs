using System.Diagnostics;

namespace CosmosLab.LTRTool;

public sealed class RequestChargeThrottle
{
    private readonly double maxRuPerSecond;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double available;
    private double lastTimestamp;

    public RequestChargeThrottle(double maxRuPerSecond)
    {
        if (!double.IsFinite(maxRuPerSecond) || maxRuPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRuPerSecond), "The export RU cap must be positive.");
        }
        this.maxRuPerSecond = maxRuPerSecond;
        available = maxRuPerSecond;
    }

    public async ValueTask<TokenBucketLease> AcquireAsync(
        double requestCharge,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(requestCharge) || requestCharge <= 0)
        {
            return new TokenBucketLease(requestCharge, 0);
        }

        var required = Math.Min(requestCharge, maxRuPerSecond);
        var totalDelay = TimeSpan.Zero;
        while (true)
        {
            TimeSpan delay;
            await gate.WaitAsync(cancellationToken);
            try
            {
                var now = clock.Elapsed.TotalSeconds;
                var elapsed = Math.Max(0, now - lastTimestamp);
                available = Math.Min(maxRuPerSecond, available + elapsed * maxRuPerSecond);
                lastTimestamp = now;
                if (available >= required)
                {
                    available -= required;
                    return new TokenBucketLease(requestCharge, totalDelay.TotalMilliseconds);
                }
                var missing = required - available;
                delay = TimeSpan.FromSeconds(Math.Max(0.001, missing / maxRuPerSecond));
                available = 0;
                totalDelay += delay;
            }
            finally
            {
                gate.Release();
            }
            await Task.Delay(delay, cancellationToken);
        }
    }
}
