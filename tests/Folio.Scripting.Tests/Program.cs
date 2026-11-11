using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.Scripting.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["rtc-chrome-probe", var chrome])
            return ChromeRtcProbe.Run(chrome);
        if (args is ["rtc-chrome-probe", var answeringChrome, "folio-answerer"])
            return ChromeRtcProbe.Run(answeringChrome, folioAnswerer: true);
        if (args is ["dtls-openssl-probe", var openssl])
            return OpenSslDtlsProbe.Run(openssl);
        if (args is ["dtls-openssl-probe", var serverOpenSsl, "folio-server"])
            return OpenSslDtlsProbe.Run(serverOpenSsl, folioServer: true);
        if (args is ["worker-thread-pool-probe"])
            return WorkerThreadPoolProbe();
        if (args is ["service-worker-load-deadline-probe"])
            return ServiceWorkerSafetyLifecycleTests.RunLoadDeadlineProbe();
        if (args is ["rtc-thread-pool-probe"])
            return RtcThreadPoolProbe.Run();
        if (args is ["gpu-thread-pool-probe"])
            return GpuThreadPoolProbe.Run();
        // Mirror xUnit's generated entry point for ordinary test runs.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    private static int WorkerThreadPoolProbe()
    {
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.GetMaxThreads(out _, out var maxIo);
        if (!ThreadPool.SetMinThreads(1, minIo) || !ThreadPool.SetMaxThreads(1, maxIo)) return 2;
        // Occupy the sole pool thread. Worker initialization must use its own
        // execution thread instead of waiting for another pool work item.
        var result = Task.Run(() =>
        {
            using var engine = new JintScriptEngine();
            using var document = Document.Parse("<html></html>", new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Workers = true }
            });
            DomBindings.Install(engine, document);
            return engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    'onmessage = function(event) { postMessage(event.data + 1); };'));
                let answer = 0;
                worker.onmessage = event => answer = event.data;
                worker.postMessage(41);
                answer === 42;
                """).AsBoolean();
        }).GetAwaiter().GetResult();
        return result ? 0 : 1;
    }
}
