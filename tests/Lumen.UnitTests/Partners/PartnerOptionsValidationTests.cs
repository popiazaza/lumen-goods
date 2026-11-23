using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Lumen.UnitTests.Partners.PartnerTestKit;

namespace Lumen.UnitTests.Partners;

/// <summary>
/// Time spans the partner jobs and the image proxy feed to timers, caches and timeouts. A zero or negative one would
/// only blow up after start-up (PeriodicTimer throws inside a hosted job, which stops the host), so it is refused at start.
/// </summary>
public sealed class PartnerOptionsValidationTests
{
    [Theory]
    [InlineData("Jobs:PollInterval", "00:00:00")]
    [InlineData("Jobs:PollInterval", "-00:00:05")]
    [InlineData("Partners:Media:CacheDuration", "00:00:00")]
    [InlineData("Partners:Media:FailureCacheDuration", "00:00:00")]
    [InlineData("Partners:Media:ResponseTimeout", "00:00:00")]
    [InlineData("Partners:Media:MaxFetchDuration", "00:00:00")]
    public async Task A_time_span_that_is_not_positive_is_refused_at_start(string key, string value)
    {
        using var services = Services(overrides: new() { [key] = value });

        var act = () => services.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage($"*{key[(key.LastIndexOf(':') + 1)..]}*");
    }

    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:11")]
    public async Task An_image_rate_limit_wait_outside_0_to_10_seconds_is_refused_at_start(string value)
    {
        // Longer would let queued image requests hold the browser's few connections again.
        using var services = Services(overrides: new() { ["Partners:Media:RateLimitWait"] = value });

        var act = () => services.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*RateLimitWait*");
    }

    [Theory]
    [InlineData("Partners:AttributeKeySynonyms:fill power", "fillPower")]
    [InlineData("Partners:AttributeKeySynonyms:colour", "Color")]
    [InlineData("Partners:AttributeKeySynonyms:colour", "colour shade")]
    [InlineData("Partners:AttributeKeySynonyms:colour", "")]
    public async Task An_attribute_key_synonym_that_is_not_lower_camel_case_on_both_sides_is_refused_at_start(string key, string value)
    {
        using var services = Services(overrides: new() { [key] = value });

        var act = () => services.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OptionsValidationException>()).WithMessage("*AttributeKeySynonyms*");
    }

    [Fact]
    public async Task The_default_settings_pass_start_up_validation()
    {
        using var services = Services();

        var act = () => services.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
