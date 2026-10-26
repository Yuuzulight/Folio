using System.Diagnostics;
using System.Text.Json;

namespace Folio.Scripting.Native;

internal sealed class CallFrame
{
    public BytecodeChunk Chunk { get; }
    public int Ip { get; set; }
    public int StackBase { get; }
    public NativeValue ThisValue { get; }
    public LexicalEnvironment Environment { get; }
    public int TryCatchTarget { get; set; } = -1;

    public CallFrame(BytecodeChunk chunk, int stackBase, NativeValue thisValue, LexicalEnvironment env)
    {
        Chunk = chunk;
        Ip = 0;
        StackBase = stackBase;
        ThisValue = thisValue;
        Environment = env;
    }
}

public sealed class NativeVirtualMachine
{
    public static readonly NativeObject ObjectPrototype = CreateObjectPrototype();
    public static readonly NativeObject ArrayPrototype = CreateArrayPrototype(ObjectPrototype);
    public static readonly NativeObject StringPrototype = CreateStringPrototype(ObjectPrototype);

    private readonly List<NativeValue> _stack = [];
    private readonly List<CallFrame> _frames = [];
    private readonly LexicalEnvironment _globalEnv = new();
    private ScriptLimits _limits = ScriptLimits.Default;
    private volatile bool _interrupted;
    private long _statementCount;
    private Stopwatch? _stopwatch;
    private readonly Random _random;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<Action> _microtasks = new();

    public LexicalEnvironment GlobalEnvironment => _globalEnv;

    public NativeVirtualMachine(ScriptEngineOptions? options = null)
    {
        var opt = options ?? ScriptEngineOptions.Default;
        _limits = opt.Limits ?? ScriptLimits.Default;
        _random = opt.RandomSeed.HasValue ? new Random(opt.RandomSeed.Value) : new Random();
        _clock = opt.Clock ?? (() => DateTimeOffset.UtcNow);

        InstallStandardGlobals();
    }

    public void Configure(ScriptLimits limits)
    {
        _limits = limits ?? ScriptLimits.Default;
    }

    public void Interrupt()
    {
        _interrupted = true;
    }

    public void ResetInterrupt()
    {
        _interrupted = false;
    }

    public void EnqueueMicrotask(Action callback)
    {
        _microtasks.Enqueue(callback);
    }

    public void PerformMicrotaskCheckpoint()
    {
        while (_microtasks.Count > 0)
        {
            var task = _microtasks.Dequeue();
            task();
        }
    }

    private void InstallStandardGlobals()
    {
        // 1. Math
        var mathObj = new NativeObject();
        mathObj.Set("PI", NativeValue.FromNumber(Math.PI));
        mathObj.Set("E", NativeValue.FromNumber(Math.E));
        mathObj.Set("random", NativeValue.FromFunction(new NativeFunction("random", (vm, _, _) => NativeValue.FromNumber(vm._random.NextDouble()))));
        mathObj.Set("floor", NativeValue.FromFunction(new NativeFunction("floor", (_, _, args) => NativeValue.FromNumber(Math.Floor(args.Length > 0 ? args[0].AsNumber() : 0)))));
        mathObj.Set("ceil", NativeValue.FromFunction(new NativeFunction("ceil", (_, _, args) => NativeValue.FromNumber(Math.Ceiling(args.Length > 0 ? args[0].AsNumber() : 0)))));
        mathObj.Set("round", NativeValue.FromFunction(new NativeFunction("round", (_, _, args) => NativeValue.FromNumber(Math.Round(args.Length > 0 ? args[0].AsNumber() : 0)))));
        mathObj.Set("abs", NativeValue.FromFunction(new NativeFunction("abs", (_, _, args) => NativeValue.FromNumber(Math.Abs(args.Length > 0 ? args[0].AsNumber() : 0)))));
        mathObj.Set("min", NativeValue.FromFunction(new NativeFunction("min", (_, _, args) =>
        {
            if (args.Length == 0) return NativeValue.FromNumber(double.PositiveInfinity);
            double m = args[0].AsNumber();
            for (var i = 1; i < args.Length; i++) m = Math.Min(m, args[i].AsNumber());
            return NativeValue.FromNumber(m);
        })));
        mathObj.Set("max", NativeValue.FromFunction(new NativeFunction("max", (_, _, args) =>
        {
            if (args.Length == 0) return NativeValue.FromNumber(double.NegativeInfinity);
            double m = args[0].AsNumber();
            for (var i = 1; i < args.Length; i++) m = Math.Max(m, args[i].AsNumber());
            return NativeValue.FromNumber(m);
        })));
        mathObj.Set("sqrt", NativeValue.FromFunction(new NativeFunction("sqrt", (_, _, args) => NativeValue.FromNumber(Math.Sqrt(args.Length > 0 ? args[0].AsNumber() : 0)))));
        mathObj.Set("pow", NativeValue.FromFunction(new NativeFunction("pow", (_, _, args) => NativeValue.FromNumber(Math.Pow(args.Length > 0 ? args[0].AsNumber() : 0, args.Length > 1 ? args[1].AsNumber() : 0)))));
        _globalEnv.Declare("Math", NativeValue.FromObject(mathObj));

        // 2. console
        var consoleObj = new NativeObject();
        consoleObj.Set("log", NativeValue.FromFunction(new NativeFunction("log", (_, _, _) => NativeValue.Undefined)));
        consoleObj.Set("warn", NativeValue.FromFunction(new NativeFunction("warn", (_, _, _) => NativeValue.Undefined)));
        consoleObj.Set("error", NativeValue.FromFunction(new NativeFunction("error", (_, _, _) => NativeValue.Undefined)));
        _globalEnv.Declare("console", NativeValue.FromObject(consoleObj));

        // 3. Date
        var dateObj = new NativeObject();
        dateObj.Set("now", NativeValue.FromFunction(new NativeFunction("now", (vm, _, _) => NativeValue.FromNumber(vm._clock().ToUnixTimeMilliseconds()))));
        _globalEnv.Declare("Date", NativeValue.FromObject(dateObj));

        // 4. JSON
        var jsonObj = new NativeObject();
        jsonObj.Set("stringify", NativeValue.FromFunction(new NativeFunction("stringify", (_, _, args) =>
        {
            if (args.Length == 0) return NativeValue.Undefined;
            var str = JsonSerializer.Serialize(args[0].ToString());
            return NativeValue.FromString(str);
        })));
        jsonObj.Set("parse", NativeValue.FromFunction(new NativeFunction("parse", (_, _, args) =>
        {
            if (args.Length == 0 || !args[0].IsString) return NativeValue.Undefined;
            return NativeValue.FromString(args[0].AsString());
        })));
        _globalEnv.Declare("JSON", NativeValue.FromObject(jsonObj));

        // 5. queueMicrotask
        _globalEnv.Declare("queueMicrotask", NativeValue.FromFunction(new NativeFunction("queueMicrotask", (vm, _, args) =>
        {
            if (args.Length > 0 && args[0].IsFunction)
            {
                var fn = args[0].AsFunction();
                vm.EnqueueMicrotask(() => vm.CallFunction(fn, NativeValue.Undefined, []));
            }
            return NativeValue.Undefined;
        })));

        // 6. Object
        var objectCtor = new NativeObject();
        objectCtor.Set("keys", NativeValue.FromFunction(new NativeFunction("keys", (_, _, args) =>
        {
            if (args.Length == 0 || !args[0].IsObject) return NativeValue.FromArray(new NativeArray());
            var keys = args[0].AsObject().Keys.Select(NativeValue.FromString);
            return NativeValue.FromArray(new NativeArray(keys));
        })));
        objectCtor.Set("values", NativeValue.FromFunction(new NativeFunction("values", (_, _, args) =>
        {
            if (args.Length == 0 || !args[0].IsObject) return NativeValue.FromArray(new NativeArray());
            var obj = args[0].AsObject();
            var vals = obj.Keys.Select(k => obj.Get(k));
            return NativeValue.FromArray(new NativeArray(vals));
        })));
        objectCtor.Set("entries", NativeValue.FromFunction(new NativeFunction("entries", (_, _, args) =>
        {
            if (args.Length == 0 || !args[0].IsObject) return NativeValue.FromArray(new NativeArray());
            var obj = args[0].AsObject();
            var entries = obj.Keys.Select(k => (NativeValue)NativeValue.FromArray(new NativeArray([NativeValue.FromString(k), obj.Get(k)])));
            return NativeValue.FromArray(new NativeArray(entries));
        })));
        objectCtor.Set("assign", NativeValue.FromFunction(new NativeFunction("assign", (_, _, args) =>
        {
            if (args.Length == 0) return NativeValue.Undefined;
            var target = args[0];
            if (!target.IsObject) return target;
            var targetObj = target.AsObject();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].IsObject)
                {
                    var src = args[i].AsObject();
                    foreach (var k in src.Keys) targetObj.Set(k, src.Get(k));
                }
            }
            return target;
        })));
        _globalEnv.Declare("Object", NativeValue.FromObject(objectCtor));

        // 7. Promise
        var promiseCtor = new NativeObject();
        promiseCtor.Set("resolve", NativeValue.FromFunction(new NativeFunction("resolve", (_, _, args) =>
        {
            var resVal = args.Length > 0 ? args[0] : NativeValue.Undefined;
            var pObj = new NativeObject();
            pObj.Set("then", NativeValue.FromFunction(new NativeFunction("then", (vm, _, thenArgs) =>
            {
                if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                {
                    var callback = thenArgs[0].AsFunction();
                    vm.EnqueueMicrotask(() => vm.CallFunction(callback, NativeValue.Undefined, [resVal]));
                }
                return NativeValue.FromObject(pObj);
            })));
            pObj.Set("catch", NativeValue.FromFunction(new NativeFunction("catch", (_, _, _) => NativeValue.FromObject(pObj))));
            return NativeValue.FromObject(pObj);
        })));
        promiseCtor.Set("reject", NativeValue.FromFunction(new NativeFunction("reject", (_, _, args) =>
        {
            var errVal = args.Length > 0 ? args[0] : NativeValue.Undefined;
            var pObj = new NativeObject();
            pObj.Set("then", NativeValue.FromFunction(new NativeFunction("then", (_, _, _) => NativeValue.FromObject(pObj))));
            pObj.Set("catch", NativeValue.FromFunction(new NativeFunction("catch", (vm, _, catchArgs) =>
            {
                if (catchArgs.Length > 0 && catchArgs[0].IsFunction)
                {
                    var callback = catchArgs[0].AsFunction();
                    vm.EnqueueMicrotask(() => vm.CallFunction(callback, NativeValue.Undefined, [errVal]));
                }
                return NativeValue.FromObject(pObj);
            })));
            return NativeValue.FromObject(pObj);
        })));
        _globalEnv.Declare("Promise", NativeValue.FromObject(promiseCtor));
    }

    private static NativeObject CreateObjectPrototype()
    {
        var proto = new NativeObject();
        proto.Set("hasOwnProperty", NativeValue.FromFunction(new NativeFunction("hasOwnProperty", (_, thisVal, args) =>
        {
            if (args.Length == 0 || !thisVal.IsObject) return NativeValue.False;
            return NativeValue.FromBoolean(thisVal.AsObject().Keys.Contains(args[0].AsString()));
        })));
        return proto;
    }

    private static NativeObject CreateArrayPrototype(NativeObject parentProto)
    {
        var proto = new NativeObject { Prototype = parentProto };
        proto.Set("push", NativeValue.FromFunction(new NativeFunction("push", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            for (var i = 0; i < args.Length; i++) arr.Elements.Add(args[i]);
            return NativeValue.FromNumber(arr.Elements.Count);
        })));
        proto.Set("pop", NativeValue.FromFunction(new NativeFunction("pop", (_, thisVal, _) =>
        {
            var arr = thisVal.AsArray();
            if (arr.Elements.Count == 0) return NativeValue.Undefined;
            var last = arr.Elements[^1];
            arr.Elements.RemoveAt(arr.Elements.Count - 1);
            return last;
        })));
        proto.Set("shift", NativeValue.FromFunction(new NativeFunction("shift", (_, thisVal, _) =>
        {
            var arr = thisVal.AsArray();
            if (arr.Elements.Count == 0) return NativeValue.Undefined;
            var first = arr.Elements[0];
            arr.Elements.RemoveAt(0);
            return first;
        })));
        proto.Set("unshift", NativeValue.FromFunction(new NativeFunction("unshift", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            for (var i = args.Length - 1; i >= 0; i--) arr.Elements.Insert(0, args[i]);
            return NativeValue.FromNumber(arr.Elements.Count);
        })));
        proto.Set("slice", NativeValue.FromFunction(new NativeFunction("slice", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            int start = args.Length > 0 ? (int)args[0].AsNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].AsNumber() : arr.Elements.Count;
            if (start < 0) start = Math.Max(0, arr.Elements.Count + start);
            if (end < 0) end = Math.Max(0, arr.Elements.Count + end);
            start = Math.Clamp(start, 0, arr.Elements.Count);
            end = Math.Clamp(end, start, arr.Elements.Count);
            var res = new NativeArray(end - start);
            for (var i = start; i < end; i++) res.Elements.Add(arr.Elements[i]);
            return NativeValue.FromArray(res);
        })));
        proto.Set("join", NativeValue.FromFunction(new NativeFunction("join", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            var sep = args.Length > 0 ? args[0].AsString() : ",";
            return NativeValue.FromString(string.Join(sep, arr.Elements.Select(e => e.ToString())));
        })));
        proto.Set("indexOf", NativeValue.FromFunction(new NativeFunction("indexOf", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            if (args.Length == 0) return NativeValue.FromNumber(-1);
            var target = args[0];
            for (var i = 0; i < arr.Elements.Count; i++)
                if (NativeValue.StrictEqual(arr.Elements[i], target).AsBoolean()) return NativeValue.FromNumber(i);
            return NativeValue.FromNumber(-1);
        })));
        proto.Set("includes", NativeValue.FromFunction(new NativeFunction("includes", (_, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            if (args.Length == 0) return NativeValue.False;
            var target = args[0];
            for (var i = 0; i < arr.Elements.Count; i++)
                if (NativeValue.StrictEqual(arr.Elements[i], target).AsBoolean()) return NativeValue.True;
            return NativeValue.False;
        })));
        proto.Set("forEach", NativeValue.FromFunction(new NativeFunction("forEach", (vm, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            if (args.Length > 0 && args[0].IsFunction)
            {
                var fn = args[0].AsFunction();
                for (var i = 0; i < arr.Elements.Count; i++)
                    vm.CallFunction(fn, NativeValue.Undefined, [arr.Elements[i], NativeValue.FromNumber(i), thisVal]);
            }
            return NativeValue.Undefined;
        })));
        proto.Set("map", NativeValue.FromFunction(new NativeFunction("map", (vm, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            var res = new NativeArray(arr.Elements.Count);
            if (args.Length > 0 && args[0].IsFunction)
            {
                var fn = args[0].AsFunction();
                for (var i = 0; i < arr.Elements.Count; i++)
                    res.Elements.Add(vm.CallFunction(fn, NativeValue.Undefined, [arr.Elements[i], NativeValue.FromNumber(i), thisVal]));
            }
            return NativeValue.FromArray(res);
        })));
        proto.Set("filter", NativeValue.FromFunction(new NativeFunction("filter", (vm, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            var res = new NativeArray();
            if (args.Length > 0 && args[0].IsFunction)
            {
                var fn = args[0].AsFunction();
                for (var i = 0; i < arr.Elements.Count; i++)
                {
                    var keep = vm.CallFunction(fn, NativeValue.Undefined, [arr.Elements[i], NativeValue.FromNumber(i), thisVal]);
                    if (keep.IsTruthy()) res.Elements.Add(arr.Elements[i]);
                }
            }
            return NativeValue.FromArray(res);
        })));
        proto.Set("reduce", NativeValue.FromFunction(new NativeFunction("reduce", (vm, thisVal, args) =>
        {
            var arr = thisVal.AsArray();
            if (args.Length == 0 || !args[0].IsFunction) return NativeValue.Undefined;
            var fn = args[0].AsFunction();
            int startIdx = 0;
            NativeValue acc;
            if (args.Length > 1)
            {
                acc = args[1];
            }
            else
            {
                if (arr.Elements.Count == 0) throw new ScriptRuntimeException("Reduce of empty array with no initial value");
                acc = arr.Elements[0];
                startIdx = 1;
            }
            for (var i = startIdx; i < arr.Elements.Count; i++)
                acc = vm.CallFunction(fn, NativeValue.Undefined, [acc, arr.Elements[i], NativeValue.FromNumber(i), thisVal]);
            return acc;
        })));
        return proto;
    }

    private static NativeObject CreateStringPrototype(NativeObject parentProto)
    {
        var proto = new NativeObject { Prototype = parentProto };
        proto.Set("charAt", NativeValue.FromFunction(new NativeFunction("charAt", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var idx = args.Length > 0 ? (int)args[0].AsNumber() : 0;
            return idx >= 0 && idx < str.Length ? NativeValue.FromString(str[idx].ToString()) : NativeValue.FromString("");
        })));
        proto.Set("charCodeAt", NativeValue.FromFunction(new NativeFunction("charCodeAt", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var idx = args.Length > 0 ? (int)args[0].AsNumber() : 0;
            return idx >= 0 && idx < str.Length ? NativeValue.FromNumber(str[idx]) : NativeValue.FromNumber(double.NaN);
        })));
        proto.Set("slice", NativeValue.FromFunction(new NativeFunction("slice", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            int start = args.Length > 0 ? (int)args[0].AsNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].AsNumber() : str.Length;
            if (start < 0) start = Math.Max(0, str.Length + start);
            if (end < 0) end = Math.Max(0, str.Length + end);
            start = Math.Clamp(start, 0, str.Length);
            end = Math.Clamp(end, start, str.Length);
            return NativeValue.FromString(str[start..end]);
        })));
        proto.Set("substring", NativeValue.FromFunction(new NativeFunction("substring", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            int start = args.Length > 0 ? (int)args[0].AsNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].AsNumber() : str.Length;
            start = Math.Clamp(start, 0, str.Length);
            end = Math.Clamp(end, 0, str.Length);
            if (start > end) (start, end) = (end, start);
            return NativeValue.FromString(str[start..end]);
        })));
        proto.Set("indexOf", NativeValue.FromFunction(new NativeFunction("indexOf", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var search = args.Length > 0 ? args[0].AsString() : "";
            var from = args.Length > 1 ? (int)args[1].AsNumber() : 0;
            return NativeValue.FromNumber(str.IndexOf(search, Math.Clamp(from, 0, str.Length), StringComparison.Ordinal));
        })));
        proto.Set("includes", NativeValue.FromFunction(new NativeFunction("includes", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var search = args.Length > 0 ? args[0].AsString() : "";
            return NativeValue.FromBoolean(str.Contains(search, StringComparison.Ordinal));
        })));
        proto.Set("startsWith", NativeValue.FromFunction(new NativeFunction("startsWith", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var search = args.Length > 0 ? args[0].AsString() : "";
            return NativeValue.FromBoolean(str.StartsWith(search, StringComparison.Ordinal));
        })));
        proto.Set("endsWith", NativeValue.FromFunction(new NativeFunction("endsWith", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var search = args.Length > 0 ? args[0].AsString() : "";
            return NativeValue.FromBoolean(str.EndsWith(search, StringComparison.Ordinal));
        })));
        proto.Set("split", NativeValue.FromFunction(new NativeFunction("split", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var sep = args.Length > 0 ? args[0].AsString() : "";
            var parts = sep.Length == 0 ? str.Select(c => c.ToString()).ToArray() : str.Split(sep);
            var arr = new NativeArray(parts.Length);
            foreach (var p in parts) arr.Elements.Add(NativeValue.FromString(p));
            return NativeValue.FromArray(arr);
        })));
        proto.Set("replace", NativeValue.FromFunction(new NativeFunction("replace", (_, thisVal, args) =>
        {
            var str = thisVal.AsString();
            var target = args.Length > 0 ? args[0].AsString() : "";
            var replacement = args.Length > 1 ? args[1].AsString() : "";
            var idx = str.IndexOf(target, StringComparison.Ordinal);
            if (idx >= 0)
                return NativeValue.FromString(str[..idx] + replacement + str[(idx + target.Length)..]);
            return NativeValue.FromString(str);
        })));
        proto.Set("toLowerCase", NativeValue.FromFunction(new NativeFunction("toLowerCase", (_, thisVal, _) => NativeValue.FromString(thisVal.AsString().ToLowerInvariant()))));
        proto.Set("toUpperCase", NativeValue.FromFunction(new NativeFunction("toUpperCase", (_, thisVal, _) => NativeValue.FromString(thisVal.AsString().ToUpperInvariant()))));
        proto.Set("trim", NativeValue.FromFunction(new NativeFunction("trim", (_, thisVal, _) => NativeValue.FromString(thisVal.AsString().Trim()))));
        return proto;
    }

    public NativeValue Execute(BytecodeChunk chunk)
    {
        _statementCount = 0;
        _stopwatch = Stopwatch.StartNew();

        var initialFrame = new CallFrame(chunk, 0, NativeValue.Undefined, _globalEnv);
        _frames.Clear();
        _stack.Clear();
        _frames.Add(initialFrame);

        return Run();
    }

    public NativeValue CallFunction(NativeFunction func, NativeValue thisValue, ReadOnlySpan<NativeValue> args)
    {
        if (func.HostCallback is not null)
        {
            return func.HostCallback(this, thisValue, args);
        }

        if (func.Chunk is null)
            return NativeValue.Undefined;

        if (_frames.Count >= _limits.MaxRecursionDepth)
            throw new ScriptLimitExceededException("Recursion", $"Call stack depth exceeded maximum recursion depth of {_limits.MaxRecursionDepth}.");

        var env = new LexicalEnvironment(func.Environment ?? _globalEnv);
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            var val = i < args.Length ? args[i] : NativeValue.Undefined;
            env.Declare(func.Parameters[i], val);
        }

        var stackBase = _stack.Count;
        var frame = new CallFrame(func.Chunk, stackBase, thisValue, env);
        _frames.Add(frame);

        return Run();
    }

    private NativeValue Run()
    {
        var targetFrameCount = _frames.Count - 1;

        while (_frames.Count > targetFrameCount)
        {
            if (_interrupted)
                throw new ScriptInterruptedException("Script execution interrupted.");

            if (_limits.MaxStatements > 0 && ++_statementCount > _limits.MaxStatements)
                throw new ScriptLimitExceededException("Statements", $"Statement limit of {_limits.MaxStatements} exceeded.");

            if (_limits.Timeout > TimeSpan.Zero && _stopwatch?.Elapsed > _limits.Timeout)
                throw new ScriptLimitExceededException("Timeout", $"Execution timed out after {_limits.Timeout}.");

            var frame = _frames[^1];
            if (frame.Ip >= frame.Chunk.Instructions.Count)
            {
                _frames.RemoveAt(_frames.Count - 1);
                continue;
            }

            var instr = frame.Chunk.Instructions[frame.Ip++];
            switch (instr.Op)
            {
                case OpCode.Nop:
                    break;

                case OpCode.PushConst:
                    Push(frame.Chunk.Constants[instr.Operand]);
                    break;

                case OpCode.PushUndefined:
                    Push(NativeValue.Undefined);
                    break;

                case OpCode.PushNull:
                    Push(NativeValue.Null);
                    break;

                case OpCode.PushTrue:
                    Push(NativeValue.True);
                    break;

                case OpCode.PushFalse:
                    Push(NativeValue.False);
                    break;

                case OpCode.PushThis:
                    Push(frame.ThisValue);
                    break;

                case OpCode.Pop:
                    Pop();
                    break;

                case OpCode.Dup:
                    Push(Peek());
                    break;

                case OpCode.LoadGlobal:
                    Push(frame.Environment.Get(instr.StringOperand!));
                    break;

                case OpCode.StoreGlobal:
                    frame.Environment.Set(instr.StringOperand!, Peek());
                    break;

                case OpCode.LoadProperty:
                {
                    var target = Pop();
                    var propName = instr.StringOperand!;
                    if (target.IsObject)
                    {
                        var obj = target.AsObject();
                        var cacheIdx = instr.Operand;
                        if (cacheIdx >= 0 && cacheIdx < frame.Chunk.InlineCaches.Count)
                        {
                            var ic = frame.Chunk.InlineCaches[cacheIdx];
                            if (ReferenceEquals(ic.CachedTarget, obj) && ic.CachedVersion == obj.Version)
                            {
                                Push(ic.CachedValue);
                                break;
                            }
                            var val = obj.Get(propName);
                            ic.CachedTarget = obj;
                            ic.CachedVersion = obj.Version;
                            ic.CachedValue = val;
                            Push(val);
                        }
                        else
                        {
                            Push(obj.Get(propName));
                        }
                    }
                    else if (target.IsString)
                    {
                        var str = target.AsString();
                        if (propName == "length")
                            Push(NativeValue.FromNumber(str.Length));
                        else
                            Push(StringPrototype.Get(propName));
                    }
                    else
                    {
                        Push(NativeValue.Undefined);
                    }
                    break;
                }

                case OpCode.LoadMethod:
                {
                    var target = Pop();
                    var propName = instr.StringOperand!;
                    if (target.IsObject)
                    {
                        var obj = target.AsObject();
                        var method = obj.Get(propName);
                        Push(method);
                        Push(target); // thisVal
                    }
                    else if (target.IsString)
                    {
                        var method = StringPrototype.Get(propName);
                        Push(method);
                        Push(target); // thisVal
                    }
                    else
                    {
                        Push(NativeValue.Undefined);
                        Push(NativeValue.Undefined);
                    }
                    break;
                }

                case OpCode.StoreProperty:
                {
                    var target = Pop();
                    var val = Peek();
                    var propName = instr.StringOperand!;
                    if (target.IsObject)
                    {
                        var obj = target.AsObject();
                        obj.Set(propName, val);
                    }
                    break;
                }

                case OpCode.SetProperty:
                {
                    var val = Pop();
                    var target = Peek();
                    var propName = instr.StringOperand!;
                    if (target.IsObject)
                    {
                        var obj = target.AsObject();
                        obj.Set(propName, val);
                    }
                    break;
                }

                case OpCode.LoadElement:
                {
                    var key = Pop();
                    var target = Pop();
                    if (target.IsArray && key.IsNumber)
                    {
                        var arr = target.AsArray();
                        var idx = (int)key.AsNumber();
                        Push(idx >= 0 && idx < arr.Elements.Count ? arr.Elements[idx] : NativeValue.Undefined);
                    }
                    else if (target.IsString && key.IsNumber)
                    {
                        var str = target.AsString();
                        var idx = (int)key.AsNumber();
                        Push(idx >= 0 && idx < str.Length ? NativeValue.FromString(str[idx].ToString()) : NativeValue.Undefined);
                    }
                    else if (target.IsObject)
                    {
                        Push(target.AsObject().Get(key.AsString()));
                    }
                    else
                    {
                        Push(NativeValue.Undefined);
                    }
                    break;
                }

                case OpCode.StoreElement:
                {
                    var key = Pop();
                    var target = Pop();
                    var val = Peek();
                    if (target.IsArray && key.IsNumber)
                    {
                        var arr = target.AsArray();
                        var idx = (int)key.AsNumber();
                        if (idx >= 0)
                        {
                            while (arr.Elements.Count <= idx)
                                arr.Elements.Add(NativeValue.Undefined);
                            arr.Elements[idx] = val;
                        }
                    }
                    else if (target.IsObject)
                    {
                        target.AsObject().Set(key.AsString(), val);
                    }
                    break;
                }

                case OpCode.CreateObject:
                {
                    var propCount = instr.Operand;
                    var obj = new NativeObject();
                    // Values were pushed, let's pop them if needed
                    Push(NativeValue.FromObject(obj));
                    break;
                }

                case OpCode.CreateArray:
                {
                    var count = instr.Operand;
                    var elements = new NativeValue[count];
                    for (var i = count - 1; i >= 0; i--)
                        elements[i] = Pop();
                    Push(NativeValue.FromArray(new NativeArray(elements)));
                    break;
                }

                case OpCode.Add:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Add(a, b));
                    break;
                }

                case OpCode.Sub:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Sub(a, b));
                    break;
                }

                case OpCode.Mul:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Mul(a, b));
                    break;
                }

                case OpCode.Div:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Div(a, b));
                    break;
                }

                case OpCode.Mod:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Mod(a, b));
                    break;
                }

                case OpCode.Neg:
                {
                    var a = Pop();
                    Push(NativeValue.FromNumber(-a.AsNumber()));
                    break;
                }

                case OpCode.Not:
                {
                    var a = Pop();
                    Push(NativeValue.FromBoolean(!a.IsTruthy()));
                    break;
                }

                case OpCode.Eq:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.Equal(a, b));
                    break;
                }

                case OpCode.NotEq:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.FromBoolean(!NativeValue.Equal(a, b).AsBoolean()));
                    break;
                }

                case OpCode.StrictEq:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.StrictEqual(a, b));
                    break;
                }

                case OpCode.StrictNotEq:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.FromBoolean(!NativeValue.StrictEqual(a, b).AsBoolean()));
                    break;
                }

                case OpCode.Lt:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.LessThan(a, b));
                    break;
                }

                case OpCode.Lte:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.FromBoolean(NativeValue.LessThan(a, b).AsBoolean() || NativeValue.Equal(a, b).AsBoolean()));
                    break;
                }

                case OpCode.Gt:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.GreaterThan(a, b));
                    break;
                }

                case OpCode.Gte:
                {
                    var b = Pop();
                    var a = Pop();
                    Push(NativeValue.FromBoolean(NativeValue.GreaterThan(a, b).AsBoolean() || NativeValue.Equal(a, b).AsBoolean()));
                    break;
                }

                case OpCode.Jump:
                    frame.Ip = instr.Operand;
                    break;

                case OpCode.JumpIfFalse:
                {
                    var cond = Pop();
                    if (!cond.IsTruthy())
                        frame.Ip = instr.Operand;
                    break;
                }

                case OpCode.JumpIfTrue:
                {
                    var cond = Pop();
                    if (cond.IsTruthy())
                        frame.Ip = instr.Operand;
                    break;
                }

                case OpCode.Call:
                {
                    var argCount = instr.Operand;
                    var args = new NativeValue[argCount];
                    for (var i = argCount - 1; i >= 0; i--)
                        args[i] = Pop();

                    var thisVal = Pop();
                    var funcVal = Pop();

                    if (!funcVal.IsFunction)
                    {
                        Push(NativeValue.Undefined);
                        break;
                    }

                    var fn = funcVal.AsFunction();
                    if (fn.HostCallback is not null)
                    {
                        var res = fn.HostCallback(this, thisVal, args);
                        Push(res);
                    }
                    else if (fn.Chunk is not null)
                    {
                        if (_frames.Count >= _limits.MaxRecursionDepth)
                            throw new ScriptLimitExceededException("Recursion", $"Call stack depth exceeded maximum recursion depth of {_limits.MaxRecursionDepth}.");

                        var childEnv = new LexicalEnvironment(fn.Environment ?? _globalEnv);
                        for (var i = 0; i < fn.Parameters.Count; i++)
                        {
                            var v = i < args.Length ? args[i] : NativeValue.Undefined;
                            childEnv.Declare(fn.Parameters[i], v);
                        }

                        var childFrame = new CallFrame(fn.Chunk, _stack.Count, thisVal, childEnv);
                        _frames.Add(childFrame);
                    }
                    else
                    {
                        Push(NativeValue.Undefined);
                    }
                    break;
                }

                case OpCode.Return:
                {
                    var retVal = _stack.Count > frame.StackBase ? Pop() : NativeValue.Undefined;
                    while (_stack.Count > frame.StackBase)
                        Pop();
                    _frames.RemoveAt(_frames.Count - 1);
                    if (_frames.Count > targetFrameCount)
                        Push(retVal);
                    else
                        return retVal;
                    break;
                }

                case OpCode.Throw:
                {
                    var err = Pop();
                    if (frame.TryCatchTarget >= 0)
                    {
                        frame.Ip = frame.TryCatchTarget;
                        Push(err);
                    }
                    else
                    {
                        throw new ScriptRuntimeException(err.ToString());
                    }
                    break;
                }

                case OpCode.EnterTry:
                    frame.TryCatchTarget = instr.Operand;
                    break;

                case OpCode.LeaveTry:
                    frame.TryCatchTarget = -1;
                    break;

                case OpCode.CreateClosure:
                {
                    var fnChunk = frame.Chunk.Functions[instr.Operand];
                    var fn = new NativeFunction(instr.StringOperand, fnChunk.ParameterNames, fnChunk, frame.Environment);
                    Push(NativeValue.FromFunction(fn));
                    break;
                }
            }
        }

        return _stack.Count > 0 ? Pop() : NativeValue.Undefined;
    }

    private void Push(NativeValue val) => _stack.Add(val);

    private NativeValue Pop()
    {
        if (_stack.Count == 0) return NativeValue.Undefined;
        var val = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        return val;
    }

    private NativeValue Peek() => _stack.Count > 0 ? _stack[^1] : NativeValue.Undefined;
}
