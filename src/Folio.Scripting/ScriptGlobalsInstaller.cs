namespace Folio.Scripting;

/// <summary>
/// Installs HTML timer, microtask, animation frame, and performance globals onto an <see cref="IScriptEngine"/>
/// backed by an <see cref="HtmlEventLoop"/> (study 17, HTML §8.1.7, #450).
/// </summary>
public static class ScriptGlobalsInstaller
{
    /// <summary>
    /// Installs standard globals onto the script engine:
    /// - setTimeout(handler, timeout, ...args)
    /// - clearTimeout(id)
    /// - setInterval(handler, timeout, ...args)
    /// - clearInterval(id)
    /// - queueMicrotask(callback)
    /// - requestAnimationFrame(callback)
    /// - cancelAnimationFrame(id)
    /// - performance: { now(), timeOrigin }
    /// </summary>
    public static void Install(HtmlEventLoop eventLoop, IScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(eventLoop);
        ArgumentNullException.ThrowIfNull(engine);

        // 1. setTimeout(handler, timeout, ...args)
        var setTimeoutTemplate = new HostObjectTemplate("SetTimeout")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length == 0)
                    return ScriptValue.FromNumber(0);

                var handler = args[0];
                var delayMs = args.Length > 1 ? WebIdlConversions.ToUnrestrictedDouble(args[1]) : 0.0;
                if (double.IsNaN(delayMs) || delayMs < 0)
                    delayMs = 0;

                var extraArgs = args.Length > 2 ? args.Slice(2).ToArray() : Array.Empty<ScriptValue>();

                var action = CreateTimerAction(eventLoop, engine, handler, extraArgs);
                var id = eventLoop.AddTimer(action, delayMs, isInterval: false);
                return ScriptValue.FromNumber(id);
            });

        // 2. clearTimeout(id)
        var clearTimeoutTemplate = new HostObjectTemplate("ClearTimeout")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length > 0)
                {
                    var id = WebIdlConversions.ToInt32(args[0]);
                    eventLoop.ClearTimer(id);
                }
                return ScriptValue.Undefined;
            });

        // 3. setInterval(handler, timeout, ...args)
        var setIntervalTemplate = new HostObjectTemplate("SetInterval")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length == 0)
                    return ScriptValue.FromNumber(0);

                var handler = args[0];
                var delayMs = args.Length > 1 ? WebIdlConversions.ToUnrestrictedDouble(args[1]) : 0.0;
                if (double.IsNaN(delayMs) || delayMs < 0)
                    delayMs = 0;

                var extraArgs = args.Length > 2 ? args.Slice(2).ToArray() : Array.Empty<ScriptValue>();

                var action = CreateTimerAction(eventLoop, engine, handler, extraArgs);
                var id = eventLoop.AddTimer(action, delayMs, isInterval: true);
                return ScriptValue.FromNumber(id);
            });

        // 4. clearInterval(id)
        var clearIntervalTemplate = new HostObjectTemplate("ClearInterval")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length > 0)
                {
                    var id = WebIdlConversions.ToInt32(args[0]);
                    eventLoop.ClearTimer(id);
                }
                return ScriptValue.Undefined;
            });

        // 5. queueMicrotask(callback)
        var queueMicrotaskTemplate = new HostObjectTemplate("QueueMicrotask")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    throw WebIdlException.TypeError("queueMicrotask requires a function callback.");

                var callback = args[0];
                eventLoop.QueueMicrotask(() =>
                {
                    try
                    {
                        engine.Call(callback, ScriptValue.Undefined, ReadOnlySpan<ScriptValue>.Empty);
                    }
                    catch (Exception ex)
                    {
                        OnEngineCallbackException(eventLoop, ex);
                    }
                });
                return ScriptValue.Undefined;
            });

        // 6. requestAnimationFrame(callback)
        var rafTemplate = new HostObjectTemplate("RequestAnimationFrame")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    throw WebIdlException.TypeError("requestAnimationFrame requires a function callback.");

                var callback = args[0];
                var id = eventLoop.RequestAnimationFrame(timestamp =>
                {
                    try
                    {
                        var cbArgs = new[] { ScriptValue.FromNumber(timestamp) };
                        engine.Call(callback, ScriptValue.Undefined, cbArgs);
                    }
                    catch (Exception ex)
                    {
                        OnEngineCallbackException(eventLoop, ex);
                    }
                });
                return ScriptValue.FromNumber(id);
            });

        // 7. cancelAnimationFrame(id)
        var cancelRafTemplate = new HostObjectTemplate("CancelAnimationFrame")
            .DefineMethod("invoke", (_, _, args) =>
            {
                if (args.Length > 0)
                {
                    var id = WebIdlConversions.ToInt32(args[0]);
                    eventLoop.CancelAnimationFrame(id);
                }
                return ScriptValue.Undefined;
            });

        // 8. Performance object
        var performanceTemplate = new HostObjectTemplate("Performance")
            .DefineProperty("timeOrigin", (_, _) => ScriptValue.FromNumber(eventLoop.Clock.TimeOrigin.ToUnixTimeMilliseconds()))
            .DefineMethod("now", (_, _, _) => ScriptValue.FromNumber(eventLoop.Clock.NowMs));

        var target = new object();
        engine.SetGlobalProperty("__folio_setTimeout_obj", engine.CreateHostObject(setTimeoutTemplate, target));
        engine.SetGlobalProperty("__folio_clearTimeout_obj", engine.CreateHostObject(clearTimeoutTemplate, target));
        engine.SetGlobalProperty("__folio_setInterval_obj", engine.CreateHostObject(setIntervalTemplate, target));
        engine.SetGlobalProperty("__folio_clearInterval_obj", engine.CreateHostObject(clearIntervalTemplate, target));
        engine.SetGlobalProperty("__folio_queueMicrotask_obj", engine.CreateHostObject(queueMicrotaskTemplate, target));
        engine.SetGlobalProperty("__folio_raf_obj", engine.CreateHostObject(rafTemplate, target));
        engine.SetGlobalProperty("__folio_cancelRaf_obj", engine.CreateHostObject(cancelRafTemplate, target));
        engine.SetGlobalProperty("performance", engine.CreateHostObject(performanceTemplate, target));

        // Wire functions and clean up helper names
        var timerSource = engine is Folio.Scripting.Native.NativeJavaScriptEngine ? @"
            (function() {
                var _st = __folio_setTimeout_obj, _ct = __folio_clearTimeout_obj;
                var _si = __folio_setInterval_obj, _ci = __folio_clearInterval_obj;
                var _qm = __folio_queueMicrotask_obj, _raf = __folio_raf_obj, _craf = __folio_cancelRaf_obj;
                globalThis.setTimeout = function(handler, timeout) { return _st.invoke(handler, timeout); };
                globalThis.clearTimeout = function(id) { return _ct.invoke(id); };
                globalThis.setInterval = function(handler, timeout) { return _si.invoke(handler, timeout); };
                globalThis.clearInterval = function(id) { return _ci.invoke(id); };
                globalThis.queueMicrotask = function(callback) { return _qm.invoke(callback); };
                globalThis.requestAnimationFrame = function(callback) { return _raf.invoke(callback); };
                globalThis.cancelAnimationFrame = function(id) { return _craf.invoke(id); };
            })();
        " : @"
            (function() {
                var _st = __folio_setTimeout_obj;
                var _ct = __folio_clearTimeout_obj;
                var _si = __folio_setInterval_obj;
                var _ci = __folio_clearInterval_obj;
                var _qm = __folio_queueMicrotask_obj;
                var _raf = __folio_raf_obj;
                var _craf = __folio_cancelRaf_obj;

                globalThis.setTimeout = function(handler, timeout, ...args) {
                    return _st.invoke(handler, timeout, ...args);
                };
                globalThis.clearTimeout = function(id) {
                    return _ct.invoke(id);
                };
                globalThis.setInterval = function(handler, timeout, ...args) {
                    return _si.invoke(handler, timeout, ...args);
                };
                globalThis.clearInterval = function(id) {
                    return _ci.invoke(id);
                };
                globalThis.queueMicrotask = function(callback) {
                    return _qm.invoke(callback);
                };
                globalThis.requestAnimationFrame = function(callback) {
                    return _raf.invoke(callback);
                };
                globalThis.cancelAnimationFrame = function(id) {
                    return _craf.invoke(id);
                };

                delete globalThis.__folio_setTimeout_obj;
                delete globalThis.__folio_clearTimeout_obj;
                delete globalThis.__folio_setInterval_obj;
                delete globalThis.__folio_clearInterval_obj;
                delete globalThis.__folio_queueMicrotask_obj;
                delete globalThis.__folio_raf_obj;
                delete globalThis.__folio_cancelRaf_obj;
            })();
        ";
        engine.EvaluateHostScript(timerSource, "folio:event-loop");
    }

    private static Action CreateTimerAction(
        HtmlEventLoop eventLoop,
        IScriptEngine engine,
        ScriptValue handler,
        ScriptValue[] extraArgs)
    {
        return () =>
        {
            try
            {
                if (handler.IsFunction)
                {
                    engine.Call(handler, ScriptValue.Undefined, extraArgs);
                }
                else if (handler.IsString)
                {
                    var source = handler.AsString();
                    if (!string.IsNullOrEmpty(source))
                    {
                        engine.Evaluate(source, "timer");
                    }
                }
            }
            catch (Exception ex)
            {
                OnEngineCallbackException(eventLoop, ex);
            }
        };
    }

    private static void OnEngineCallbackException(HtmlEventLoop eventLoop, Exception ex)
    {
        // Issue #450: An exception in a callback is reported as a diagnostic and the loop carries on
        eventLoop.ReportException(ex);
    }
}
