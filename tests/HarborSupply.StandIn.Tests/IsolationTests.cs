namespace HarborSupply.StandIn.Tests;

/// <summary>
/// The spec: stand-ins "share no database or code with the Lumen Goods back end". Checking the compiled
/// assembly catches every route by which code could be shared (project or DLL references, linked source
/// files, references injected by Directory.Build.props).
/// </summary>
public sealed class IsolationTests
{
    private static readonly System.Reflection.Assembly StandIn = typeof(Program).Assembly;

    [Fact]
    public void Stand_in_assembly_references_no_Lumen_assembly() =>
        StandIn.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(name => name!.StartsWith("Lumen", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Stand_in_assembly_contains_no_Lumen_types() =>
        StandIn.GetTypes().Select(t => t.Namespace ?? "")
            .Should().NotContain(ns => ns.StartsWith("Lumen", StringComparison.Ordinal));
}
