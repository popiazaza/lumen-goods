using System.Globalization;

namespace Lumen.Api.Orders;

/// <summary>
/// When the partner order job next sends or polls a partner order (BR-ORD-01, BR-ORD-04). Pure, so the timing rules are
/// unit-tested without a database.
/// </summary>
internal static class PartnerOrderSchedule
{
    /// <summary>BR-ORD-04: a partner order not accepted by then is given up (after one last send at that moment).</summary>
    public static DateTimeOffset GiveUpAt(DateTimeOffset paidUtc, PartnerOrdersOptions options) => paidUtc + options.GiveUpAfter;

    /// <summary>BR-ORD-04: an accepted partner order still undecided at this moment needs an admin.</summary>
    public static DateTimeOffset NeedsAttentionAt(DateTimeOffset acceptedUtc, PartnerOrdersOptions options) => acceptedUtc + options.NeedsAttentionAfter;

    /// <summary>
    /// The next send after a send that got no answer: <see cref="PartnerOrdersOptions.RetryDelay"/>, doubling with each
    /// attempt up to <see cref="PartnerOrdersOptions.MaxRetryDelay"/>, but never later than the give-up moment, so the
    /// last attempt happens right at the deadline. A partner's Retry-After always wins, even over the deadline: the
    /// order is then given up at the first send after it.
    /// </summary>
    /// <param name="attempts">Calls made for this partner order so far, the failed one included.</param>
    public static DateTimeOffset NextSend(int attempts, DateTimeOffset now, TimeSpan? retryAfter, DateTimeOffset giveUpAt, PartnerOrdersOptions options)
    {
        var next = now + Backoff(attempts, options.RetryDelay, options.MaxRetryDelay);
        if (next > giveUpAt)
        {
            next = giveUpAt < now ? now : giveUpAt;
        }

        return retryAfter is { } wait && now + wait > next ? now + wait : next;
    }

    /// <summary>
    /// The next poll of an accepted ticket: every <see cref="PartnerOrdersOptions.PollInterval"/> at first (Kestrel decides
    /// within seconds), then backing off to half the time already waited, at most <see cref="PartnerOrdersOptions.MaxPollInterval"/>.
    /// Tied to the time since acceptance rather than a counter, so the pace survives restarts.
    /// </summary>
    public static DateTimeOffset NextPoll(DateTimeOffset acceptedUtc, DateTimeOffset now, TimeSpan? retryAfter, PartnerOrdersOptions options)
    {
        var waited = now - acceptedUtc;
        var delay = waited / 2;
        if (delay < options.PollInterval)
        {
            delay = options.PollInterval;
        }

        if (delay > options.MaxPollInterval)
        {
            delay = options.MaxPollInterval;
        }

        return retryAfter is { } wait && wait > delay ? now + wait : now + delay;
    }

    public static TimeSpan Backoff(int attempts, TimeSpan first, TimeSpan max)
    {
        var doublings = Math.Clamp(attempts - 1, 0, 30);
        var ticks = first.Ticks * Math.Pow(2, doublings);
        return ticks >= max.Ticks ? max : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>"30 minutes", "1 hour", "24 hours": for reasons customers and admins read.</summary>
    public static string Describe(TimeSpan duration)
    {
        if (duration.TotalHours >= 1 && duration.Ticks % TimeSpan.TicksPerHour == 0)
        {
            var hours = (long)duration.TotalHours;
            return hours == 1 ? "1 hour" : $"{hours.ToString(CultureInfo.InvariantCulture)} hours";
        }

        if (duration.TotalMinutes >= 1 && duration.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            var minutes = (long)duration.TotalMinutes;
            return minutes == 1 ? "1 minute" : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes";
        }

        return $"{((long)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} seconds";
    }
}
