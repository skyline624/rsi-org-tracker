namespace Collector.Api.Dtos.Stats;

public class StatsOverviewDto
{
    public int TotalOrganizations { get; set; }
    public int TotalUsers { get; set; }
    public DateTime? LastCollectionAt { get; set; }
}

public class TimelinePointDto
{
    public string Date { get; set; } = null!;
    public int ChangeCount { get; set; }
}

public class ArchetypeStatsDto
{
    public string Archetype { get; set; } = null!;
    public int Count { get; set; }
}

public class MemberActivityDto
{
    public string Date { get; set; } = null!;
    public int Joins { get; set; }
    public int Leaves { get; set; }
    public int Total { get; set; }
}

public class OrganizationTopDto
{
    public string Sid { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int MembersCount { get; set; }
    public string? Archetype { get; set; }
}
