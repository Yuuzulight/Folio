namespace Folio.Scripting.Native;

public enum OpCode : byte
{
    Nop,
    PushConst,
    PushUndefined,
    PushNull,
    PushTrue,
    PushFalse,
    PushThis,
    Pop,
    Dup,
    LoadLocal,
    StoreLocal,
    LoadGlobal,
    StoreGlobal,
    LoadProperty,
    LoadMethod,
    StoreProperty,
    SetProperty,
    LoadElement,
    StoreElement,
    CreateObject,
    CreateArray,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Neg,
    Not,
    BitNot,
    Eq,
    NotEq,
    StrictEq,
    StrictNotEq,
    Lt,
    Lte,
    Gt,
    Gte,
    Jump,
    JumpIfFalse,
    JumpIfTrue,
    Call,
    New,
    Return,
    Throw,
    EnterTry,
    LeaveTry,
    CreateClosure,
}

public readonly struct Instruction(OpCode op, int operand = 0, string? stringOperand = null)
{
    public OpCode Op { get; } = op;
    public int Operand { get; } = operand;
    public string? StringOperand { get; } = stringOperand;

    public override string ToString() => StringOperand is not null ? $"{Op} \"{StringOperand}\"" : $"{Op} {Operand}";
}
