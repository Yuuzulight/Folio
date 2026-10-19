namespace Folio.Scripting.Dom;

/// <summary>Private permission and quota boundary for the compatibility WebAssembly interpreter.</summary>
internal static class WebAssemblyBindings
{
    internal static void Install(IScriptEngine engine, DomContext context)
    {
        var host = new HostObjectTemplate("WebAssemblyHost")
            .DefineMethod("enabled", (_, _, _) => ScriptValue.FromBoolean(
                context.CheckCapability("WebAssembly", context.Capabilities.WebAssembly)))
            .DefineProperty("maxBytes", (_, _) => ScriptValue.FromNumber(context.MaxWebAssemblyBytes));
        engine.SetGlobalProperty("__folio_wasm_host", engine.CreateHostObject(host, context));
        engine.EvaluateHostScript(Source, "folio:webassembly");
    }

    private const string Source = """
        (function(host) {
            const apply = Reflect.apply, check = host.enabled;
            const intrinsic_Math_abs = globalThis.Math.abs;
            const intrinsic_Math_ceil = globalThis.Math.ceil;
            const intrinsic_Math_clz32 = globalThis.Math.clz32;
            const intrinsic_Math_floor = globalThis.Math.floor;
            const intrinsic_Math_max = globalThis.Math.max;
            const intrinsic_Math_min = globalThis.Math.min;
            const intrinsic_Math_pow = globalThis.Math.pow;
            const intrinsic_Math_round = globalThis.Math.round;
            const intrinsic_Math_sqrt = globalThis.Math.sqrt;
            const intrinsic_Math_trunc = globalThis.Math.trunc;
            const intrinsic_Number_isInteger = globalThis.Number.isInteger;
            const intrinsic_Number_isSafeInteger = globalThis.Number.isSafeInteger;
            const intrinsic_Object_defineProperty = globalThis.Object.defineProperty;
            const intrinsic_Object_keys = globalThis.Object.keys;
            const intrinsic_String_fromCharCode = globalThis.String.fromCharCode;
            const String = globalThis.String, ArrayCtor = globalThis.Array, PromiseCtor = globalThis.Promise;
            const arrayFromCallback = ArrayCtor.from, resolveCallback = PromiseCtor.resolve, rejectCallback = PromiseCtor.reject;
            function arrayFrom(value) { return apply(arrayFromCallback, ArrayCtor, [value]); }
            function resolvePromise(value) { return apply(resolveCallback, PromiseCtor, [value]); }
            function rejectPromise(value) { return apply(rejectCallback, PromiseCtor, [value]); }
            const Number = globalThis.Number, ArrayBuffer = globalThis.ArrayBuffer;
            const Uint8Array = globalThis.Uint8Array, Map = globalThis.Map;
            const DOMException = globalThis.DOMException, TypeError = globalThis.TypeError, RangeError = globalThis.RangeError;
            const __folio_wasm_max_bytes = host.maxBytes;
            const __folio_wasm_max_pages = intrinsic_Math_floor(__folio_wasm_max_bytes / 65536);
            const __folio_wasm_max_table_elements = intrinsic_Math_floor(__folio_wasm_max_bytes / 4);
            const __folio_wasm_max_globals = intrinsic_Math_floor(__folio_wasm_max_bytes / 16);
            var __folio_wasm_global_count = 0;
            function enabled() { return apply(check, host, []); }
            function denied() { return new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError'); }
            function requireAllowed() { if (!enabled()) throw denied(); }
            function __folio_wasm_read_u32(bytes, cursor) { var value = 0; var shift = 0; while (cursor.i < bytes.length) { var part = bytes[cursor.i++]; value |= (part & 127) << shift; if ((part & 128) === 0) break; shift += 7; } return value >>> 0; }
            function __folio_wasm_read_name(bytes, cursor) { var length = __folio_wasm_read_u32(bytes, cursor); var text = ''; for (var i = 0; i < length && cursor.i < bytes.length; i++) text += intrinsic_String_fromCharCode(bytes[cursor.i++]); return text; }
            function __folio_wasm_skip_limits(bytes, cursor) { var flags = bytes[cursor.i++]; __folio_wasm_read_u32(bytes, cursor); if (flags & 1) __folio_wasm_read_u32(bytes, cursor); }
            function __folio_wasm_read_s32(bytes, cursor) { var value = 0; var shift = 0; var part = 0; do { part = bytes[cursor.i++]; value |= (part & 127) << shift; shift += 7; } while (part & 128); if (shift < 32 && (part & 64)) value |= (~0 << shift); return value | 0; } function __folio_wasm_read_f32(bytes, cursor) { var raw = (bytes[cursor.i++] | (bytes[cursor.i++] << 8) | (bytes[cursor.i++] << 16) | (bytes[cursor.i++] << 24)) >>> 0; var exponent = (raw >>> 23) & 255; var mantissa = raw & 8388607; if (exponent === 255) return mantissa === 0 ? ((raw >>> 31) ? -Infinity : Infinity) : NaN; var value = (exponent === 0 ? mantissa / 8388608 : 1 + mantissa / 8388608) * intrinsic_Math_pow(2, exponent === 0 ? -126 : exponent - 127); return (raw >>> 31) ? -value : value; }
            function __folio_wasm_skip_control(bytes, cursor, stopAtElse) { var depth = 1; while (cursor.i < bytes.length) { var op = bytes[cursor.i++]; if (op === 0x02 || op === 0x03 || op === 0x04) { cursor.i++; depth++; } else if (op === 0x0b) { depth--; if (depth === 0) { cursor.i--; return; } } else if (op === 0x05 && depth === 1 && stopAtElse) return; } }
            function __folio_wasm_functions(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = intrinsic_Math_min(bytes.length, cursor.i + size); if (section !== 10) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { var bodySize = __folio_wasm_read_u32(bytes, cursor); result.push(bytes.slice(cursor.i, intrinsic_Math_min(end, cursor.i + bodySize))); cursor.i += bodySize; } cursor.i = end; }
                return result;
            }
            function __folio_wasm_globals(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = intrinsic_Math_min(bytes.length, cursor.i + size); if (section !== 6) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { cursor.i += 2; var opcode = bytes[cursor.i++]; var value = opcode === 0x41 ? __folio_wasm_read_s32(bytes, cursor) : 0; while (cursor.i < end && bytes[cursor.i++] !== 0x0b) {} result.push(value | 0); } cursor.i = end; }
                return result;
            }
            function __folio_wasm_memories(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = intrinsic_Math_min(bytes.length, cursor.i + size); if (section !== 5) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { var flags = bytes[cursor.i++]; var initial = __folio_wasm_read_u32(bytes, cursor); var maximum = flags & 1 ? __folio_wasm_read_u32(bytes, cursor) : __folio_wasm_max_pages; result.push({ initial: initial, maximum: maximum }); } cursor.i = end; }
                return result;
            }
            function __folio_wasm_tables(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = intrinsic_Math_min(bytes.length, cursor.i + size); if (section !== 4) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { cursor.i++; var flags = bytes[cursor.i++]; var initial = __folio_wasm_read_u32(bytes, cursor); var maximum = flags & 1 ? __folio_wasm_read_u32(bytes, cursor) : 100000; result.push({ initial: initial, maximum: maximum }); } cursor.i = end; }
                return result;
            }
            var __folio_wasm_call_depth = 0;
            function __folio_wasm_call(body, argumentsObject, instanceExports, importedFunctions, invokeFunction, functionTables, functionGlobals) {
                requireAllowed();
                if (__folio_wasm_call_depth >= 32) throw new WebAssembly.RuntimeError('WebAssembly call stack limit exceeded.');
                __folio_wasm_call_depth++;
                try { var result = __folio_wasm_execute(body, argumentsObject, instanceExports, importedFunctions, invokeFunction, functionTables, functionGlobals); requireAllowed(); return result; }
                finally { __folio_wasm_call_depth--; }
            }
            function __folio_wasm_execute(body, argumentsObject, instanceExports, importedFunctions, invokeFunction, functionTables, functionGlobals) {
                var cursor = { i: 0 }; var localGroups = __folio_wasm_read_u32(body, cursor); var locals = []; for (var argumentIndex = 0; argumentIndex < argumentsObject.length; argumentIndex++) locals.push(Number(argumentsObject[argumentIndex]) | 0); for (var group = 0; group < localGroups; group++) { var localCount = __folio_wasm_read_u32(body, cursor); cursor.i++; for (var localIndex = 0; localIndex < localCount; localIndex++) locals.push(0); }
                var stack = []; while (cursor.i < body.length) { requireAllowed(); var opcode = body[cursor.i++]; if (opcode === 0x00) throw new WebAssembly.RuntimeError('Unreachable WebAssembly instruction.'); else if (opcode === 0x01) { } else if (opcode === 0x1a) { stack.pop(); } else if (opcode === 0x1b) { var selectCondition = stack.pop() || 0; var selectFalse = stack.pop(); var selectTrue = stack.pop(); stack.push(selectCondition ? selectTrue : selectFalse); } else if (opcode === 0x41) stack.push(__folio_wasm_read_s32(body, cursor)); else if (opcode === 0x43) stack.push(__folio_wasm_read_f32(body, cursor)); else if (opcode === 0x20) { var getIndex = __folio_wasm_read_u32(body, cursor); stack.push(locals[getIndex] || 0); } else if (opcode === 0x21 || opcode === 0x22) { var setIndex = __folio_wasm_read_u32(body, cursor); var setValue = stack.pop() || 0; locals[setIndex] = setValue; if (opcode === 0x22) stack.push(setValue); } else if (opcode === 0x23) { var globalIndex = __folio_wasm_read_u32(body, cursor); var global = functionGlobals && functionGlobals[globalIndex]; if (!global) throw new WebAssembly.RuntimeError('WebAssembly global is unavailable.'); stack.push(Number(global.value) | 0); } else if (opcode === 0x24) { var setGlobalIndex = __folio_wasm_read_u32(body, cursor); var setGlobal = functionGlobals && functionGlobals[setGlobalIndex]; if (!setGlobal) throw new WebAssembly.RuntimeError('WebAssembly global is unavailable.'); setGlobal.value = stack.pop() || 0; } else if (opcode === 0x45) stack.push((stack.pop() || 0) === 0 ? 1 : 0); else if (opcode === 0x46 || opcode === 0x47) { var equalityRight = stack.pop() || 0; var equalityLeft = stack.pop() || 0; stack.push((opcode === 0x46 ? equalityLeft === equalityRight : equalityLeft !== equalityRight) ? 1 : 0); } else if (opcode >= 0x5b && opcode <= 0x60) { var floatRight = Number(stack.pop()); var floatLeft = Number(stack.pop()); var floatCompare = opcode === 0x5b ? floatLeft === floatRight : opcode === 0x5c ? floatLeft !== floatRight : opcode === 0x5d ? floatLeft < floatRight : opcode === 0x5e ? floatLeft > floatRight : opcode === 0x5f ? floatLeft <= floatRight : floatLeft >= floatRight; stack.push(floatCompare ? 1 : 0); } else if (opcode >= 0x48 && opcode <= 0x4f) { var compareRight = stack.pop() || 0; var compareLeft = stack.pop() || 0; var unsigned = (opcode - 0x48) % 2 === 1; var leftValue = unsigned ? compareLeft >>> 0 : compareLeft; var rightValue = unsigned ? compareRight >>> 0 : compareRight; var compare = opcode === 0x48 || opcode === 0x49 ? leftValue < rightValue : opcode === 0x4a || opcode === 0x4b ? leftValue > rightValue : opcode === 0x4c || opcode === 0x4d ? leftValue <= rightValue : leftValue >= rightValue; stack.push(compare ? 1 : 0); } else if (opcode >= 0x8b && opcode <= 0x91) { var unaryFloat = Number(stack.pop()); var unaryResult = opcode === 0x8b ? intrinsic_Math_abs(unaryFloat) : opcode === 0x8c ? -unaryFloat : opcode === 0x8d ? intrinsic_Math_ceil(unaryFloat) : opcode === 0x8e ? intrinsic_Math_floor(unaryFloat) : opcode === 0x8f ? intrinsic_Math_trunc(unaryFloat) : opcode === 0x90 ? intrinsic_Math_round(unaryFloat) : intrinsic_Math_sqrt(unaryFloat); stack.push(unaryResult); } else if (opcode >= 0x92 && opcode <= 0x98) { var floatRightValue = Number(stack.pop()); var floatLeftValue = Number(stack.pop()); var floatArithmetic = opcode === 0x92 ? floatLeftValue + floatRightValue : opcode === 0x93 ? floatLeftValue - floatRightValue : opcode === 0x94 ? floatLeftValue * floatRightValue : opcode === 0x95 ? floatLeftValue / floatRightValue : opcode === 0x96 ? intrinsic_Math_min(floatLeftValue, floatRightValue) : opcode === 0x97 ? intrinsic_Math_max(floatLeftValue, floatRightValue) : (floatRightValue < 0 ? -intrinsic_Math_abs(floatLeftValue) : intrinsic_Math_abs(floatLeftValue)); stack.push(floatArithmetic); } else if (opcode === 0x67 || opcode === 0x68 || opcode === 0x69) { var bits = (stack.pop() || 0) | 0; stack.push((opcode === 0x67 ? intrinsic_Math_clz32(bits) : opcode === 0x68 ? (bits === 0 ? 32 : (function(value) { var count = 0; while ((value & 1) === 0) { count++; value >>>= 1; } return count; })(bits >>> 0)) : bits.toString(2).split('1').length - 1) | 0); } else if (opcode === 0x77 || opcode === 0x78) { var rotateRight = (stack.pop() || 0) & 31; var rotateValue = (stack.pop() || 0) | 0; stack.push((opcode === 0x77 ? (rotateValue << rotateRight) | (rotateValue >>> (32 - rotateRight)) : (rotateValue >>> rotateRight) | (rotateValue << (32 - rotateRight))) | 0); } else if (opcode === 0xc0 || opcode === 0xc1) { var extension = (stack.pop() || 0) | 0; stack.push((opcode === 0xc0 ? (extension << 24) >> 24 : (extension << 16) >> 16) | 0); } else if (opcode === 0x04) { cursor.i++; var condition = stack.pop() || 0; if (!condition) __folio_wasm_skip_control(body, cursor, true); } else if (opcode === 0x05) { __folio_wasm_skip_control(body, cursor, false); } else if (opcode === 0x0f) { return stack.length ? stack[stack.length - 1] : undefined; } else if (opcode === 0x0c) { var branchDepth = __folio_wasm_read_u32(body, cursor); if (branchDepth !== 0) throw new WebAssembly.CompileError('Unsupported WebAssembly branch depth.'); return stack.length ? stack[stack.length - 1] : undefined; } else if (opcode === 0x0d) { var conditionalDepth = __folio_wasm_read_u32(body, cursor); var branchCondition = stack.pop() || 0; if (branchCondition) { if (conditionalDepth !== 0) throw new WebAssembly.CompileError('Unsupported WebAssembly branch depth.'); return stack.length ? stack[stack.length - 1] : undefined; } } else if (opcode === 0x0e) { var labelCount = __folio_wasm_read_u32(body, cursor); var labelIndex = stack.pop() || 0; var selectedDepth = 0; for (var label = 0; label < labelCount; label++) { var depth = __folio_wasm_read_u32(body, cursor); if (label === labelIndex) selectedDepth = depth; } var defaultDepth = __folio_wasm_read_u32(body, cursor); if (labelIndex >= labelCount) selectedDepth = defaultDepth; if (selectedDepth !== 0) throw new WebAssembly.CompileError('Unsupported WebAssembly branch depth.'); return stack.length ? stack[stack.length - 1] : undefined; } else if (opcode === 0x10) { var importedIndex = __folio_wasm_read_u32(body, cursor); if (!invokeFunction && (!importedFunctions || typeof importedFunctions[importedIndex] !== 'function')) throw new WebAssembly.CompileError('WebAssembly function import is unavailable.'); var importedArguments = stack.splice(0, stack.length); stack.push(invokeFunction ? invokeFunction(importedIndex, importedArguments) : importedFunctions[importedIndex].apply(undefined, importedArguments)); } else if (opcode === 0x11) { var typeIndex = __folio_wasm_read_u32(body, cursor); var tableIndex = __folio_wasm_read_u32(body, cursor); var elementIndex = stack.pop() || 0; var table = functionTables && functionTables[tableIndex]; if (!table) throw new WebAssembly.RuntimeError('WebAssembly table is unavailable.'); var target = table.get(elementIndex); if (typeof target !== 'function') throw new WebAssembly.RuntimeError('WebAssembly indirect call target is null.'); var indirectArguments = stack.splice(0, stack.length); stack.push(target.apply(undefined, indirectArguments)); } else if (opcode === 0xfc) { var bulkOpcode = __folio_wasm_read_u32(body, cursor); var bulkMemory = instanceExports && instanceExports.mem; if (!bulkMemory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); var bulkBytes = new Uint8Array(bulkMemory.buffer); if (bulkOpcode === 10) { __folio_wasm_read_u32(body, cursor); __folio_wasm_read_u32(body, cursor); var copyLength = stack.pop() || 0; var copySource = stack.pop() || 0; var copyTarget = stack.pop() || 0; if (copyTarget < 0 || copySource < 0 || copyLength < 0 || copyTarget + copyLength > bulkBytes.length || copySource + copyLength > bulkBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); bulkBytes.copyWithin(copyTarget, copySource, copySource + copyLength); } else if (bulkOpcode === 11) { __folio_wasm_read_u32(body, cursor); var fillLength = stack.pop() || 0; var fillValue = stack.pop() || 0; var fillTarget = stack.pop() || 0; if (fillTarget < 0 || fillLength < 0 || fillTarget + fillLength > bulkBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); bulkBytes.fill(fillValue & 255, fillTarget, fillTarget + fillLength); } else throw new WebAssembly.CompileError('Unsupported WebAssembly bulk memory instruction.'); } else if (opcode === 0x3f || opcode === 0x40) { __folio_wasm_read_u32(body, cursor); var growMemory = instanceExports && instanceExports.mem; if (!growMemory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); if (opcode === 0x3f) stack.push((growMemory.buffer.byteLength / 65536) | 0); else { var growDelta = stack.pop() || 0; var previousPages = (growMemory.buffer.byteLength / 65536) | 0; growMemory.grow(growDelta); stack.push(previousPages); } } else if (opcode === 0x2c || opcode === 0x2d || opcode === 0x2e || opcode === 0x2f || opcode === 0x3a || opcode === 0x3b) { __folio_wasm_read_u32(body, cursor); var narrowOffset = __folio_wasm_read_u32(body, cursor); var narrowMemory = instanceExports && instanceExports.mem; if (!narrowMemory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); var narrowBytes = new Uint8Array(narrowMemory.buffer); var width = opcode === 0x2c || opcode === 0x2d || opcode === 0x3a ? 1 : 2; if (opcode === 0x3a || opcode === 0x3b) { var narrowValue = stack.pop() || 0; var narrowAddress = (stack.pop() || 0) + narrowOffset; if (narrowAddress < 0 || narrowAddress + width > narrowBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); narrowBytes[narrowAddress] = narrowValue & 255; if (width === 2) narrowBytes[narrowAddress + 1] = (narrowValue >>> 8) & 255; } else { var narrowAddress = (stack.pop() || 0) + narrowOffset; if (narrowAddress < 0 || narrowAddress + width > narrowBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); var narrowValue = width === 1 ? narrowBytes[narrowAddress] : narrowBytes[narrowAddress] | (narrowBytes[narrowAddress + 1] << 8); if (opcode === 0x2c && (narrowValue & 0x80)) narrowValue |= 0xFFFFFF00; if (opcode === 0x2e && (narrowValue & 0x8000)) narrowValue |= 0xFFFF0000; stack.push(narrowValue | 0); } } else if (opcode === 0x28 || opcode === 0x36) { var alignment = __folio_wasm_read_u32(body, cursor); var offset = __folio_wasm_read_u32(body, cursor); var memory = instanceExports && instanceExports.mem; if (!memory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); var bytes = new Uint8Array(memory.buffer); if (opcode === 0x28) { var address = (stack.pop() || 0) + offset; if (address < 0 || address + 4 > bytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); stack.push((bytes[address] | (bytes[address + 1] << 8) | (bytes[address + 2] << 16) | (bytes[address + 3] << 24)) | 0); } else { var value = stack.pop() || 0; var address = (stack.pop() || 0) + offset; if (address < 0 || address + 4 > bytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); bytes[address] = value & 255; bytes[address + 1] = (value >>> 8) & 255; bytes[address + 2] = (value >>> 16) & 255; bytes[address + 3] = (value >>> 24) & 255; } } else if (opcode >= 0x6a && opcode <= 0x76) { var right = stack.pop() || 0; var left = stack.pop() || 0; if ((opcode === 0x6d || opcode === 0x6e || opcode === 0x6f || opcode === 0x70) && right === 0) throw new WebAssembly.RuntimeError('WebAssembly integer division by zero.'); if (opcode === 0x6d && left === -2147483648 && right === -1) throw new WebAssembly.RuntimeError('WebAssembly integer division overflow.'); var arithmetic = opcode === 0x6a ? left + right : opcode === 0x6b ? left - right : opcode === 0x6c ? left * right : opcode === 0x6d ? intrinsic_Math_trunc(left / right) : opcode === 0x6e ? intrinsic_Math_floor((left >>> 0) / (right >>> 0)) : opcode === 0x6f ? left % right : opcode === 0x70 ? (left >>> 0) % (right >>> 0) : opcode === 0x71 ? left & right : opcode === 0x72 ? left | right : opcode === 0x73 ? left ^ right : opcode === 0x74 ? left << (right & 31) : opcode === 0x75 ? left >> (right & 31) : left >>> (right & 31); stack.push(arithmetic | 0); } else if (opcode === 0x0b) return stack.length ? stack[stack.length - 1] : undefined; else throw new WebAssembly.CompileError('Unsupported WebAssembly instruction.'); }
                return undefined;
            }
            function __folio_wasm_metadata(bytes, wanted) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) {
                    var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = intrinsic_Math_min(bytes.length, cursor.i + size);
                    if (section !== wanted) { cursor.i = end; continue; }
                    var count = __folio_wasm_read_u32(bytes, cursor);
                    for (var n = 0; n < count && cursor.i < end; n++) {
                        var moduleName = ''; if (wanted === 2) moduleName = __folio_wasm_read_name(bytes, cursor);
                        var name = __folio_wasm_read_name(bytes, cursor); var kind = bytes[cursor.i++]; var index = __folio_wasm_read_u32(bytes, cursor);
                        result.push(wanted === 2 ? { module: moduleName, name: name, kind: kind, index: index } : { name: name, kind: kind, index: index });
                        if (wanted === 2) { if (kind === 1) { cursor.i++; __folio_wasm_skip_limits(bytes, cursor); } else if (kind === 2) __folio_wasm_skip_limits(bytes, cursor); else if (kind === 3) { cursor.i += 2; } else if (kind === 4) __folio_wasm_read_u32(bytes, cursor); }
                    }
                    cursor.i = end;
                }
                return result;
            }
            function __folio_wasm_custom_sections(bytes, wantedName) {
                var result = []; var cursor = { i: 8 };
                while (cursor.i < bytes.length) {
                    var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var start = cursor.i; var end = intrinsic_Math_min(bytes.length, start + size);
                    if (section === 0) { var nameLength = __folio_wasm_read_u32(bytes, cursor); var name = ''; for (var n = 0; n < nameLength && cursor.i < end; n++) name += intrinsic_String_fromCharCode(bytes[cursor.i++]); if (name === String(wantedName)) result.push(bytes.slice(cursor.i, end)); }
                    cursor.i = end;
                }
                return result;
            }
            var __folio_wasm_compile_cache = new Map(), __folio_wasm_compile_cache_bytes = 0;
            function __folio_wasm_validate_bytes(bytes) {
                if (!bytes || bytes.length < 8 || bytes[0] !== 0 || bytes[1] !== 97 || bytes[2] !== 115 || bytes[3] !== 109 || bytes[4] !== 1 || bytes[5] !== 0 || bytes[6] !== 0 || bytes[7] !== 0) return false;
                var cursor = 8, lastSection = 0;
                while (cursor < bytes.length) {
                    var section = bytes[cursor++]; if (section > 12) return false;
                    if (section !== 0 && section <= lastSection) return false;
                    var size = 0, shift = 0, read = 0, part;
                    do { if (cursor >= bytes.length || read++ === 5) return false; part = bytes[cursor++]; size += (part & 127) * intrinsic_Math_pow(2, shift); shift += 7; } while (part & 128);
                    if (!intrinsic_Number_isSafeInteger(size) || size > bytes.length - cursor) return false;
                    cursor += size; if (section !== 0) lastSection = section;
                }
                return cursor === bytes.length;
            }
            var WebAssembly = {
                CompileError: function(message) { this.name = 'CompileError'; this.message = String(message || ''); },
                RuntimeError: function(message) { this.name = 'RuntimeError'; this.message = String(message || ''); },
                validate: function(bytes) {
                    if (bytes instanceof ArrayBuffer) bytes = new Uint8Array(bytes);
                    return enabled() && __folio_wasm_validate_bytes(bytes);
                },
                compile: function(bytes) {
                    if (!enabled()) return rejectPromise(denied());
                    if (bytes instanceof ArrayBuffer) bytes = new Uint8Array(bytes);
                    if (!WebAssembly.validate(bytes)) return rejectPromise(new WebAssembly.CompileError('Invalid WebAssembly module'));
                    var key = arrayFrom(bytes).join(','); var cached = __folio_wasm_compile_cache.get(key); if (cached) { __folio_wasm_compile_cache.delete(key); __folio_wasm_compile_cache.set(key, cached); return resolvePromise(cached.module); }
                    var module; try { module = new WebAssembly.Module(bytes); } catch (error) { return rejectPromise(error); }
                    var size = bytes.byteLength || bytes.length || 0; var cap = Number(__folio_wasm_max_bytes);
                    if (size <= cap) { while (__folio_wasm_compile_cache_bytes + size > cap && __folio_wasm_compile_cache.size) { var oldestKey = __folio_wasm_compile_cache.keys().next().value; var oldest = __folio_wasm_compile_cache.get(oldestKey); __folio_wasm_compile_cache.delete(oldestKey); __folio_wasm_compile_cache_bytes -= oldest ? oldest.size : 0; } if (__folio_wasm_compile_cache_bytes + size <= cap) { __folio_wasm_compile_cache.set(key, { module: module, size: size }); __folio_wasm_compile_cache_bytes += size; } }
                    return resolvePromise(module);
                },
                compileStreaming: function(response) {
                    if (!enabled()) return rejectPromise(denied());
                    if (response && typeof response.then === 'function') return response.then(function(resolved) { return WebAssembly.compileStreaming(resolved); });
                    if (!response || typeof response.arrayBuffer !== 'function') return rejectPromise(new TypeError('WebAssembly.compileStreaming requires a Response.'));
                    if (response.ok === false || response.status !== undefined && (response.status < 200 || response.status > 299)) return rejectPromise(new TypeError('WebAssembly.compileStreaming requires a successful response.'));
                    var contentType = response.headers && response.headers.get ? response.headers.get('content-type') : null;
                    if (!contentType || contentType.toLowerCase().split(';')[0].trim() !== 'application/wasm') return rejectPromise(new TypeError('WebAssembly.compileStreaming requires an application/wasm response.'));
                    return response.arrayBuffer().then(function(buffer) { return WebAssembly.compile(new Uint8Array(buffer)); });
                },
                instantiate: function(bytes, imports) {
                    if (!enabled()) return rejectPromise(denied());
                    if (bytes instanceof WebAssembly.Module) return resolvePromise({ module: bytes, instance: new WebAssembly.Instance(bytes, imports) });
                    return WebAssembly.compile(bytes).then(function(module) { return { module: module, instance: new WebAssembly.Instance(module, imports) }; });
                },
                instantiateStreaming: function(response, imports) {
                    if (!enabled()) return rejectPromise(denied());
                    if (response && typeof response.then === 'function') return response.then(function(resolved) { return WebAssembly.instantiateStreaming(resolved, imports); });
                    return WebAssembly.compileStreaming(response).then(function(module) { return { module: module, instance: new WebAssembly.Instance(module, imports) }; });
                },
                Memory: function(options) {
                    if (!enabled()) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    options = options || {}; this._pages = Number(options.initial) || 0;
                    this._maximum = options.maximum === undefined ? __folio_wasm_max_pages : Number(options.maximum);
                    if (!intrinsic_Number_isInteger(this._pages) || !intrinsic_Number_isInteger(this._maximum) || this._pages < 0 || this._maximum < 0 || this._pages > this._maximum || this._pages > __folio_wasm_max_pages) throw new RangeError('WebAssembly memory limit exceeded.');
                    requireAllowed();
                    this.buffer = new ArrayBuffer(this._pages * 65536);
                },
                Module: function(bytes) {
                    if (!enabled()) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    if (bytes instanceof ArrayBuffer) bytes = new Uint8Array(bytes);
                    var moduleSize = bytes && (bytes.byteLength || bytes.length) || 0;
                    if (moduleSize > Number(__folio_wasm_max_bytes)) throw new RangeError('WebAssembly module size limit exceeded.');
                    if (!WebAssembly.validate(bytes)) throw new WebAssembly.CompileError('Invalid WebAssembly module');
                    this.bytes = bytes; this._exports = __folio_wasm_metadata(bytes, 7); this._imports = __folio_wasm_metadata(bytes, 2); this._functions = __folio_wasm_functions(bytes); this._globals = __folio_wasm_globals(bytes); this._memories = __folio_wasm_memories(bytes); this._tables = __folio_wasm_tables(bytes); this._functionImportCount = this._imports.filter(function(entry) { return entry.kind === 0; }).length;
                },
                Instance: function(module, imports) {
                    requireAllowed();
                    if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Instance requires a Module.');
                    var importedFunctions = []; var importedMemories = []; var importedTables = []; var importedGlobals = []; module._imports.forEach(function(entry) { if (!imports || !imports[entry.module] || imports[entry.module][entry.name] === undefined) throw new TypeError('Missing WebAssembly import: ' + entry.module + '.' + entry.name); if (entry.kind === 0) { if (typeof imports[entry.module][entry.name] !== 'function') throw new TypeError('WebAssembly function import is not callable.'); importedFunctions.push(imports[entry.module][entry.name]); } else if (entry.kind === 1) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Table)) throw new TypeError('WebAssembly table import is not a Table.'); importedTables.push(imports[entry.module][entry.name]); } else if (entry.kind === 2) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Memory)) throw new TypeError('WebAssembly memory import is not a Memory.'); importedMemories.push(imports[entry.module][entry.name]); } else if (entry.kind === 3) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Global)) throw new TypeError('WebAssembly global import is not a Global.'); importedGlobals.push(imports[entry.module][entry.name]); } });
                    requireAllowed(); this.module = module; this.exports = {}; var functionTables = importedTables.slice(); var functionGlobals = importedGlobals.slice(); var invokeFunction = function(index, args) { requireAllowed(); if (index < module._functionImportCount) { var result = importedFunctions[index].apply(undefined, args); requireAllowed(); return result; } var localBody = module._functions[index - module._functionImportCount]; if (!localBody) throw new WebAssembly.CompileError('WebAssembly function body is unavailable.'); return __folio_wasm_call(localBody, args, this, importedFunctions, invokeFunction, functionTables, functionGlobals); };
                    module._exports.forEach(function(entry) { if (entry.kind === 0) { var functionIndex = entry.index - module._functionImportCount; this.exports[entry.name] = entry.index < module._functionImportCount ? function() { requireAllowed(); var result = importedFunctions[entry.index].apply(undefined, arguments); requireAllowed(); return result; } : functionIndex >= 0 && module._functions[functionIndex] ? function() { return __folio_wasm_call(module._functions[functionIndex], arguments, this, importedFunctions, invokeFunction, functionTables, functionGlobals); } : function() { throw new WebAssembly.CompileError('WebAssembly function body is unavailable.'); }; } else if (entry.kind === 1 && importedTables[entry.index]) this.exports[entry.name] = importedTables[entry.index]; else if (entry.kind === 1 && module._tables[entry.index - importedTables.length]) { var table = module._tables[entry.index - importedTables.length]; var localTable = new WebAssembly.Table({ initial: table.initial, maximum: table.maximum }); functionTables.push(localTable); this.exports[entry.name] = localTable; } else if (entry.kind === 2 && importedMemories[entry.index]) this.exports[entry.name] = importedMemories[entry.index]; else if (entry.kind === 2 && module._memories[entry.index - importedMemories.length]) { var memory = module._memories[entry.index - importedMemories.length]; this.exports[entry.name] = new WebAssembly.Memory({ initial: memory.initial, maximum: memory.maximum }); } else if (entry.kind === 3 && importedGlobals[entry.index]) this.exports[entry.name] = importedGlobals[entry.index]; else if (entry.kind === 3 && module._globals[entry.index - importedGlobals.length] !== undefined) { var localGlobal = new WebAssembly.Global({ value: 'i32', mutable: false }, module._globals[entry.index - importedGlobals.length]); functionGlobals.push(localGlobal); this.exports[entry.name] = localGlobal; } }, this);
                },
                Table: function(options) {
                    if (!enabled()) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    options = options || {}; this._maximum = options.maximum === undefined ? __folio_wasm_max_table_elements : Number(options.maximum); this._entries = [];
                    var initial = Number(options.initial) || 0; if (!intrinsic_Number_isInteger(initial) || !intrinsic_Number_isInteger(this._maximum) || initial < 0 || this._maximum < 0 || this._maximum > __folio_wasm_max_table_elements || initial > this._maximum) throw new RangeError('WebAssembly table limit exceeded.');
                    requireAllowed(); for (var i = 0; i < initial; i++) this._entries.push(null);
                },
                Global: function(options, value) {
                    if (!enabled()) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    if (Number(__folio_wasm_global_count) >= Number(__folio_wasm_max_globals)) throw new RangeError('WebAssembly global limit exceeded.');
                    options = options || {}; this.type = String(options.value || 'i32'); if (['i32', 'i64', 'f32', 'f64', 'v128', 'funcref', 'externref'].indexOf(this.type) < 0) throw new TypeError('Invalid WebAssembly global value type.'); this.mutable = !!options.mutable; this._value = value === undefined ? 0 : value;
                    if (this.type === 'i32' || this.type === 'i64') this._value = Number(this._value) | 0; else if (this.type === 'f32' || this.type === 'f64') this._value = Number(this._value);
                    requireAllowed(); intrinsic_Object_defineProperty(this, 'value', { get: function() { return this._value; }, set: function(next) { requireAllowed(); if (!this.mutable) throw new TypeError('Immutable WebAssembly.Global.'); this._value = (this.type === 'i32' || this.type === 'i64') ? Number(next) | 0 : (this.type === 'f32' || this.type === 'f64') ? Number(next) : next; } });
                    __folio_wasm_global_count++;
                }
            };
            WebAssembly.Memory.prototype.grow = function(delta) {
                requireAllowed();
                delta = Number(delta); var previous = this._pages; var next = previous + delta;
                if (!intrinsic_Number_isInteger(delta) || delta < 0 || next > this._maximum || next > __folio_wasm_max_pages) throw new RangeError('WebAssembly memory limit exceeded.');
                requireAllowed(); this._pages = next; this.buffer = new ArrayBuffer(next * 65536); return previous;
            };
            intrinsic_Object_defineProperty(WebAssembly.Table.prototype, 'length', { get: function() { return this._entries.length; } });
            WebAssembly.Table.prototype.grow = function(delta, value) {
                requireAllowed();
                delta = Number(delta); var previous = this._entries.length; var next = previous + delta;
                if (!intrinsic_Number_isInteger(delta) || delta < 0 || next > this._maximum || next > __folio_wasm_max_table_elements) throw new RangeError('WebAssembly table limit exceeded.');
                requireAllowed(); for (var i = previous; i < next; i++) this._entries.push(value === undefined ? null : value); return previous;
            };
            WebAssembly.Table.prototype.get = function(index) { requireAllowed(); index = Number(index); if (index < 0 || index >= this._entries.length) throw new RangeError('WebAssembly table index out of range.'); return this._entries[index]; };
            WebAssembly.Table.prototype.set = function(index, value) { requireAllowed(); index = Number(index); if (index < 0 || index >= this._entries.length) throw new RangeError('WebAssembly table index out of range.'); this._entries[index] = value; };
            WebAssembly.Global.prototype.valueOf = function() { return this.value; };
            WebAssembly.Module.exports = function(module) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.exports requires a Module.');
                return module._exports.slice();
            };
            WebAssembly.Module.imports = function(module) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.imports requires a Module.');
                return module._imports.slice();
            };
            WebAssembly.Module.customSections = function(module, name) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.customSections requires a Module.');
                return __folio_wasm_custom_sections(module.bytes, name);
            };
            // Keep the implementation's constructor table private when the page edits its public facade.
            const facade = {};
            for (const name of intrinsic_Object_keys(WebAssembly)) facade[name] = WebAssembly[name];
            globalThis.WebAssembly = facade;
        })(__folio_wasm_host);
        delete globalThis.__folio_wasm_host;
        """;
}
