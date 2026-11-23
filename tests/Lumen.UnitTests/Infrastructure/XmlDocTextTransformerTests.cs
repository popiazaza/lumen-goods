using System.Text.Json.Nodes;
using Lumen.Api.Infrastructure.OpenApi;

namespace Lumen.UnitTests.Infrastructure;

public sealed class XmlDocTextTransformerTests
{
    // Stands in for the document's schemas: PlaceOrderRequest has `lines`, PaymentRetryMode has same_payment_only.
    private static string? Wire(string owner, string member) => (owner, member) switch
    {
        ("PlaceOrderRequest", "Lines") => "lines",
        ("PagedResult&lt;T&gt;", "Page") => "page",
        ("PaymentRetryMode", "SamePaymentOnly") => "same_payment_only",
        _ => null,
    };

    [Fact]
    public void A_member_cref_becomes_its_wire_name_without_the_csharp_signature()
    {
        XmlDocTextTransformer.Clean("goes back in IReadOnlyList&lt;PlaceOrderLineDto&gt;? PlaceOrderRequest.Lines unchanged.", Wire)
            .Should().Be("goes back in `lines` unchanged.");
        XmlDocTextTransformer.Clean("int PagedResult&lt;T&gt;.Page is 1-based", Wire).Should().Be("`page` is 1-based");
        XmlDocTextTransformer.Clean("(PaymentRetryMode.SamePaymentOnly: a card request", Wire)
            .Should().Be("(`same_payment_only`: a card request");
    }

    [Fact]
    public void A_member_outside_the_contract_keeps_its_owner_but_loses_its_signature()
    {
        XmlDocTextTransformer.Clean("at most int PlaceOrderRequest.MaxLines (50)", Wire).Should().Be("at most `PlaceOrderRequest.MaxLines` (50)");
        XmlDocTextTransformer.Clean("(DateTimeOffset? ProductMapping.LastChecked(Product product, PartnerCheck? partner)), which", Wire)
            .Should().Be("(`ProductMapping.LastChecked()`), which");
    }

    [Fact]
    public void Prose_with_a_full_stop_or_a_file_name_is_left_alone()
    {
        const string prose = "Harbor Supply Co. ships it. See Lumen.Api for more; e.g. this.";

        XmlDocTextTransformer.Clean(prose, Wire).Should().Be(prose);
        XmlDocTextTransformer.Clean("When PaymentRetryMode.SamePaymentOnly, retry", Wire).Should().Be("When `same_payment_only`, retry");
    }

    [Fact]
    public void Paragraph_indentation_is_removed_and_list_items_are_numbered_in_order()
    {
        const string generated = "    compares it (BR-CHK-01):\n    1. the cart's products;\n1. each line's price\n    (up or down);\n1. the total.";

        XmlDocTextTransformer.Clean(generated, Wire).Should().Be(
            "compares it (BR-CHK-01):\n1. the cart's products;\n2. each line's price\n(up or down);\n3. the total.");
    }

    [Fact]
    public void A_fenced_example_is_parsed_when_it_is_json_and_dropped_when_it_is_not()
    {
        XmlDocTextTransformer.CleanExample(JsonValue.Create("```{ \"quantity\": 3 }```"))!.ToJsonString().Should().Be("{\"quantity\":3}");
        XmlDocTextTransformer.CleanExample(JsonValue.Create("```{ \"productId\": \"0193a0f1-...\", \"quantity\": 2, }```"))
            .Should().BeNull();
        XmlDocTextTransformer.CleanExample(JsonNode.Parse("{\"code\":\"123456\"}"))!.ToJsonString().Should().Be("{\"code\":\"123456\"}");
    }
}
