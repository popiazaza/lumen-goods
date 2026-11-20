namespace Lumen.IntegrationTests.Infrastructure;

/// <summary>
/// Tests that release many requests at the same moment. They run on their own, after the parallel tests: a burst of
/// 50 requests needs up to 50 of PostgreSQL's 100 connections, and the race should be between its own callers only.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManyCallersCollection
{
    public const string Name = "Many concurrent callers";
}

public static class ManyCallers
{
    /// <summary>
    /// Starts every call only once all of them are ready, so they reach the API together rather than one after another.
    /// </summary>
    public static async Task<T[]> AllAtOnceAsync<T>(IEnumerable<Func<Task<T>>> calls)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = calls.Select(async call =>
        {
            await start.Task;
            return await call();
        }).ToList();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    /// <summary>Runs <paramref name="check"/> again and again (at least once) until <paramref name="race"/> has ended.</summary>
    public static async Task CheckWhileRunningAsync(Task race, Func<Task> check)
    {
        do
        {
            await check();
        }
        while (!race.IsCompleted);
    }
}
