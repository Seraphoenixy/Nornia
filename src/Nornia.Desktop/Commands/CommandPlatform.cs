using System.Collections.Immutable;
using System.Windows.Input;

namespace Nornia.Desktop.Commands;

public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    string Glyph,
    IReadOnlyList<KeybindingDefinition> DefaultKeybindings,
    Func<CommandExecutionContext, Task> ExecuteAsync,
    Func<bool>? CanExecute = null,
    string? Enablement = null,
    IReadOnlyList<CommandMenuPlacement>? MenuPlacements = null);

public sealed record CommandExecutionContext(
    object? Arguments,
    IReadOnlyDictionary<string, object?> ContextKeys,
    CancellationToken CancellationToken);

public sealed record CommandMenuPlacement(string MenuId, string Group, int Order = 0, string? When = null);

public interface ICommandRegistry
{
    IReadOnlyCollection<CommandDescriptor> Commands { get; }
    void Register(CommandDescriptor descriptor);
    bool TryGet(string id, out CommandDescriptor descriptor);
    Task<bool> ExecuteAsync(string id, object? args = null, CancellationToken cancellationToken = default);
}

public sealed class CommandAccessor
{
    private ICommandRegistry? _registry;
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.Ordinal);
    public ICommand this[string id]
    {
        get
        {
            if (_commands.TryGetValue(id, out var command)) return command;
            return _commands[id] = new RegistryCommand(() => _registry, id);
        }
    }
    public void Attach(ICommandRegistry registry) => _registry = registry;

    private sealed class RegistryCommand(Func<ICommandRegistry?> registry, string id) : ICommand
    {
        public bool CanExecute(object? parameter) => registry()?.TryGet(id, out var descriptor) == true &&
            descriptor.CanExecute?.Invoke() != false;
        public async void Execute(object? parameter)
        {
            if (registry() is { } target) await target.ExecuteAsync(id, parameter);
        }
        public event EventHandler? CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
    }
}

public sealed class CommandRegistry(IContextKeyService? contextKeys = null) : ICommandRegistry
{
    private ImmutableDictionary<string, CommandDescriptor> _commands =
        ImmutableDictionary<string, CommandDescriptor>.Empty.WithComparers(StringComparer.Ordinal);

    public IReadOnlyCollection<CommandDescriptor> Commands => _commands.Values.OrderBy(item => item.Category)
        .ThenBy(item => item.Title).ToArray();

    public void Register(CommandDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        ImmutableInterlocked.AddOrUpdate(ref _commands, descriptor.Id, descriptor, (_, _) => descriptor);
    }

    public bool TryGet(string id, out CommandDescriptor descriptor) => _commands.TryGetValue(id, out descriptor!);

    public async Task<bool> ExecuteAsync(string id, object? args = null, CancellationToken cancellationToken = default)
    {
        if (!TryGet(id, out var descriptor) || descriptor.CanExecute?.Invoke() == false ||
            contextKeys?.Matches(descriptor.Enablement) == false) return false;
        await descriptor.ExecuteAsync(new(args, contextKeys?.Snapshot ?? ImmutableDictionary<string, object?>.Empty,
            cancellationToken));
        return true;
    }
}

public interface IContextKeyService
{
    event EventHandler? Changed;
    void Set(string key, object? value);
    object? Get(string key);
    bool Matches(string? expression);
    IReadOnlyDictionary<string, object?> Snapshot { get; }
}

public sealed class ContextKeyService : IContextKeyService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WhenExpression> _expressions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _invalidExpressions = new(StringComparer.Ordinal);
    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, object?> Snapshot
    {
        get { lock (_gate) return _values.ToImmutableDictionary(StringComparer.Ordinal); }
    }

    public void Set(string key, object? value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var current) && Equals(current, value)) return;
            _values[key] = value;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public object? Get(string key) { lock (_gate) return _values.GetValueOrDefault(key); }
    public bool Matches(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;
        try
        {
            WhenExpression compiled;
            lock (_gate)
            {
                if (_invalidExpressions.Contains(expression)) return false;
                if (!_expressions.TryGetValue(expression, out compiled!))
                    _expressions[expression] = compiled = WhenExpression.Parse(expression);
            }
            return compiled.Evaluate(Snapshot);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or
                                           System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            lock (_gate) _invalidExpressions.Add(expression);
            return false;
        }
    }
}

public sealed class WhenExpression
{
    private readonly Node _root;
    private WhenExpression(Node root) => _root = root;
    public static WhenExpression Parse(string text) => new(new Parser(text).Parse());
    public bool Evaluate(IReadOnlyDictionary<string, object?> context) => _root.Evaluate(context);

    private abstract record Node { public abstract bool Evaluate(IReadOnlyDictionary<string, object?> context); }
    private sealed record Truthy(string Key, bool Negated = false) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
        {
            var value = context.GetValueOrDefault(Key);
            var result = value switch { bool boolean => boolean, null => false, string text => text.Length > 0, _ => true };
            return Negated ? !result : result;
        }
    }
    private sealed record Compare(string Key, string Operator, string Value) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context)
        {
            var actual = Convert.ToString(context.GetValueOrDefault(Key), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            return Operator switch
            {
                "==" => string.Equals(actual, Value, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(actual, Value, StringComparison.OrdinalIgnoreCase),
                "=~" => System.Text.RegularExpressions.Regex.IsMatch(actual, Value,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)),
                _ => false,
            };
        }
    }
    private sealed record Binary(Node Left, string Operator, Node Right) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context) =>
            Operator == "&&" ? Left.Evaluate(context) && Right.Evaluate(context) : Left.Evaluate(context) || Right.Evaluate(context);
    }
    private sealed record Not(Node Inner) : Node
    {
        public override bool Evaluate(IReadOnlyDictionary<string, object?> context) => !Inner.Evaluate(context);
    }

    private sealed class Parser(string text)
    {
        private readonly Lexer _lexer = new(text);
        private Token _current;
        public Node Parse() { _current = _lexer.Next(); var node = ParseOr(); if (_current.Kind != TokenKind.End) throw Error(); return node; }
        private Node ParseOr() { var left = ParseAnd(); while (Accept(TokenKind.Or)) left = new Binary(left, "||", ParseAnd()); return left; }
        private Node ParseAnd() { var left = ParsePrimary(); while (Accept(TokenKind.And)) left = new Binary(left, "&&", ParsePrimary()); return left; }
        private Node ParsePrimary()
        {
            if (Accept(TokenKind.LeftParen)) { var nested = ParseOr(); Expect(TokenKind.RightParen); return nested; }
            var negated = Accept(TokenKind.Not);
            var key = Expect(TokenKind.Word).Text;
            if (_current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.Regex)
            {
                var op = _current.Text; _current = _lexer.Next();
                var value = Expect(TokenKind.Word).Text;
                if (op == "=~") _ = new System.Text.RegularExpressions.Regex(value.Trim('/'),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
                var compared = new Compare(key, op, value.Trim('/'));
                return negated ? new Not(compared) : compared;
            }
            return new Truthy(key, negated);
        }
        private bool Accept(TokenKind kind) { if (_current.Kind != kind) return false; _current = _lexer.Next(); return true; }
        private Token Expect(TokenKind kind) { if (_current.Kind != kind) throw Error(); var value = _current; _current = _lexer.Next(); return value; }
        private static FormatException Error() => new("无效的快捷键 when 表达式。");
    }

    private enum TokenKind { End, Word, Not, And, Or, Equal, NotEqual, Regex, LeftParen, RightParen }
    private readonly record struct Token(TokenKind Kind, string Text);
    private sealed class Lexer(string text)
    {
        private int _position;
        public Token Next()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
            if (_position >= text.Length) return new(TokenKind.End, string.Empty);
            foreach (var item in new[] { ("&&", TokenKind.And), ("||", TokenKind.Or), ("==", TokenKind.Equal),
                         ("!=", TokenKind.NotEqual), ("=~", TokenKind.Regex) })
                if (text.AsSpan(_position).StartsWith(item.Item1, StringComparison.Ordinal))
                { _position += item.Item1.Length; return new(item.Item2, item.Item1); }
            var character = text[_position++];
            if (character == '!') return new(TokenKind.Not, "!");
            if (character == '(') return new(TokenKind.LeftParen, "(");
            if (character == ')') return new(TokenKind.RightParen, ")");
            if (character is '\'' or '"')
            {
                var quote = character; var start = _position;
                while (_position < text.Length && text[_position] != quote) _position++;
                var value = text[start..Math.Min(_position, text.Length)];
                if (_position < text.Length) _position++;
                return new(TokenKind.Word, value);
            }
            var wordStart = _position - 1;
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not '(' and not ')' &&
                   !text.AsSpan(_position).StartsWith("&&") && !text.AsSpan(_position).StartsWith("||") &&
                   !text.AsSpan(_position).StartsWith("==") && !text.AsSpan(_position).StartsWith("!=") &&
                   !text.AsSpan(_position).StartsWith("=~")) _position++;
            return new(TokenKind.Word, text[wordStart.._position]);
        }
    }
}
