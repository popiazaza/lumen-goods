using Lumen.Domain;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;

namespace Lumen.UnitTests;

/// <summary>Builds "every pair of statuses" theory data from an expected list written out in the test, not read from the code.</summary>
internal static class TransitionCases
{
    public static TheoryData<TState, TState, bool> AllPairs<TState>(params (TState From, TState To)[] legal)
        where TState : struct, Enum
    {
        var data = new TheoryData<TState, TState, bool>();
        foreach (var from in Enum.GetValues<TState>())
        {
            foreach (var to in Enum.GetValues<TState>())
            {
                data.Add(from, to, legal.Contains((from, to)));
            }
        }

        return data;
    }

    public static void AssertMove<TState>(TransitionTable<TState> table, TState from, TState to, bool legal)
        where TState : struct, Enum
    {
        table.CanMove(from, to).Should().Be(legal);
        if (legal)
        {
            table.Move(from, to).Should().Be(to);
        }
        else
        {
            FluentActions.Invoking(() => table.Move(from, to))
                .Should().Throw<InvalidOperationException>()
                .WithMessage($"{typeof(TState).Name} cannot move from {from} to {to}.");
        }
    }
}

public sealed class TransitionTableTests
{
    private enum Light
    {
        Red,
        Green,
        Amber,
        Off,
    }

    private static readonly TransitionTable<Light> Lights = new(Light.Red,
    [
        new(Light.Red, Light.Green, "go"),
        new(Light.Green, Light.Amber, "slow"),
        new(Light.Amber, Light.Red, "stop"),
        new(Light.Red, Light.Off, "night"),
    ]);

    [Fact]
    public void Move_returns_the_new_status_for_a_listed_change() =>
        Lights.Move(Light.Red, Light.Green).Should().Be(Light.Green);

    [Fact]
    public void Move_throws_a_clear_error_for_a_change_not_listed() =>
        FluentActions.Invoking(() => Lights.Move(Light.Green, Light.Red))
            .Should().Throw<InvalidOperationException>().WithMessage("Light cannot move from Green to Red.");

    [Fact]
    public void Move_to_the_same_status_throws_because_a_deliberate_change_must_change_something() =>
        FluentActions.Invoking(() => Lights.Move(Light.Red, Light.Red))
            .Should().Throw<InvalidOperationException>().WithMessage("Light cannot move from Red to Red.");

    [Fact]
    public void MoveIfChanged_is_a_no_op_for_the_same_status() =>
        Lights.MoveIfChanged(Light.Amber, Light.Amber).Should().Be(Light.Amber);

    [Fact]
    public void MoveIfChanged_applies_a_listed_change_and_rejects_any_other()
    {
        Lights.MoveIfChanged(Light.Red, Light.Green).Should().Be(Light.Green);
        FluentActions.Invoking(() => Lights.MoveIfChanged(Light.Green, Light.Red))
            .Should().Throw<InvalidOperationException>().WithMessage("Light cannot move from Green to Red.");
    }

    [Fact]
    public void Next_states_and_final_states_come_from_the_table()
    {
        Lights.NextStates(Light.Red).Should().Equal(Light.Green, Light.Off);
        Lights.IsFinal(Light.Off).Should().BeTrue();
        Lights.IsFinal(Light.Amber).Should().BeFalse();
    }

    [Fact]
    public void A_duplicate_row_is_rejected_when_the_table_is_built() =>
        FluentActions.Invoking(() => new TransitionTable<Light>(Light.Red,
            [new(Light.Red, Light.Green, "a"), new(Light.Red, Light.Green, "b"), new(Light.Green, Light.Amber, "c"), new(Light.Amber, Light.Off, "d")]))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void A_self_move_is_rejected_when_the_table_is_built() =>
        FluentActions.Invoking(() => new TransitionTable<Light>(Light.Red,
            [new(Light.Red, Light.Red, "a"), new(Light.Red, Light.Green, "b"), new(Light.Green, Light.Amber, "c"), new(Light.Amber, Light.Off, "d")]))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void A_status_no_row_mentions_is_rejected_when_the_table_is_built() =>
        FluentActions.Invoking(() => new TransitionTable<Light>(Light.Red, [new(Light.Red, Light.Green, "go")]))
            .Should().Throw<ArgumentException>().WithMessage("*Amber, Off*");

    [Fact]
    public void The_mermaid_diagram_lists_the_start_every_row_and_the_final_states() =>
        Lights.ToMermaid().Should().Be(
            """
            stateDiagram-v2
                [*] --> Red
                Red --> Green: go
                Green --> Amber: slow
                Amber --> Red: stop
                Red --> Off: night
                Off --> [*]

            """.ReplaceLineEndings("\n"));

    [Fact]
    public void A_colon_inside_a_label_is_escaped_so_mermaid_can_render_it() =>
        new TransitionTable<Light>(Light.Red,
                [new(Light.Red, Light.Green, "status: go"), new(Light.Green, Light.Amber, "slow"), new(Light.Amber, Light.Off, "stop")])
            .ToMermaid().Should().Contain("    Red --> Green: status#58; go\n");

    public static TheoryData<string, string> Diagrams() => new()
    {
        { nameof(OrderTransitions.Order), OrderTransitions.Order.ToMermaid() },
        { nameof(OrderTransitions.Line), OrderTransitions.Line.ToMermaid() },
        { nameof(OrderTransitions.PartnerOrder), OrderTransitions.PartnerOrder.ToMermaid() },
        { nameof(PaymentTransitions.Payment), PaymentTransitions.Payment.ToMermaid() },
        { nameof(OrderTransitions.Refund), OrderTransitions.Refund.ToMermaid() },
    };

    [Theory]
    [MemberData(nameof(Diagrams))]
    public void The_orders_readme_shows_each_table_exactly_as_the_code_defines_it(string table, string mermaid)
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Lumen.Domain", "Orders", "README.md")).ReplaceLineEndings("\n");

        readme.Should().Contain($"```mermaid\n{mermaid}```", $"the {table} diagram must match the {table} transition table");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LumenGoods.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (LumenGoods.slnx).");
    }
}
