using System.Text.RegularExpressions;
using Lumen.Domain.Orders;

namespace Lumen.UnitTests.Orders;

public sealed class OrderNumberGeneratorTests
{
    private const string Format = "^LG-[0-9A-HJKMNP-TV-Z]{8}$";

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_order_numbers_are_LG_plus_8_crockford_base32_characters()
    {
        var generator = new OrderNumberGenerator();

        var numbers = Enumerable.Range(0, 1000).Select(_ => generator.Next()).ToList();

        numbers.Should().AllSatisfy(n => n.Should().MatchRegex(Format));
        numbers.Should().AllSatisfy(n => OrderNumberGenerator.IsWellFormed(n).Should().BeTrue());
        numbers.Should().OnlyHaveUniqueItems("8 random characters give about 10^12 values");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_the_alphabet_is_crockford_base32_without_I_L_O_or_U()
    {
        OrderNumberGenerator.Alphabet.Should().Be("0123456789ABCDEFGHJKMNPQRSTVWXYZ");
        OrderNumberGenerator.Alphabet.Should().HaveLength(32);
        OrderNumberGenerator.Alphabet.Distinct().Should().HaveCount(32);
        OrderNumberGenerator.Alphabet.Should().NotContainAny("I", "L", "O", "U");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_the_random_source_can_be_injected()
    {
        var next = 0;
        var sequential = new OrderNumberGenerator(max => next++ % max);
        var highest = new OrderNumberGenerator(max => max - 1);

        sequential.Next().Should().Be("LG-01234567");
        sequential.Next().Should().Be("LG-89ABCDEF");
        highest.Next().Should().Be("LG-ZZZZZZZZ");
    }

    [Fact]
    [Trait("BR", "BR-ORD-01")]
    public void BR_ORD_01_the_generator_asks_for_indexes_below_the_alphabet_size()
    {
        var requested = new List<int>();
        new OrderNumberGenerator(max => { requested.Add(max); return 0; }).Next();

        requested.Should().HaveCount(8).And.OnlyContain(max => max == 32);
    }

    [Theory]
    [Trait("BR", "BR-ORD-01")]
    [InlineData("LG-7K3M9QX2", true)]
    [InlineData("LG-00000000", true)]
    [InlineData("LG-7K3M9QXI", false)] // I is not in the alphabet
    [InlineData("LG-7K3M9QXL", false)]
    [InlineData("LG-7K3M9QXO", false)]
    [InlineData("LG-7K3M9QXU", false)]
    [InlineData("lg-7k3m9qx2", false)]
    [InlineData("LG-7K3M9QX", false)]
    [InlineData("LG-7K3M9QX22", false)]
    [InlineData("XX-7K3M9QX2", false)]
    [InlineData("LG7K3M9QX2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BR_ORD_01_well_formed_order_numbers_are_recognised(string? value, bool expected) =>
        OrderNumberGenerator.IsWellFormed(value).Should().Be(expected);

    [Fact]
    public void The_example_in_the_business_rules_matches_the_format() =>
        Regex.IsMatch("LG-7K3M9QX2", Format).Should().BeTrue();
}
