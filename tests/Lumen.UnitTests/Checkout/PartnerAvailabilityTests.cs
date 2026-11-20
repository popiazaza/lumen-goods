using Lumen.Api.Checkout;
using Lumen.Api.Partners.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.UnitTests.Checkout;

/// <summary>BR-CHK-02: all partners are checked at once within one 5 s budget, and anything not answered is not sold.</summary>
public sealed class PartnerAvailabilityTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PartnerAvailability Create(params IPartnerAvailabilityCheck[] checks) =>
        new(checks, new FixedOptions(new CheckoutOptions { PartnerCheckTimeout = TimeSpan.FromSeconds(5) }), _clock, NullLogger<PartnerAvailability>.Instance);

    private static PartnerLineToCheck Line(string partner) => new(partner, new PartnerLineCheck(Guid.NewGuid(), "X-1", 1, 10.00m));

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_a_partner_silent_for_5_seconds_is_unreachable_while_the_other_partner_answers()
    {
        var harbor = Line("harbor");
        var kestrel = Line("kestrel");
        var sut = Create(new Hanging("harbor"), new Answering("kestrel", PartnerLineCheckOutcome.Ok));

        var checking = sut.CheckAsync([harbor, kestrel], Ct);
        checking.IsCompleted.Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(5));
        var results = await checking.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        results[harbor.Line.LineId].Outcome.Should().Be(PartnerLineCheckOutcome.Unreachable);
        results[kestrel.Line.LineId].Outcome.Should().Be(PartnerLineCheckOutcome.Ok);
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public async Task BR_CHK_02_a_throwing_adapter_an_unknown_partner_or_a_missing_answer_is_unreachable()
    {
        var thrown = Line("harbor");
        var unknown = Line("acme");
        var forgotten = Line("kestrel");
        var sut = Create(new Throwing("harbor"), new Forgetful("kestrel"));

        var results = await sut.CheckAsync([thrown, unknown, forgotten], Ct);

        results.Values.Should().HaveCount(3).And.OnlyContain(r => r.Outcome == PartnerLineCheckOutcome.Unreachable);
    }

    [Fact]
    public async Task No_partner_lines_means_no_partner_call()
    {
        (await Create(new Throwing("harbor")).CheckAsync([], Ct)).Should().BeEmpty();
    }

    private sealed class FixedOptions(CheckoutOptions value) : IOptionsMonitor<CheckoutOptions>
    {
        public CheckoutOptions CurrentValue => value;

        public CheckoutOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CheckoutOptions, string?> listener) => null;
    }

    private sealed class Hanging(string partner) : IPartnerAvailabilityCheck
    {
        public string Partner => partner;

        public async Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken)
        {
            // Ignores the token on purpose: the budget must hold even for an adapter that does not cooperate.
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return [];
        }
    }

    private sealed class Answering(string partner, PartnerLineCheckOutcome outcome) : IPartnerAvailabilityCheck
    {
        public string Partner => partner;

        public Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PartnerLineCheckResult>>([.. lines.Select(l => new PartnerLineCheckResult(l.LineId, outcome))]);
    }

    private sealed class Throwing(string partner) : IPartnerAvailabilityCheck
    {
        public string Partner => partner;

        public Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("adapter bug");
    }

    private sealed class Forgetful(string partner) : IPartnerAvailabilityCheck
    {
        public string Partner => partner;

        public Task<IReadOnlyList<PartnerLineCheckResult>> CheckAsync(IReadOnlyList<PartnerLineCheck> lines, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PartnerLineCheckResult>>([]);
    }
}
