using Folio;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class MessagePortTests
{
    [Fact]
    public void MessagesAreCopiedAndDeliveredAfterTheSendingTask()
    {
        using var page = new Page();
        page.Engine.Evaluate("""
            const channel = new MessageChannel();
            const order = ['before'];
            const sent = { value: 1 };
            let received;
            channel.port2.onmessage = event => {
                __thread_probe.record();
                received = event.data;
                order.push('message:' + received.value);
                received.value = 3;
            };
            channel.port1.postMessage(sent);
            sent.value = 2;
            order.push('after');
            """);
        Assert.Equal("before,after", page.Js("order.join(',')").AsString());
        page.Loop.SpinUntilIdle();
        Assert.Equal("before,after,message:1", page.Js("order.join(',')").AsString());
        Assert.True(page.Js("received !== sent && sent.value === 2 && received.value === 3").AsBoolean());
        Assert.False(page.OffOwnerCallback);
    }

    [Fact]
    public void ListenerQueuesWaitForStartAndDispatchAllRegisteredHandlersInOrder()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            const seen = [];
            function first(event) { seen.push('a' + event.data); }
            function second(event) { seen.push('b' + event.data); }
            channel.port2.addEventListener('message', first);
            channel.port2.addEventListener('message', second);
            channel.port1.postMessage(1);
            channel.port1.postMessage(2);
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal("", page.Js("seen.join(',')").AsString());
        page.Js("channel.port2.start()");
        page.Loop.SpinUntilIdle();
        Assert.Equal("a1,b1,a2,b2", page.Js("seen.join(',')").AsString());
        page.Js("channel.port2.removeEventListener('message', first); channel.port1.postMessage(3)");
        page.Loop.SpinUntilIdle();
        Assert.Equal("a1,b1,a2,b2,b3", page.Js("seen.join(',')").AsString());
    }

    [Fact]
    public void ClosingOrDisposingDropsAlreadyQueuedCallbacks()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            let deliveries = 0;
            channel.port2.onmessage = () => deliveries++;
            channel.port1.postMessage('queued');
            channel.port2.close();
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal(0, page.Js("deliveries").AsNumber());
        page.Js("""
            const other = new MessageChannel();
            other.port2.onmessage = () => deliveries++;
            other.port1.postMessage('also queued');
            """);
        DomBindings.Dispose(page.Engine);
        page.Loop.SpinUntilIdle();
        Assert.Equal(0, page.Js("deliveries").AsNumber());
    }

    [Fact]
    public void RichGraphsRetainCyclesAliasesViewsAndSparseArrayHoles()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            let received;
            channel.port2.onmessage = event => received = event.data;
            const buffer = new ArrayBuffer(16);
            new Uint8Array(buffer).set([1, 2, 3, 4, 5, 6, 7, 8]);
            const shared = { value: 7 };
            const sparse = new Array(4);
            sparse[2] = undefined;
            const expression = /folio/gi;
            expression.lastIndex = 3;
            const graph = {
                shared, again: shared, buffer,
                view: new Uint8Array(buffer, 2, 4), dataView: new DataView(buffer, 1, 6),
                sparse, date: new Date(1337), expression, error: new TypeError('sample'),
                negativeZero: -0, nan: NaN, infinity: Infinity, integer: 1234567890123456789n
            };
            graph.self = graph;
            graph.map = new Map([[shared, graph]]);
            graph.set = new Set([shared]);
            Object.defineProperty(graph, '__proto__', { value: { safe: 42 }, enumerable: true });
            channel.port1.postMessage(graph);
            """);
        page.Loop.SpinUntilIdle();
        Assert.True(page.Js("""
            received !== graph && received.self === received && received.shared === received.again &&
            received.map.get(received.shared) === received && received.set.has(received.shared) &&
            received.buffer !== buffer && received.view.buffer === received.buffer &&
            received.dataView.buffer === received.buffer && received.view.byteOffset === 2 &&
            received.view.length === 4 && received.view[0] === 3 && received.dataView.byteOffset === 1 &&
            received.dataView.byteLength === 6 && received.date.getTime() === 1337 &&
            received.expression.source === 'folio' && received.expression.flags === 'gi' &&
            received.expression.lastIndex === 0 && received.error.name === 'TypeError' &&
            received.error.message === 'sample' && received.error instanceof TypeError && received.sparse.length === 4 &&
            !(0 in received.sparse) && (2 in received.sparse) && received.sparse[2] === undefined &&
            Object.is(received.negativeZero, -0) && Number.isNaN(received.nan) &&
            received.infinity === Infinity && received.integer === 1234567890123456789n &&
            Object.prototype.hasOwnProperty.call(received, '__proto__') &&
            received.__proto__.safe === 42 && Object.getPrototypeOf(received) === Object.prototype
            """).AsBoolean());
    }

    [Fact]
    public void UnsupportedValuesAndDuplicateTransfersFailBeforeDetachment()
    {
        using var page = new Page();
        Assert.True(page.Js("""
            const channel = new MessageChannel();
            const other = new MessageChannel();
            const buffer = new ArrayBuffer(8);
            function cloneError(action) {
                try { action(); return false; } catch (error) { return error.name === 'DataCloneError'; }
            }
            [() => channel.port1.postMessage({ nested: () => 1 }),
             () => channel.port1.postMessage(Symbol('private')),
             () => channel.port1.postMessage(new WeakMap()),
             () => channel.port1.postMessage(Promise.resolve(1)),
             () => channel.port1.postMessage(document),
             () => channel.port1.postMessage({ port: other.port1 }),
             () => channel.port1.postMessage(buffer, [buffer, buffer]),
             () => channel.port1.postMessage(1, [channel.port1])]
                .every(cloneError) && buffer.byteLength === 8;
            """).AsBoolean());
        Assert.True(page.Js("""
            const bad = { get value() { throw new TypeError('getter failure'); } };
            let originalError = false;
            try { channel.port1.postMessage(bad, [buffer]); }
            catch (error) { originalError = error.name === 'TypeError' && error.message === 'getter failure'; }
            originalError && buffer.byteLength === 8;
            """).AsBoolean());
    }

    [Fact]
    public void BuffersDetachImmediatelyAndTransferredPortsAppearEvenWhenUnreferenced()
    {
        using var page = new Page();
        Assert.True(page.Js("""
            const channel = new MessageChannel();
            const other = new MessageChannel();
            const buffer = new ArrayBuffer(8);
            new Uint8Array(buffer)[0] = 42;
            let received, ports;
            channel.port2.onmessage = event => { received = event.data; ports = event.ports; };
            channel.port1.postMessage({ buffer }, [buffer, other.port1]);
            buffer.byteLength === 0;
            """).AsBoolean());
        Assert.True(page.Js("""
            let rejected = false;
            try { channel.port1.postMessage(buffer, [buffer]); }
            catch (error) { rejected = error.name === 'DataCloneError'; }
            rejected;
            """).AsBoolean());
        page.Loop.SpinUntilIdle();
        Assert.True(page.Js("received.buffer.byteLength === 8 && new Uint8Array(received.buffer)[0] === 42 && ports.length === 1 && ports[0] instanceof MessagePort").AsBoolean());
        Assert.True(page.Js("""
            let rejectedAgain = false;
            try { channel.port1.postMessage(other.port1, [other.port1]); }
            catch (error) { rejectedAgain = error.name === 'DataCloneError'; }
            rejectedAgain;
            """).AsBoolean());
    }

    [Fact]
    public void TransferredPortRetainsPendingMessagesAndNeedsItsNewHandler()
    {
        using var page = new Page();
        page.Js("""
            const carrier = new MessageChannel();
            const original = new MessageChannel();
            const seen = [];
            let received;
            original.port2.onmessage = event => seen.push('old:' + event.data);
            original.port1.postMessage('before transfer');
            carrier.port2.onmessage = event => received = event.data;
            carrier.port1.postMessage({ meta: 7, nested: { port: original.port2 } }, [original.port2]);
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal("", page.Js("seen.join(',')").AsString());
        Assert.True(page.Js("received.meta === 7 && received.nested.port instanceof MessagePort").AsBoolean());
        page.Js("received.nested.port.onmessage = event => seen.push('new:' + event.data)");
        page.Loop.SpinUntilIdle();
        Assert.Equal("new:before transfer", page.Js("seen.join(',')").AsString());
        page.Js("original.port1.postMessage('after transfer')");
        page.Loop.SpinUntilIdle();
        Assert.Equal("new:before transfer,new:after transfer", page.Js("seen.join(',')").AsString());
    }

    [Fact]
    public void HostCloneFunctionsSurvivePageReplacementOfHelpersAndJsonMethods()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            let received;
            channel.port2.onmessage = event => received = event.data;
            globalThis.__folio_encode_clone = () => 'null';
            globalThis.__folio_decode_clone = () => ({ answer: -1 });
            globalThis.__folio_structured_clone_encode = () => 'null';
            globalThis.__folio_structured_clone_decode = () => ({ answer: -1 });
            JSON.stringify = () => 'null';
            JSON.parse = () => ({ answer: -1 });
            channel.port1.postMessage({ answer: 42 });
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal(42, page.Js("received.answer").AsNumber());
    }

    [Fact]
    public void Utf8AndBinaryDataShareTheConfiguredMessageLimit()
    {
        using var page = new Page(maxMessageBytes: 512);
        Assert.True(page.Js("""
            const channel = new MessageChannel();
            const buffer = new ArrayBuffer(400);
            let rejected = false;
            try { channel.port1.postMessage({ buffer, text: '😀'.repeat(80) }, [buffer]); }
            catch (error) { rejected = error.name === 'DataCloneError'; }
            rejected && buffer.byteLength === 400;
            """).AsBoolean());
    }

    [Fact]
    public void WorkerPortRepliesUseTheParentThreadAndRetainNestedMetadata()
    {
        using var page = new Page();
        Assert.True(page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                onmessage = function(event) {
                    const data = event.data;
                    data.nested.port.onmessage = message => data.nested.port.postMessage({ value: message.data.value + 1 });
                    data.nested.port.postMessage({ ready: data.meta });
                };
            `));
            const channel = new MessageChannel();
            const seen = [];
            channel.port1.onmessage = event => { __thread_probe.record(); seen.push(event.data); };
            worker.postMessage({ meta: 'preserved', nested: { port: channel.port2 } }, [channel.port2]);
            seen.length === 0;
            """).AsBoolean());
        page.PumpUntil("seen.length === 1");
        Assert.Equal("preserved", page.Js("seen[0].ready").AsString());
        page.Js("const sent = { value: 41 }; channel.port1.postMessage(sent); sent.value = 99;");
        page.PumpUntil("seen.length === 2");
        Assert.Equal(42, page.Js("seen[1].value").AsNumber());
        Assert.False(page.OffOwnerCallback);
    }

    [Fact]
    public void EachMessageHasItsOwnMicrotaskCheckpoint()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            const seen = [];
            channel.port2.onmessage = event => {
                seen.push(event.data);
                queueMicrotask(() => seen.push('micro' + event.data));
            };
            channel.port1.postMessage(1);
            channel.port1.postMessage(2);
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal("1,micro1,2,micro2", page.Js("seen.join(',')").AsString());
    }

    [Fact]
    public void MessageEventsUseTheReceivingPortAndFrozenTransferredPorts()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            let correct = false;
            channel.port2.onmessage = function(event) {
                correct = this === channel.port2 && event.target === channel.port2 && event.source === null &&
                    event.origin === '' && Array.isArray(event.ports) && event.ports.length === 0 && Object.isFrozen(event.ports);
            };
            channel.port1.postMessage(1);
            """);
        page.Loop.SpinUntilIdle();
        Assert.True(page.Js("correct").AsBoolean());
    }

    [Fact]
    public void ClosedPortsStillRejectUncloneableMessagesAndSelfTransfers()
    {
        using var page = new Page();
        Assert.True(page.Js("""
            const channel = new MessageChannel();
            channel.port1.close();
            let uncloneable = false, self = false;
            try { channel.port1.postMessage(() => 1); } catch (error) { uncloneable = error.name === 'DataCloneError'; }
            try { channel.port1.postMessage(1, [channel.port1]); } catch (error) { self = error.name === 'DataCloneError'; }
            uncloneable && self;
            """).AsBoolean());
    }

    [Fact]
    public void ATransferredSourceWrapperCannotBeRestartedOrUsedForDelivery()
    {
        using var page = new Page();
        page.Js("""
            const carrier = new MessageChannel(), original = new MessageChannel();
            let moved;
            const seen = [];
            carrier.port2.onmessage = event => moved = event.data;
            carrier.port1.postMessage(original.port2, [original.port2]);
            """);
        page.Loop.SpinUntilIdle();
        page.Js("""
            original.port2.onmessage = event => seen.push('old');
            original.port2.start();
            moved.onmessage = event => seen.push(event.data);
            original.port1.onmessage = event => seen.push('wrong');
            original.port2.postMessage('must drop');
            original.port1.postMessage('new');
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal("new", page.Js("seen.join(',')").AsString());
    }

    [Fact]
    public void PendingMemoryIsSharedAcrossChannelsAndRefusalDoesNotDetach()
    {
        using var page = new Page(maxPendingBytes: 1024);
        Assert.True(page.Js("""
            const first = new MessageChannel(), second = new MessageChannel();
            let accepted = 0, refused = false;
            for (let i = 0; i < 40; i++) {
                try { (i % 2 ? first.port1 : second.port1).postMessage('x'.repeat(64)); accepted++; }
                catch (error) { refused = error.name === 'QuotaExceededError'; break; }
            }
            const buffer = new ArrayBuffer(128);
            let transferRefused = false;
            try { first.port1.postMessage({ buffer }, [buffer]); }
            catch (error) { transferRefused = error.name === 'QuotaExceededError'; }
            accepted > 0 && accepted * 64 <= 1024 && refused && transferRefused && buffer.byteLength === 128;
            """).AsBoolean());
        page.Js("first.port2.close(); second.port2.close()");
        Assert.True(page.Js("""
            const next = new MessageChannel();
            let delivered = '';
            next.port2.onmessage = event => delivered = event.data;
            next.port1.postMessage('budget released');
            true;
            """).AsBoolean());
        page.Loop.SpinUntilIdle();
        Assert.Equal("budget released", page.Js("delivered").AsString());
    }

    [Fact]
    public void WorkerCreatedPortsSurviveTheirSourceEngineAfterTransfer()
    {
        using var page = new Page();
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                onmessage = function() {
                    const channel = new MessageChannel();
                    postMessage({ first: channel.port1, second: channel.port2 }, [channel.port1, channel.port2]);
                };
            `));
            let moved;
            let workerError = '';
            worker.onerror = event => workerError = event.message;
            worker.onmessage = event => moved = event.data;
            worker.postMessage(0);
            """);
        page.PumpUntil("moved !== undefined");
        page.Js("worker.terminate(); let delivered = 0; moved.second.onmessage = event => delivered = event.data; moved.first.postMessage(42)");
        page.Loop.SpinUntilIdle();
        Assert.Equal(42, page.Js("delivered").AsNumber());
        Assert.True(page.Js("!moved.first.closed && !moved.second.closed").AsBoolean());
    }

    [Fact]
    public void WorkerCanCatchACloneFailureOnItsOwnPostMessage()
    {
        using var page = new Page();
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                onmessage = function() {
                    try { postMessage({ bad: () => 1 }); }
                    catch (error) { postMessage({ caught: error.name }); }
                };
            `));
            let reply;
            worker.onmessage = event => reply = event.data;
            worker.postMessage(0);
            """);
        page.PumpUntil("reply !== undefined");
        Assert.Equal("DataCloneError", page.Js("reply.caught").AsString());
    }

    [Fact]
    public void CloneUsesNativeBrandsAndCapturedOperationsAfterPrototypeMutation()
    {
        using var page = new Page();
        page.Js("""
            const channel = new MessageChannel();
            let received;
            channel.port2.onmessage = event => received = event.data;
            const originalMapGet = Map.prototype.get;
            const originalDateGet = Date.prototype.getTime;
            const restore = {
                Map, Set, ArrayBuffer, Uint8Array, Date, RegExp,
                map: Array.prototype.map, each: Array.prototype.forEach,
                mapGet: Map.prototype.get, mapSet: Map.prototype.set, mapEach: Map.prototype.forEach
            };
            const buffer = new ArrayBuffer(16);
            new Uint16Array(buffer).set([1000, 2000, 3000, 4000]);
            const graph = { map: new Map([['key', 42]]), date: new Date(17),
                expression: /folio/gi, view: new Uint16Array(buffer, 2, 3), buffer };
            Object.defineProperty(graph.expression, 'source', { value: 'changed' });
            Object.defineProperty(graph.view, 'constructor', { value: Float64Array });
            Object.prototype.toJSON = () => 'poison';
            Object.prototype.r = 999;
            Object.prototype.get = () => { throw new Error('poison descriptor'); };
            Array.prototype.map = () => { throw new Error('poison map'); };
            Array.prototype.forEach = () => { throw new Error('poison each'); };
            Map.prototype.get = Map.prototype.set = Map.prototype.forEach = () => { throw new Error('poison Map'); };
            globalThis.Map = globalThis.Set = globalThis.ArrayBuffer = globalThis.Uint8Array =
                globalThis.Date = globalThis.RegExp = function() { throw new Error('poison constructor'); };
            channel.port1.postMessage(graph);
            """);
        try
        {
            page.Loop.SpinUntilIdle();
            Assert.True(page.Js("""
                originalMapGet.call(received.map, 'key') === 42 && originalDateGet.call(received.date) === 17 &&
                received.expression.source === 'folio' && received.expression.flags === 'gi' &&
                received.view instanceof Uint16Array && received.view.buffer === received.buffer &&
                received.view.byteOffset === 2 && received.view.length === 3 && received.view[0] === 2000
                """).AsBoolean());
        }
        finally
        {
            page.Js("""
                globalThis.Map = restore.Map; globalThis.Set = restore.Set; globalThis.ArrayBuffer = restore.ArrayBuffer;
                globalThis.Uint8Array = restore.Uint8Array; globalThis.Date = restore.Date; globalThis.RegExp = restore.RegExp;
                Array.prototype.map = restore.map; Array.prototype.forEach = restore.each;
                Map.prototype.get = restore.mapGet; Map.prototype.set = restore.mapSet; Map.prototype.forEach = restore.mapEach;
                delete Object.prototype.toJSON; delete Object.prototype.r; delete Object.prototype.get;
                """);
        }
    }

    [Fact]
    public void GlobalStructuredCloneUsesTheSharedGraphCodecAndTransfersAtomically()
    {
        using var page = new Page();
        Assert.True(page.Js("""
            const original = { map: new Map(), buffer: new ArrayBuffer(4) };
            original.self = original;
            new Uint8Array(original.buffer)[2] = 91;
            original.map.set(original, original.buffer);
            const copy = structuredClone(original, { transfer: [original.buffer] });
            copy !== original && copy.self === copy && copy.map.get(copy) === copy.buffer &&
                new Uint8Array(copy.buffer)[2] === 91 && original.buffer.detached &&
                structuredClone(undefined) === undefined;
            """).AsBoolean());
        Assert.True(page.Js("""
            const intact = new ArrayBuffer(3), marker = new TypeError('getter');
            let caught = false;
            try { structuredClone({ get bad() { throw marker; } }, { transfer: [intact] }); }
            catch (error) { caught = error === marker; }
            caught && !intact.detached;
            """).AsBoolean());
        page.Js("""
            const channel = new MessageChannel();
            const moved = structuredClone(channel.port2, { transfer: [channel.port2] });
            let received = 0;
            moved.onmessage = event => received = event.data;
            channel.port1.postMessage(23);
            """);
        page.Loop.SpinUntilIdle();
        Assert.Equal(23, page.Js("received").AsNumber());
    }

    [Fact]
    public void WorkerHandlerFailuresDoNotStopOtherListenersOrLaterMessages()
    {
        using var page = new Page();
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                let count = 0;
                onmessage = function(event) { throw new Error('handler-' + event.data); };
                addEventListener('message', function(event) {
                    postMessage({ count: ++count, data: event.data,
                        metadata: event.target === self && event.source === null &&
                            event.origin === '' && Object.isFrozen(event.ports) && this === self });
                });
            `));
            const replies = [], errors = [];
            let decodeErrors = 0;
            worker.onerror = event => errors.push(event.message);
            worker.onmessageerror = () => decodeErrors++;
            worker.onmessage = event => replies.push(event.data);
            worker.postMessage(1);
            """);
        page.PumpUntil("replies.length === 1 && errors.length === 1");
        page.Js("worker.postMessage(2)");
        page.PumpUntil("replies.length === 2 && errors.length === 2");
        Assert.True(page.Js("replies[0].count === 1 && replies[1].count === 2 && replies.every(r => r.metadata) && errors.join(',') === 'handler-1,handler-2' && decodeErrors === 0").AsBoolean());
    }

    [Fact]
    public void ParentHandlerFailureIsReportedWithoutCreatingAMessageError()
    {
        using var page = new Page();
        var reported = new List<Exception>();
        page.Loop.UncaughtException += reported.Add;
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = e => postMessage(e.data);'));
            let delivered = 0, decodeErrors = 0;
            worker.onmessage = () => { throw new Error('parent handler'); };
            worker.onmessageerror = () => decodeErrors++;
            worker.addEventListener('message', () => delivered++);
            worker.postMessage(1);
            """);
        page.PumpUntil("delivered === 1");
        Assert.Single(reported);
        Assert.Contains("parent handler", reported[0].Message);
        Assert.Equal(0, page.Js("decodeErrors").AsNumber());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiverAllocationFailureDispatchesMessageErrorAndKeepsDeliveryRunning(bool worker)
    {
        using var engine = new RejectingBufferEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        var uncaught = new List<Exception>();
        loop.UncaughtException += uncaught.Add;
        engine.Evaluate(worker ? """
            const receiver = new Worker('data:text/javascript,' + encodeURIComponent(`
                onmessage = function() {
                    const channel = new MessageChannel();
                    postMessage(new ArrayBuffer(4), [channel.port1]);
                    postMessage(73);
                };
            `));
            """ : """
            const channel = new MessageChannel();
            const receiver = channel.port2;
            """);
        engine.Evaluate("""
            let messageErrors = 0, listenerErrors = 0, received = 0;
            receiver.onmessageerror = function(event) {
                if (event.type === 'messageerror' && event.target === receiver &&
                    event.data === undefined && event.source === null && event.origin === '' &&
                    event.ports.length === 0 && Object.isFrozen(event.ports) &&
                    this === receiver) messageErrors++;
            };
            receiver.addEventListener('messageerror', () => listenerErrors++);
            receiver.onmessage = event => received = event.data;
            """);
        engine.Evaluate(worker ? "receiver.postMessage(1)" : """
            const transfer = new MessageChannel();
            channel.port1.postMessage(new ArrayBuffer(4), [transfer.port1]);
            channel.port1.postMessage(73);
            """);
        engine.RejectBuffers = true;
        loop.SpinUntilIdle();
        Assert.True(engine.Evaluate("messageErrors === 1 && listenerErrors === 1 && received === 73").AsBoolean());
        Assert.Empty(uncaught);
    }

    [Fact]
    public void OptionsAndIterableTransferListsWorkInBothWorkerDirections()
    {
        using var page = new Page();
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                onmessage = function(event) {
                    const bytes = event.data;
                    const before = bytes.byteLength;
                    postMessage(bytes, { transfer: new Set([bytes]) });
                    postMessage(before === 4 && bytes.detached);
                };
            `));
            const replies = [];
            worker.onmessage = event => replies.push(event.data);
            const buffer = new ArrayBuffer(4);
            new Uint8Array(buffer)[1] = 83;
            worker.postMessage(buffer, { transfer: new Set([buffer]) });
            """);
        page.PumpUntil("replies.length === 2");
        Assert.True(page.Js("buffer.detached && new Uint8Array(replies[0])[1] === 83 && replies[1]").AsBoolean());
        Assert.True(page.Js("""
            const buffer2 = new ArrayBuffer(2);
            let reads = 0;
            const transfers = { get [Symbol.iterator]() { reads++; return function*() { yield buffer2; }; } };
            const cloned = structuredClone(buffer2, { transfer: transfers });
            reads === 1 && buffer2.detached && cloned.byteLength === 2;
            """).AsBoolean());
        page.Js("""
            const ports = new MessageChannel();
            let portReply;
            ports.port2.onmessage = event => portReply = event.data;
            const buffer3 = new ArrayBuffer(3);
            ports.port1.postMessage(buffer3, { transfer: new Set([buffer3]) });
            """);
        page.Loop.SpinUntilIdle();
        Assert.True(page.Js("buffer3.detached && portReply.byteLength === 3").AsBoolean());
    }

    [Fact]
    public void ClosingDeepTransferChainsReleasesEveryMailboxWithoutHostRecursion()
    {
        using var page = new Page(maxPendingBytes: 1024 * 1024);
        page.Js("""
            const root = new MessageChannel();
            let sender = root.port1;
            """);
        for (var batch = 0; batch < 20; batch++) page.Js("""
            for (let i = 0; i < 100; i++) {
                const next = new MessageChannel();
                sender.postMessage(undefined, [next.port1]);
                sender = next.port2;
            }
            """);
        Assert.True(page.Js("""
            root.port2.close();
            const after = new MessageChannel();
            after.port1.postMessage('x'.repeat(400000));
            after.port2.close();
            true;
            """).AsBoolean());
    }

    [Fact]
    public void WorkerListenersRemovedDuringDispatchAreSkippedInBothDirections()
    {
        using var page = new Page();
        page.Js("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(`
                function removed() { postMessage('removed-worker-listener'); }
                onmessage = function() {
                    removeEventListener('message', removed);
                    postMessage('kept');
                };
                addEventListener('message', removed);
            `));
            const replies = [];
            function removed() { replies.push('removed-parent-listener'); }
            worker.onmessage = event => {
                worker.removeEventListener('message', removed);
                replies.push(event.data);
            };
            worker.addEventListener('message', removed);
            worker.postMessage(1);
            """);
        page.PumpUntil("replies.length > 0");
        Assert.Equal("kept", page.Js("replies.join(',')").AsString());
    }

    [Fact]
    public void LegacyParentHandlerFailureDoesNotEscapePostMessageOrSkipListeners()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        });
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = e => postMessage(e.data);'));
            let delivered = 0, decodeErrors = 0;
            worker.onmessage = () => { throw new Error('listener'); };
            worker.onmessageerror = () => decodeErrors++;
            worker.addEventListener('message', () => delivered++);
            worker.postMessage(1);
            delivered === 1 && decodeErrors === 0;
            """).AsBoolean());
    }

    [Fact]
    public void EventAllocationFailureClosesAlreadyClaimedPortsAndReleasesTheirMailboxes()
    {
        using var engine = new RejectingBufferEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Limits = ResourceLimits.Default with { MaxWorkerMemoryBytes = 60000 }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        engine.Evaluate("""
            const channel = new MessageChannel(), pending = new MessageChannel();
            let failed = 0, received = 0;
            channel.port2.onmessageerror = () => failed++;
            channel.port2.onmessage = event => received = event.data;
            pending.port2.postMessage('x'.repeat(10000));
            channel.port1.postMessage(pending.port1, [pending.port1]);
            channel.port1.postMessage(73);
            """);
        engine.RejectNextMessageEvent = true;
        loop.SpinUntilIdle();
        Assert.True(engine.Evaluate("failed === 1 && received === 73").AsBoolean());
        // This fits only if the undelivered transferred port's 20 KB mailbox was freed.
        engine.Evaluate("const after = new MessageChannel(); after.port1.postMessage('x'.repeat(25000)); after.port2.close();");
    }

    [Fact]
    public void ReinstallAfterDocumentTeardownUsesFreshHelpersAndTheNewBudget()
    {
        using var engine = new JintScriptEngine();
        using var first = Document.Parse("<html></html>", new FolioOptions
        {
            Limits = ResourceLimits.Default with { MaxWorkerMemoryBytes = 500 }
        });
        DomBindings.Install(engine, first);
        engine.Evaluate("const old = new MessageChannel(); old.port1.postMessage(1);");
        DomBindings.Dispose(engine);
        engine.Configure(ScriptLimits.Default);
        using var second = Document.Parse("<html></html>", new FolioOptions
        {
            Limits = ResourceLimits.Default with { MaxWorkerMemoryBytes = 10000 }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, second, eventLoop: loop);
        engine.Evaluate("""
            const channel = new MessageChannel();
            let size = 0;
            channel.port2.onmessage = event => size = event.data.length;
            channel.port1.postMessage('x'.repeat(2000));
            """);
        loop.SpinUntilIdle();
        Assert.Equal(2000, engine.Evaluate("size").AsNumber());
        Assert.True(engine.Evaluate("""
            try { structuredClone(() => {}); false; }
            catch (error) { error instanceof DOMException && error.name === 'DataCloneError'; }
            """).AsBoolean());
    }

    private sealed class RejectingBufferEngine : IScriptEngine, IScriptEngineFactory, IHostScriptEvaluator, IStructuredCloneEngine
    {
        private readonly JintScriptEngine _inner = new();
        internal bool RejectBuffers;
        internal bool RejectNextMessageEvent;
        public void Configure(ScriptLimits limits) => _inner.Configure(limits);
        public ScriptValue Evaluate(string source, string url = "eval") => _inner.Evaluate(source, url);
        public ScriptValue EvaluateHostScript(string source, string url) => ((IHostScriptEvaluator)_inner).EvaluateHostScript(source, url);
        public ScriptValue Call(ScriptValue function, ScriptValue target, ReadOnlySpan<ScriptValue> args) => _inner.Call(function, target, args);
        public ScriptValue CreateHostObject(IHostObjectTemplate template, object target)
        {
            if (RejectNextMessageEvent && template.Name == "MessageEvent")
            {
                RejectNextMessageEvent = false;
                throw new ScriptLimitExceededException("Memory", "Receiver refused event allocation.");
            }
            return _inner.CreateHostObject(template, target);
        }
        public bool TryGetHostTarget(ScriptValue value, out object? target) => _inner.TryGetHostTarget(value, out target);
        public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> items) => _inner.CreateArray(items);
        public bool TryGetArrayItems(ScriptValue value, out IReadOnlyList<ScriptValue> items) => _inner.TryGetArrayItems(value, out items);
        public ScriptValue GetProperty(ScriptValue value, string name) => _inner.GetProperty(value, name);
        public bool TryGetArrayBufferBytes(ScriptValue value, out byte[] bytes) => _inner.TryGetArrayBufferBytes(value, out bytes);
        public bool TryDetachArrayBuffer(ScriptValue value) => _inner.TryDetachArrayBuffer(value);
        public ScriptValue CreateArrayBuffer(ReadOnlySpan<byte> bytes) => RejectBuffers
            ? throw new ScriptLimitExceededException("Memory", "Receiver refused buffer allocation.") : _inner.CreateArrayBuffer(bytes);
        public void SetGlobalProperty(string name, ScriptValue value) => _inner.SetGlobalProperty(name, value);
        public ScriptValue GetGlobalProperty(string name) => _inner.GetGlobalProperty(name);
        public void PerformMicrotaskCheckpoint() => _inner.PerformMicrotaskCheckpoint();
        public void Interrupt() => _inner.Interrupt();
        public IScriptEngine CreateIsolated() => _inner.CreateIsolated();
        public string ClassifyCloneObject(ScriptValue value) => ((IStructuredCloneEngine)_inner).ClassifyCloneObject(value);
        public (string Source, string Flags) GetCloneRegExp(ScriptValue value) => ((IStructuredCloneEngine)_inner).GetCloneRegExp(value);
        public void Dispose() { DomBindings.Dispose(this); _inner.Dispose(); }
    }

    private sealed class Page : IDisposable
    {
        private readonly Document _document;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private int _offOwner;
        internal readonly JintScriptEngine Engine = new();
        internal readonly HtmlEventLoop Loop;
        internal bool OffOwnerCallback => Volatile.Read(ref _offOwner) != 0;

        internal Page(int maxMessageBytes = 1024 * 1024, long maxPendingBytes = 64L * 1024 * 1024)
        {
            _document = Document.Parse("<html><body></body></html>", new FolioOptions
            {
                BaseUri = new Uri("https://parent.example/"),
                Capabilities = new FeatureCapabilities { Workers = true },
                Limits = ResourceLimits.Default with { MaxWorkerMessageBytes = maxMessageBytes, MaxWorkerMemoryBytes = maxPendingBytes }
            });
            Loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, _document, eventLoop: Loop);
            ScriptGlobalsInstaller.Install(Loop, Engine);
            var probe = new HostObjectTemplate("ThreadProbe").DefineMethod("record", (_, _, _) =>
            {
                if (Environment.CurrentManagedThreadId != _owner) Interlocked.Exchange(ref _offOwner, 1);
                return ScriptValue.Undefined;
            });
            Engine.SetGlobalProperty("__thread_probe", Engine.CreateHostObject(probe, this));
        }

        internal ScriptValue Js(string source) => Engine.Evaluate(source);

        internal void PumpUntil(string condition)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                Loop.SpinUntilIdle();
                if (Js(condition).AsBoolean()) return;
                Thread.Sleep(1);
            }
            Assert.True(Js(condition).AsBoolean(), $"Timed out waiting for {condition}");
        }

        public void Dispose()
        {
            Engine.Dispose();
            Loop.Dispose();
            _document.Dispose();
        }
    }
}
