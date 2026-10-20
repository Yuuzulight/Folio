namespace Folio.WinForms;

/// <summary>Preserves document-command order even when earlier writes are asynchronous.</summary>
internal sealed class ContentCommandQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    internal Task Enqueue(Func<ValueTask> command)
    {
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            previous = _tail;
            _tail = completion.Task;
        }
        _ = RunAsync();
        return completion.Task;

        async Task RunAsync()
        {
            try
            {
                // A failed old document must not prevent a later document from loading.
                try { await previous.ConfigureAwait(false); } catch { }
                await command().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
