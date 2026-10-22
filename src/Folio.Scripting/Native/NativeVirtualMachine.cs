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
                        Push(obj.Get(propName));
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
