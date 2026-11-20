using System.Xml.Linq;

namespace Lumen.UnitTests.Architecture;

/// <summary>
/// The spec: the partner stand-ins "share no database or code with the Lumen Goods back end, which talks to
/// them only over HTTP". These tests fail the build if a stand-in ever references Lumen code.
/// </summary>
public sealed class StandInIsolationTests
{
    private static readonly DirectoryInfo StandInsRoot = new(Path.Combine(FindRepoRoot(), "src", "StandIns"));

    public static TheoryData<string> StandInProjects() =>
        [.. StandInsRoot.GetFiles("*.csproj", SearchOption.AllDirectories).Select(f => f.FullName)];

    [Theory]
    [MemberData(nameof(StandInProjects))]
    public void Stand_in_projects_reference_no_other_project(string csprojPath)
    {
        var references = XDocument.Load(csprojPath).Descendants("ProjectReference").Select(r => (string?)r.Attribute("Include"));

        references.Should().BeEmpty("a partner stand-in is a separate company's system and shares no code with Lumen");
    }

    [Fact]
    public void Stand_in_source_never_uses_a_Lumen_namespace()
    {
        var offenders = StandInsRoot.GetFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f.FullName).Any(line => line.TrimStart().StartsWith("using Lumen", StringComparison.Ordinal)))
            .Select(f => f.Name);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Both_partner_stand_ins_exist() =>
        StandInProjects().Should().HaveCount(2);

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
