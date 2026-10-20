using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WorkerFetchContextTests
{
    [Fact]
    public void StoredBodiesShareMessageBudgetAndReleaseIt()
    {
        using var engine = new JintScriptEngine();
        var budget = ScriptMessageBudget.For(engine, 8);
        using var context = new WorkerFetchContext(null, null, default, budget);
        var handle = context.StoreFetchBody(new byte[8]);
        Assert.Throws<WebIdlException>(() => context.StoreFetchBody(new byte[1]));
        context.FreeFetchBody(handle);
        Assert.Null(context.FetchBody(handle));
        context.StoreFetchBody(new byte[8]);
        context.Dispose();
        using var reservation = budget.Reserve(8);
    }

    [Fact]
    public async Task WorkerCancellationCancelsAnOutstandingHostLoad()
    {
        using var engine = new JintScriptEngine();
        using var cancellation = new CancellationTokenSource();
        var loader = new WaitingLoader();
        using var context = new WorkerFetchContext(null, loader, cancellation.Token, ScriptMessageBudget.For(engine, 1024));
        var fetch = Task.Run(() => context.Fetch("https://worker.example/data"));
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fetch.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        await loader.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    private sealed class WaitingLoader : IResourceLoader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return null; }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
        }
    }
}
