using Lumen.Api.Payments;

namespace Lumen.UnitTests.Payments;

public sealed class PaymentRecorderTextTests
{
    [Theory]
    [InlineData(300, "5 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(260, "4 minutes 20 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(-30, "0 seconds")]
    public void The_one_time_code_event_states_how_long_the_code_is_valid_not_a_utc_clock_time(int seconds, string expected) =>
        PaymentRecorder.Validity(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
