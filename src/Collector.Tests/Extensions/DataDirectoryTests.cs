using Collector.Extensions;
using FluentAssertions;
using Xunit;

namespace Collector.Tests.Extensions;

public class DataDirectoryTests
{
    private static readonly string BaseDir =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sc-tracker", "bin", "collector", "net10.0"));

    [Fact]
    public void Resolve_WithoutConfiguredValue_UsesDataFolderTwoLevelsAboveBin()
    {
        var expected = Path.GetFullPath(Path.Combine(BaseDir, "..", "..", "data"));

        DataDirectory.Resolve(null, BaseDir).Should().Be(expected);
        DataDirectory.Resolve("  ", BaseDir).Should().Be(expected);
    }

    [Fact]
    public void Resolve_WithRelativeValue_ResolvesAgainstBaseDirectory()
    {
        var expected = Path.GetFullPath(Path.Combine(BaseDir, "..", "..", "data"));

        DataDirectory.Resolve("../../data", BaseDir).Should().Be(expected);
    }

    [Fact]
    public void Resolve_WithAbsoluteValue_KeepsIt()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tracker-data"));

        DataDirectory.Resolve(absolute, BaseDir).Should().Be(absolute);
    }
}
