using Lumen.Api.Orders;

namespace Lumen.UnitTests.Orders;

/// <summary>When the partner order job sends again or polls again (BR-ORD-01, BR-ORD-04), with the default settings.</summary>
public sealed class PartnerOrderScheduleTests
{
    private static readonly PartnerOrdersOptions Options = new();
    private static readonly DateTimeOffset Paid = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resends_back_off_from_the_retry_delay_doubling_up_to_the_maximum()
    {
        PartnerOrderSchedule.Backoff(1, Options.RetryDelay, Options.MaxRetryDelay).Should().Be(TimeSpan.FromSeconds(15));
        PartnerOrderSchedule.Backoff(2, Options.RetryDelay, Options.MaxRetryDelay).Should().Be(TimeSpan.FromSeconds(30));
        PartnerOrderSchedule.Backoff(4, Options.RetryDelay, Options.MaxRetryDelay).Should().Be(TimeSpan.FromSeconds(120));
        PartnerOrderSchedule.Backoff(7, Options.RetryDelay, Options.MaxRetryDelay).Should().Be(TimeSpan.FromMinutes(5));
        PartnerOrderSchedule.Backoff(500, Options.RetryDelay, Options.MaxRetryDelay).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_the_last_resend_happens_at_the_give_up_moment_not_after_it()
    {
        var giveUpAt = PartnerOrderSchedule.GiveUpAt(Paid, Options);
        giveUpAt.Should().Be(Paid.AddMinutes(30));

        var now = Paid.AddMinutes(28);
        PartnerOrderSchedule.NextSend(attempts: 8, now, retryAfter: null, giveUpAt, Options).Should().Be(giveUpAt);
        PartnerOrderSchedule.NextSend(attempts: 1, Paid, retryAfter: null, giveUpAt, Options).Should().Be(Paid.AddSeconds(15));
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_a_partners_retry_after_is_always_honoured()
    {
        var giveUpAt = PartnerOrderSchedule.GiveUpAt(Paid, Options);

        PartnerOrderSchedule.NextSend(attempts: 1, Paid, TimeSpan.FromSeconds(40), giveUpAt, Options).Should().Be(Paid.AddSeconds(40));
        PartnerOrderSchedule.NextSend(attempts: 1, Paid.AddMinutes(29), TimeSpan.FromMinutes(5), giveUpAt, Options)
            .Should().Be(Paid.AddMinutes(34), "Retry-After wins even over the deadline; the order is given up at that send");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_an_accepted_ticket_is_polled_often_at_first_then_less_often()
    {
        var accepted = Paid;

        PartnerOrderSchedule.NextPoll(accepted, accepted, null, Options).Should().Be(accepted.AddSeconds(10));
        PartnerOrderSchedule.NextPoll(accepted, accepted.AddSeconds(60), null, Options).Should().Be(accepted.AddSeconds(90));
        PartnerOrderSchedule.NextPoll(accepted, accepted.AddHours(3), null, Options).Should().Be(accepted.AddHours(3).AddMinutes(5));
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_an_accepted_order_needs_attention_24_hours_after_acceptance()
    {
        PartnerOrderSchedule.NeedsAttentionAt(Paid, Options).Should().Be(Paid.AddHours(24));
    }

    [Theory]
    [InlineData(30 * 60, "30 minutes")]
    [InlineData(60 * 60, "1 hour")]
    [InlineData(24 * 60 * 60, "24 hours")]
    [InlineData(90, "90 seconds")]
    public void Durations_read_as_people_say_them(int seconds, string expected) =>
        PartnerOrderSchedule.Describe(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
