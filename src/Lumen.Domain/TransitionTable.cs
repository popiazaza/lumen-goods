using System.Text;

namespace Lumen.Domain;

/// <summary>One allowed status change and when it happens. <see cref="When"/> is also the label in the README diagrams.</summary>
public sealed record Transition<TState>(TState From, TState To, string When)
    where TState : struct, Enum;

/// <summary>
/// The allowed status changes of one entity, written out as a table so the whole lifecycle can be read in one place.
/// Every status change goes through <see cref="Move"/>, so an illegal change fails loudly instead of corrupting an order.
/// </summary>
public sealed class TransitionTable<TState>
    where TState : struct, Enum
{
    private readonly HashSet<(TState From, TState To)> _allowed;

    public TransitionTable(TState initial, IReadOnlyList<Transition<TState>> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        Initial = initial;
        Transitions = transitions;
        _allowed = [];
        foreach (var t in transitions)
        {
            if (EqualityComparer<TState>.Default.Equals(t.From, t.To) || !_allowed.Add((t.From, t.To)))
            {
                throw new ArgumentException($"{typeof(TState).Name}: {t.From} -> {t.To} is a self-move or listed twice.", nameof(transitions));
            }
        }

        // A status nobody can reach is almost always a forgotten row: fail when the table is built, not in production.
        var mentioned = transitions.SelectMany(t => new[] { t.From, t.To }).Append(initial).ToHashSet();
        var missing = Enum.GetValues<TState>().Where(s => !mentioned.Contains(s)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"{typeof(TState).Name}: no transition mentions {string.Join(", ", missing)}.", nameof(transitions));
        }
    }

    /// <summary>The status every new entity starts in.</summary>
    public TState Initial { get; }

    public IReadOnlyList<Transition<TState>> Transitions { get; }

    public bool CanMove(TState from, TState to) => _allowed.Contains((from, to));

    /// <summary>
    /// A deliberate change, such as claiming an order for a payment. Staying in the same status throws too: a second
    /// claim of an order already PaymentProcessing must fail, not pass silently (BR-PAY-01).
    /// </summary>
    /// <returns><paramref name="to"/>, so callers can write <c>entity.Status = table.Move(entity.Status, next)</c>.</returns>
    /// <exception cref="InvalidOperationException">The table does not allow this change.</exception>
    public TState Move(TState from, TState to) => CanMove(from, to)
        ? to
        : throw new InvalidOperationException($"{typeof(TState).Name} cannot move from {from} to {to}.");

    /// <summary>
    /// Applies a status that was worked out rather than requested (e.g. an order status derived from its lines):
    /// the same status is a no-op, any other change must be allowed by the table.
    /// </summary>
    /// <exception cref="InvalidOperationException">The status changed in a way the table does not allow.</exception>
    public TState MoveIfChanged(TState from, TState to) =>
        EqualityComparer<TState>.Default.Equals(from, to) ? from : Move(from, to);

    public IReadOnlyList<TState> NextStates(TState from) =>
        [.. Transitions.Where(t => EqualityComparer<TState>.Default.Equals(t.From, from)).Select(t => t.To)];

    /// <summary>A final status has no way out.</summary>
    public bool IsFinal(TState state) => NextStates(state).Count == 0;

    /// <summary>The table as a Mermaid <c>stateDiagram-v2</c>; a unit test keeps the README diagrams equal to this.</summary>
    public string ToMermaid()
    {
        var sb = new StringBuilder("stateDiagram-v2\n");
        sb.Append($"    [*] --> {Initial}\n");
        foreach (var t in Transitions)
        {
            // A second colon ends the label early and Mermaid rejects the diagram, so it is written as its entity code.
            sb.Append($"    {t.From} --> {t.To}: {t.When.Replace(":", "#58;", StringComparison.Ordinal)}\n");
        }

        foreach (var state in Enum.GetValues<TState>().Where(IsFinal))
        {
            sb.Append($"    {state} --> [*]\n");
        }

        return sb.ToString();
    }
}
