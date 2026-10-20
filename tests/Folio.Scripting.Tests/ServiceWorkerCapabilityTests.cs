using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Resources;
using System.Text;

namespace Folio.Scripting.Tests;

public sealed class ServiceWorkerCapabilityTests
{
    [Fact]
    public void NativeAuthorityChecksDocumentRetirementEvenBeforeItsOwnDisposal()
    {
        var documentLive = true;
        using var authority = new ServiceWorkerCapabilityAuthority(true, () => documentLive);
        Assert.True(authority.TryAcquire(out var lease));
        documentLive = false;
        Assert.False(authority.Enabled);
        Assert.False(lease!.IsActive);
        Assert.False(authority.TryAcquire(out _));
        Assert.True(authority.LoadCancellation.IsCancellationRequested);
    }

    [Fact]
    public void RevocationCancelsOldScriptLoadsAndRegrantUsesFreshCancellation()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        var original = authority.LoadCancellation;
        authority.UpdateCapabilities(false);
        Assert.True(original.IsCancellationRequested);
        authority.UpdateCapabilities(true);
        var replacement = authority.LoadCancellation;
        Assert.False(replacement.IsCancellationRequested);
        Assert.True(original.IsCancellationRequested);
        authority.Dispose();
        Assert.True(replacement.IsCancellationRequested);
    }

    [Fact]
    public void ThrowingCancellationCallbackCannotPreventRetirementNotification()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        var notified = false;
        authority.Changed = () => notified = true;
        using var registration = authority.LoadCancellation.Register(() => throw new InvalidOperationException("loader cancellation callback"));
        authority.UpdateCapabilities(false);
        Assert.True(notified);
        Assert.False(authority.Enabled);
    }

    [Fact]
    public void PublicApiRevocationRetiresRegistrationAndRegrantUsesFreshScript()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));

        Assert.True(engine.Evaluate("let oldRegistration; navigator.serviceWorker.register('old.js').then(r => oldRegistration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("oldRegistration && oldRegistration.active && oldRegistration.active.state === 'activated'").AsBoolean());
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        Assert.True(engine.Evaluate("let oldError = ''; oldRegistration.update().catch(e => oldError = e.name); try { oldRegistration.active.postMessage('x'); } catch (e) { oldError = e.name; } true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("InvalidStateError", engine.Evaluate("oldError").AsString());

        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { ServiceWorkers = true });
        Assert.True(engine.Evaluate("let newRegistration; navigator.serviceWorker.register('new.js').then(r => newRegistration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("new.js", engine.Evaluate("newRegistration.scriptURL").AsString());
    }

    private sealed class TestServiceWorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(request.Url.EndsWith("new.js", StringComparison.Ordinal) ? "self.addEventListener('fetch', e => e.respondWith(new Response('new')));" : "self.addEventListener('fetch', e => e.respondWith(new Response('old')));"), "application/javascript")
                : ResourceResponse.Refused("unsupported"));
    }

    [Fact]
    public void DisabledAuthorityDoesNotAcquireLeases()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(false);

        Assert.False(authority.TryAcquire(out var lease));
        Assert.Null(lease);
    }

    [Fact]
    public void RevocationRetiresExistingLeasesAndRegrantRequiresFreshLease()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var oldLease));
        Assert.NotNull(oldLease);
        Assert.True(oldLease!.IsActive);

        authority.UpdateCapabilities(false);
        Assert.False(oldLease.IsActive);
        Assert.False(authority.TryAcquire(out _));

        authority.UpdateCapabilities(true);
        Assert.True(authority.TryAcquire(out var newLease));
        Assert.NotNull(newLease);
        Assert.True(newLease!.IsActive);
        Assert.False(oldLease.IsActive);
    }

    [Fact]
    public void DisposedAuthorityRetiresAllLeases()
    {
        var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var lease));
        authority.Dispose();

        Assert.False(authority.Enabled);
        Assert.False(lease!.IsActive);
        Assert.False(authority.TryAcquire(out _));
        authority.Dispose();
    }

    [Fact]
    public void RepeatedGrantDoesNotRetireAnUnchangedLease()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var lease));

        authority.UpdateCapabilities(true);

        Assert.True(lease!.IsActive);
    }
}
