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
    public void InProduction_ADataDirectoryWithoutTrackerDb_IsRefused()
    {
        // A missing or mistyped COLLECTOR_DATA_DIR would start a new, empty database.
        var empty = Directory.CreateTempSubdirectory("sc-tracker-empty-").FullName;

        var act = () => DataDirectory.EnsureExistingDatabase(empty, production: true);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{DataDirectory.EnvironmentVariable}*");
    }

    [Fact]
    public void InProduction_ADataDirectoryWithTrackerDb_IsAccepted()
    {
        var dir = Directory.CreateTempSubdirectory("sc-tracker-data-").FullName;
        File.WriteAllBytes(Path.Combine(dir, "tracker.db"), []);

        var act = () => DataDirectory.EnsureExistingDatabase(dir, production: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void OutsideProduction_ANewDatabaseMayBeCreated()
    {
        var empty = Directory.CreateTempSubdirectory("sc-tracker-dev-").FullName;

        var act = () => DataDirectory.EnsureExistingDatabase(empty, production: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_WithAbsoluteValue_KeepsIt()
    {
        var absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tracker-data"));

        DataDirectory.Resolve(absolute, BaseDir).Should().Be(absolute);
    }
}
