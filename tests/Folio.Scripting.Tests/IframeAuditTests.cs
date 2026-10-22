using Folio;

using Folio.Hosting;

using Folio.Scripting.Dom;

using Folio.Scripting.Jint;



namespace Folio.Scripting.Tests;



public sealed class IframeAuditTests

{

    [Fact]

    public void IframeSrcdocRunsScriptsWhenSandboxAllowsScriptsAndInheritsWorkerCapability()

    {

        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, Workers = true });

        try

        {

            var ran = engine.Evaluate("""

                const frame = document.createElement('iframe');

                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');

                frame.allow = 'workers';

                frame.srcdoc = "<script>document.body.setAttribute('data-ran', typeof Worker)</script>";

                document.body.appendChild(frame);

                frame.contentDocument.body.getAttribute('data-ran') === 'function';

                """).AsBoolean();

            Assert.True(ran);

        }

        finally { DomBindings.Dispose(engine); }

    }



    [Fact]

    public void IframeNavigationCleansUpHandlersFromThePreviousChildContext()

    {

        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true });

        try

        {

            var cleaned = engine.Evaluate("""

                const frame = document.createElement('iframe');

                frame.srcdoc = '<p>first</p>';

                document.body.appendChild(frame);

                let messages = 0;

                frame.contentWindow.onmessage = () => messages++;

                frame.contentWindow.postMessage('before', '*');

                frame.srcdoc = '<p>second</p>';

                frame.contentWindow.postMessage('after', '*');

                messages === 1;

                """).AsBoolean();

            Assert.True(cleaned);

        }

        finally { DomBindings.Dispose(engine); }

    }



    [Fact]

    public void IframePostMessageTargetOriginValidationBothWays()

    {

        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true });

        try

        {

            var passed = engine.Evaluate("""

                let parentReceived = [];

                window.onmessage = (e) => parentReceived.push(e.data);



                const frame = document.createElement('iframe');

                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');

                frame.srcdoc = `

                    <script>

                        window.childReceived = [];

                        window.onmessage = (e) => window.childReceived.push(e.data);

                        // Wrong targetOrigin should not deliver to parent

                        parent.postMessage('child-wrong', 'https://wrong.example');

                        // Correct targetOrigin delivers to parent

                        parent.postMessage('child-correct', 'https://parent.example');

                        // Asterisk targetOrigin delivers to parent

                        parent.postMessage('child-star', '*');

                    </script>

                `;

                document.body.appendChild(frame);



                // Parent sends to child: wrong targetOrigin should not deliver

                frame.contentWindow.postMessage('parent-wrong', 'https://wrong.example');

                // Correct targetOrigin delivers to child

                frame.contentWindow.postMessage('parent-correct', 'https://parent.example');

                // Asterisk targetOrigin delivers to child

                frame.contentWindow.postMessage('parent-star', '*');



                const childList = frame.contentDocument.defaultView ? frame.contentDocument.defaultView.childReceived : [];

                // Parent should have received only 'child-correct' and 'child-star'

                const parentOk = parentReceived.length === 2 && parentReceived[0] === 'child-correct' && parentReceived[1] === 'child-star';

                parentOk;

            """).AsBoolean();

            Assert.True(passed);

        }

        finally { DomBindings.Dispose(engine); }

    }



    [Fact]

    public void IframeOpaqueOriginDeliversNullOriginToParent()

    {

        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true });

        try

        {

            var passed = engine.Evaluate("""

                let receivedOrigin = null;

                let receivedData = null;

                window.onmessage = (e) => {

                    receivedOrigin = e.origin;

                    receivedData = e.data;

                };



                const frame = document.createElement('iframe');

                // Sandboxed without allow-same-origin produces an opaque frame with origin 'null'

                frame.setAttribute('sandbox', 'allow-scripts');

                frame.srcdoc = `

                    <script>

                        parent.postMessage('opaque-ping', '*');

                    </script>

                `;

                document.body.appendChild(frame);



                const res = 'data=' + receivedData + ', origin=' + receivedOrigin;

                res === 'data=opaque-ping, origin=null' ? true : (function() { throw new Error(res); })();

            """).AsBoolean();

            Assert.True(passed);

        }

        finally { DomBindings.Dispose(engine); }

    }



    [Fact]
    public void IframeMessagePortTransferredToFrameAndOnToWorkerDeliversInOrder()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, Workers = true });
        try
        {
            engine.Evaluate("""
                window.workerResults = [];
                window.onmessage = (e) => {
                    if (e.data && e.data.workerDone) {
                        window.workerResults = e.data.items;
                    }
                };

                const channel = new MessageChannel();

                const frame = document.createElement('iframe');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.allow = 'workers';
                frame.srcdoc = `
                    <script>
                        window.onmessage = (e) => {
                            if (e.data === 'init-port') {
                                const port = e.ports[0];
                                const workerScript = "self.onmessage = (e) => { if (e.data === 'handoff') { const p = e.ports[0]; const items = []; p.onmessage = (msg) => { items.push(msg.data); if (items.length === 3) self.postMessage({ workerDone: true, items: items }); }; } };";
                                const worker = new Worker('data:text/javascript,' + encodeURIComponent(workerScript));
                                worker.onmessage = (we) => {
                                    parent.postMessage(we.data, '*');
                                };
                                worker.postMessage('handoff', [port]);
                            }
                        };
                    </script>
                `;
                document.body.appendChild(frame);

                // Transfer channel.port2 into frame
                frame.contentWindow.postMessage('init-port', '*', [channel.port2]);

                // Send 3 ordered messages on port1
                channel.port1.postMessage('msg-alpha');
                channel.port1.postMessage('msg-beta');
                channel.port1.postMessage('msg-gamma');
            """);

            var passed = SpinWait.SpinUntil(() =>
            {
                DomBindings.PumpIframes(engine);
                return engine.Evaluate("window.workerResults && window.workerResults.length === 3").AsBoolean();
            }, TimeSpan.FromSeconds(5));
            Assert.True(passed);
            Assert.True(engine.Evaluate("window.workerResults[0] === 'msg-alpha' && window.workerResults[1] === 'msg-beta' && window.workerResults[2] === 'msg-gamma'").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    private static JintScriptEngine CreateEngine(FeatureCapabilities capabilities)

    {

        var engine = new JintScriptEngine();

        var document = Document.Parse("<html><body></body></html>", new FolioOptions

        {

            BaseUri = new Uri("https://parent.example/"),

            Capabilities = capabilities,

            Scripting = ScriptingPolicy.ContentProcess,

        });

        DomBindings.Install(engine, document);

        return engine;

    }

}

