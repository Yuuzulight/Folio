namespace Folio.Scripting.Native;

internal abstract record JavaScriptExpression;
internal sealed record JavaScriptLiteralExpression(string Text, JavaScriptTokenKind Kind) : JavaScriptExpression
{
    private object? _cachedValue;
    private bool _hasCached;

    internal object? CachedValue => _hasCached ? _cachedValue : ComputeValue();

    private object? ComputeValue()
    {
        _cachedValue = Kind switch
        {
            JavaScriptTokenKind.String => Text,
            JavaScriptTokenKind.Number => ParseNumber(Text),
            _ => Text switch { "true" => true, "false" => false, _ => null }
        };
        _hasCached = true;
        return _cachedValue;
    }

    private static object ParseNumber(string rawText)
    {
        var text = rawText.Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.EndsWith('n')) text = text[..^1];
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 16), System.Globalization.CultureInfo.InvariantCulture);
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 2), System.Globalization.CultureInfo.InvariantCulture);
        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 8), System.Globalization.CultureInfo.InvariantCulture);
        return double.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }
}
internal sealed record JavaScriptTemplateExpression(string Text) : JavaScriptExpression;
internal sealed record JavaScriptRegexExpression(string Text) : JavaScriptExpression;
internal sealed record JavaScriptIdentifierExpression(string Name) : JavaScriptExpression;
internal sealed record JavaScriptAwaitExpression(JavaScriptExpression Operand) : JavaScriptExpression;
internal sealed record JavaScriptYieldExpression(JavaScriptExpression? Expression, bool Delegate = false) : JavaScriptExpression;
internal sealed record JavaScriptUnaryExpression(string Operator, JavaScriptExpression Operand) : JavaScriptExpression;
internal sealed record JavaScriptUpdateExpression(string Operator, JavaScriptExpression Operand, bool Prefix) : JavaScriptExpression;
internal enum JavaScriptBinaryOp : byte
{
    Other,
    Comma,
    LogicalAndAssign, LogicalOrAssign, NullishAssign,
    Assign, AddAssign, SubAssign, MulAssign, DivAssign, ModAssign,
    AndAssign, OrAssign, XorAssign, ShlAssign, ShrAssign, UshrAssign,
    LogicalAnd, LogicalOr, Nullish,
    InstanceOf, In,
    Add, Sub, Mul, Div, Mod,
    LessThan, GreaterThan, LessEqual, GreaterEqual,
    Equal, StrictEqual, NotEqual, StrictNotEqual,
    Power, ShiftLeft, ShiftRight, UnsignedShiftRight,
    BitwiseAnd, BitwiseOr, BitwiseXor
}

internal sealed record JavaScriptBinaryExpression(string Operator, JavaScriptExpression Left, JavaScriptExpression Right) : JavaScriptExpression
{
    internal JavaScriptBinaryOp Op { get; } = Classify(Operator);

    private static JavaScriptBinaryOp Classify(string op) => op switch
    {
        "," => JavaScriptBinaryOp.Comma,
        "&&=" => JavaScriptBinaryOp.LogicalAndAssign,
        "||=" => JavaScriptBinaryOp.LogicalOrAssign,
        "??=" => JavaScriptBinaryOp.NullishAssign,
        "=" => JavaScriptBinaryOp.Assign,
        "+=" => JavaScriptBinaryOp.AddAssign,
        "-=" => JavaScriptBinaryOp.SubAssign,
        "*=" => JavaScriptBinaryOp.MulAssign,
        "/=" => JavaScriptBinaryOp.DivAssign,
        "%=" => JavaScriptBinaryOp.ModAssign,
        "&=" => JavaScriptBinaryOp.AndAssign,
        "|=" => JavaScriptBinaryOp.OrAssign,
        "^=" => JavaScriptBinaryOp.XorAssign,
        "<<=" => JavaScriptBinaryOp.ShlAssign,
        ">>=" => JavaScriptBinaryOp.ShrAssign,
        ">>>=" => JavaScriptBinaryOp.UshrAssign,
        "&&" => JavaScriptBinaryOp.LogicalAnd,
        "||" => JavaScriptBinaryOp.LogicalOr,
        "??" => JavaScriptBinaryOp.Nullish,
        "instanceof" => JavaScriptBinaryOp.InstanceOf,
        "in" => JavaScriptBinaryOp.In,
        "+" => JavaScriptBinaryOp.Add,
        "-" => JavaScriptBinaryOp.Sub,
        "*" => JavaScriptBinaryOp.Mul,
        "/" => JavaScriptBinaryOp.Div,
        "%" => JavaScriptBinaryOp.Mod,
        "<" => JavaScriptBinaryOp.LessThan,
        ">" => JavaScriptBinaryOp.GreaterThan,
        "<=" => JavaScriptBinaryOp.LessEqual,
        ">=" => JavaScriptBinaryOp.GreaterEqual,
        "==" => JavaScriptBinaryOp.Equal,
        "===" => JavaScriptBinaryOp.StrictEqual,
        "!=" => JavaScriptBinaryOp.NotEqual,
        "!==" => JavaScriptBinaryOp.StrictNotEqual,
        "**" => JavaScriptBinaryOp.Power,
        "<<" => JavaScriptBinaryOp.ShiftLeft,
        ">>" => JavaScriptBinaryOp.ShiftRight,
        ">>>" => JavaScriptBinaryOp.UnsignedShiftRight,
        "&" => JavaScriptBinaryOp.BitwiseAnd,
        "|" => JavaScriptBinaryOp.BitwiseOr,
        "^" => JavaScriptBinaryOp.BitwiseXor,
        _ => JavaScriptBinaryOp.Other,
    };
}
internal sealed record JavaScriptConditionalExpression(JavaScriptExpression Condition, JavaScriptExpression WhenTrue, JavaScriptExpression WhenFalse) : JavaScriptExpression;
internal sealed record JavaScriptMemberExpression(JavaScriptExpression Target, string Name, bool Optional = false) : JavaScriptExpression;
internal sealed record JavaScriptCallExpression(JavaScriptExpression Callee, IReadOnlyList<JavaScriptExpression> Arguments) : JavaScriptExpression;
internal sealed record JavaScriptArrayExpression(IReadOnlyList<JavaScriptExpression> Elements) : JavaScriptExpression;
internal sealed record JavaScriptSpreadExpression(JavaScriptExpression Operand) : JavaScriptExpression;
internal sealed record JavaScriptIndexExpression(JavaScriptExpression Target, JavaScriptExpression Index) : JavaScriptExpression;
internal sealed record JavaScriptObjectExpression(IReadOnlyList<(string Name, JavaScriptExpression Value)> Properties) : JavaScriptExpression;
internal sealed record JavaScriptFunctionExpression(IReadOnlyList<string> Parameters, IReadOnlyList<JavaScriptStatement> Body,
    IReadOnlyList<JavaScriptExpression?>? DefaultValues = null, bool IsGenerator = false, string? Name = null, bool IsAsync = false, bool IsArrow = false) : JavaScriptExpression;
internal sealed record JavaScriptClassMethod(string Name, bool Getter, bool Setter, bool Static, JavaScriptFunctionExpression Function);
internal sealed record JavaScriptClassExpression(string? Name, string? Parent, IReadOnlyList<JavaScriptClassMethod> Methods) : JavaScriptExpression;

internal abstract record JavaScriptStatement;
internal sealed record JavaScriptExpressionStatement(JavaScriptExpression Expression) : JavaScriptStatement;
internal sealed record JavaScriptVariableStatement(string Name, JavaScriptExpression? Initializer) : JavaScriptStatement;
internal sealed record JavaScriptFunctionDeclarationStatement(string Name, JavaScriptFunctionExpression Function) : JavaScriptStatement;
internal sealed record JavaScriptClassDeclarationStatement(string Name, JavaScriptClassExpression Class) : JavaScriptStatement;
internal sealed record JavaScriptReturnStatement(JavaScriptExpression? Expression) : JavaScriptStatement;
internal sealed record JavaScriptYieldStatement(JavaScriptExpression? Expression, bool Delegate = false) : JavaScriptStatement;
internal sealed record JavaScriptBlockStatement(IReadOnlyList<JavaScriptStatement> Statements) : JavaScriptStatement;
internal sealed record JavaScriptIfStatement(JavaScriptExpression Condition, JavaScriptStatement Then, JavaScriptStatement? Else) : JavaScriptStatement;
internal sealed record JavaScriptWhileStatement(JavaScriptExpression Condition, JavaScriptStatement Body, string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptDoWhileStatement(JavaScriptStatement Body, JavaScriptExpression Condition, string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptBreakStatement(string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptContinueStatement(string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptForStatement(JavaScriptStatement? Initializer, JavaScriptExpression? Condition, JavaScriptExpression? Update, JavaScriptStatement Body, string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptForEachStatement(string Name, JavaScriptExpression Iterable, JavaScriptStatement Body, bool Keys, string? Label = null) : JavaScriptStatement;
internal sealed record JavaScriptLabeledStatement(string Label, JavaScriptStatement Body) : JavaScriptStatement;
internal sealed record JavaScriptWithStatement(JavaScriptExpression Scope, JavaScriptStatement Body) : JavaScriptStatement;
internal sealed record JavaScriptThrowStatement(JavaScriptExpression Expression) : JavaScriptStatement;
internal sealed record JavaScriptTryStatement(JavaScriptBlockStatement Body, string? CatchName, JavaScriptBlockStatement? CatchBody, JavaScriptBlockStatement? FinallyBody) : JavaScriptStatement;
internal sealed record JavaScriptSwitchCase(JavaScriptExpression? Test, IReadOnlyList<JavaScriptStatement> Statements);
internal sealed record JavaScriptSwitchStatement(JavaScriptExpression Expression, IReadOnlyList<JavaScriptSwitchCase> Cases) : JavaScriptStatement;
