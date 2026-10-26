namespace Folio.Scripting.Native;

public abstract record AstNode(int Line, int Column);

public abstract record StatementNode(int Line, int Column) : AstNode(Line, Column);

public abstract record ExpressionNode(int Line, int Column) : AstNode(Line, Column);

public sealed record ProgramNode(IReadOnlyList<StatementNode> Statements, int Line, int Column) : AstNode(Line, Column);

public sealed record BlockStatement(IReadOnlyList<StatementNode> Statements, int Line, int Column) : StatementNode(Line, Column);

public sealed record VarDeclaration(string Name, ExpressionNode? Initializer, int Line, int Column) : AstNode(Line, Column);

public sealed record VarStatement(string Kind, IReadOnlyList<VarDeclaration> Declarations, int Line, int Column) : StatementNode(Line, Column);

public sealed record ExpressionStatement(ExpressionNode Expression, int Line, int Column) : StatementNode(Line, Column);

public sealed record IfStatement(ExpressionNode Condition, StatementNode ThenBranch, StatementNode? ElseBranch, int Line, int Column) : StatementNode(Line, Column);

public sealed record WhileStatement(ExpressionNode Condition, StatementNode Body, int Line, int Column) : StatementNode(Line, Column);

public sealed record DoWhileStatement(StatementNode Body, ExpressionNode Condition, int Line, int Column) : StatementNode(Line, Column);

public sealed record ForStatement(AstNode? Init, ExpressionNode? Test, ExpressionNode? Update, StatementNode Body, int Line, int Column) : StatementNode(Line, Column);

public sealed record SwitchCase(ExpressionNode? Test, IReadOnlyList<StatementNode> Consequent, int Line, int Column) : AstNode(Line, Column);

public sealed record SwitchStatement(ExpressionNode Discriminant, IReadOnlyList<SwitchCase> Cases, int Line, int Column) : StatementNode(Line, Column);

public sealed record ReturnStatement(ExpressionNode? Argument, int Line, int Column) : StatementNode(Line, Column);

public sealed record BreakStatement(int Line, int Column) : StatementNode(Line, Column);

public sealed record ContinueStatement(int Line, int Column) : StatementNode(Line, Column);

public sealed record ThrowStatement(ExpressionNode Argument, int Line, int Column) : StatementNode(Line, Column);

public sealed record TryCatchStatement(BlockStatement TryBlock, string? CatchParam, BlockStatement? CatchBlock, BlockStatement? FinallyBlock, int Line, int Column) : StatementNode(Line, Column);

public sealed record FunctionDeclaration(string Name, IReadOnlyList<string> Parameters, BlockStatement Body, int Line, int Column) : StatementNode(Line, Column);

// Expressions
public sealed record LiteralExpression(object? Value, NativeTokenType Type, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record IdentifierExpression(string Name, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record ThisExpression(int Line, int Column) : ExpressionNode(Line, Column);

public sealed record UnaryExpression(string Operator, ExpressionNode Operand, bool IsPrefix, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record BinaryExpression(string Operator, ExpressionNode Left, ExpressionNode Right, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record AssignmentExpression(string Operator, ExpressionNode Target, ExpressionNode Value, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record TernaryExpression(ExpressionNode Condition, ExpressionNode TrueExpr, ExpressionNode FalseExpr, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record MemberExpression(ExpressionNode Object, ExpressionNode Property, bool Computed, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record CallExpression(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record NewExpression(ExpressionNode Callee, IReadOnlyList<ExpressionNode> Arguments, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record FunctionExpression(string? Name, IReadOnlyList<string> Parameters, BlockStatement Body, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record ArrowFunctionExpression(IReadOnlyList<string> Parameters, AstNode Body, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record ArrayLiteralExpression(IReadOnlyList<ExpressionNode?> Elements, int Line, int Column) : ExpressionNode(Line, Column);

public sealed record ObjectProperty(string Key, ExpressionNode Value, int Line, int Column) : AstNode(Line, Column);

public sealed record ObjectLiteralExpression(IReadOnlyList<ObjectProperty> Properties, int Line, int Column) : ExpressionNode(Line, Column);
