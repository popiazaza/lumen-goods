using Microsoft.Extensions.Options;

namespace KestrelOutfitters.StandIn;

/// <summary>
/// The one source of randomness behind the contract's quirks (feed latency, feed 503s, order decision delay),
/// so a configured seed replays the same sequence.
/// </summary>
internal sealed class ContractRandom(IOptions<KestrelOutfittersOptions> options)
{
    private readonly Random _random = options.Value.RandomSeed is { } seed ? new Random(seed) : new Random();
    private readonly Lock _gate = new();

    /// <summary>True with the given probability: 0 never, 1 always.</summary>
    public bool Chance(double probability)
    {
        lock (_gate)
        {
            return _random.NextDouble() < probability;
        }
    }

    /// <summary>Uniformly distributed between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public TimeSpan Between(TimeSpan min, TimeSpan max)
    {
        lock (_gate)
        {
            return min + ((max - min) * _random.NextDouble());
        }
    }
}
