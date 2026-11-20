using Lumen.Domain.Payments;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.UnitTests.Payments;

public sealed class OneTimeCodePolicyTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly OneTimeCodePolicy _policy = new(new PaymentOptions());

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_right_code_at_4_59_is_approved()
    {
        // Scenario 1.
        var issuedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        _policy.Check(issuedAt, _clock.GetUtcNow(), "123456").Should().Be(OneTimeCodeResult.Approved);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_right_code_at_5_01_has_expired()
    {
        // Scenario 2.
        var issuedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        _policy.Check(issuedAt, _clock.GetUtcNow(), "123456").Should().Be(OneTimeCodeResult.Expired);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_code_expires_at_exactly_5_minutes()
    {
        var issuedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(5));

        _policy.ExpiresAt(issuedAt).Should().Be(_clock.GetUtcNow());
        _policy.Check(issuedAt, _clock.GetUtcNow(), "123456").Should().Be(OneTimeCodeResult.Expired);
    }

    [Theory]
    [Trait("BR", "BR-PAY-03")]
    [InlineData("654321")] // scenario 3
    [InlineData("123457")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("１２３４５６")] // full-width digits
    [InlineData("")]
    [InlineData(null)]
    public void BR_PAY_03_any_other_code_is_declined(string? code) =>
        _policy.Check(_clock.GetUtcNow(), _clock.GetUtcNow(), code).Should().Be(OneTimeCodeResult.Declined);

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_a_wrong_code_after_expiry_is_reported_as_expired()
    {
        var issuedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromMinutes(6));

        _policy.Check(issuedAt, _clock.GetUtcNow(), "654321").Should().Be(OneTimeCodeResult.Expired);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_spaces_around_the_code_are_ignored() =>
        _policy.Check(_clock.GetUtcNow(), _clock.GetUtcNow(), " 123456 ").Should().Be(OneTimeCodeResult.Approved);

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_code_and_its_lifetime_are_configurable()
    {
        var policy = new OneTimeCodePolicy(new PaymentOptions { OneTimeCode = "000111", OneTimeCodeLifetime = TimeSpan.FromMinutes(2) });
        var issuedAt = _clock.GetUtcNow();

        policy.TestCode.Should().Be("000111");
        policy.Check(issuedAt, issuedAt.AddSeconds(119), "000111").Should().Be(OneTimeCodeResult.Approved);
        policy.Check(issuedAt, issuedAt.AddSeconds(119), "123456").Should().Be(OneTimeCodeResult.Declined);
        policy.Check(issuedAt, issuedAt.AddMinutes(2), "000111").Should().Be(OneTimeCodeResult.Expired);
    }

    [Fact]
    [Trait("BR", "BR-PAY-03")]
    public void BR_PAY_03_the_page_can_show_the_test_code() =>
        _policy.TestCode.Should().Be("123456");
}
