# SC-Organizations-Tracker Collector (.NET)

A C# .NET 8 implementation of the Star Citizen Organizations Tracker data collector.

## Features

- **4-Phase Collection Pipeline**:
  1. Organization Discovery (from RSI API)
  2. Organization Metadata Collection
  3. Member Collection with Pagination
  4. User Profile Enrichment (HTML Scraping)

- **Change Detection**:
  - Member joins/leaves detection with `citizen_id` priority
  - Rank and role changes
  - Organization metadata changes
  - User handle change tracking

- **Resilience**:
  - Polly-based retry with exponential backoff
  - Rate limiting between API requests
  - Graceful shutdown with Ctrl+C

## Architecture

```
collector-dotnet/
├── src/
│   ├── Collector/
│   │   ├── Models/          # EF Core entities
│   │   ├── Data/            # DbContext, configurations, repositories
│   │   ├── Services/        # Business logic (collectors, API client)
│   │   ├── Parsers/         # HTML parsing with HtmlAgilityPack
│   │   ├── Dtos/            # Data transfer objects
│   │   └── Exceptions/      # Custom exceptions
│   └── Collector.Tests/
└── data/
    └── tracker.db           # SQLite database
```

## Requirements

- .NET 8 SDK
- SQLite

## Getting Started

### Build

```bash
cd collector-dotnet
dotnet build
```

### Run (Continuous Loop)

```bash
dotnet run --project src/Collector
```

### Run Single Collection Cycle

```bash
dotnet run --project src/Collector -- --single-run
```

### Run Tests

```bash
dotnet test
```

## Configuration

Edit `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data/tracker.db"
  },
  "Collector": {
    "CycleInterval": "01:00:00",        // Time between cycles
    "ErrorDelay": "00:05:00",           // Delay after error
    "RateLimitDelaySeconds": 0.5,       // Delay between API requests
    "MaxRetries": 5,
    "BatchSize": 100,
    "MemberCollectionPageSize": 32,     // Max is 32
    "OrganizationPageSize": 12          // Max is ~12
  }
}
```

### Environment-Specific Configuration

- `appsettings.json` - Production settings
- `appsettings.Development.json` - Development settings (shorter intervals)

## Key Components

### RsiApiClient

HTTP client with:
- Automatic rate limiting
- Exponential backoff retry
- Throttling detection
- HTML response parsing

### ChangeDetector

Detects changes using `citizen_id` priority:
1. Match by `citizen_id` (permanent) - same person
2. Match by `handle` with same `citizen_id` - same person
3. Match by `handle` with different `citizen_id` - handle reused, different person

### CollectionOrchestrator

Sequential execution of phases:
```
Loop:
  1. Discover Organizations
  2. Collect Metadata
  3. Collect Members
  4. Enrich User Profiles
  Wait CycleInterval
```

## Database Schema

The SQLite database contains:

- `organizations` - Organization snapshots (time-series)
- `organization_members` - Member snapshots (time-series)
- `member_collection_log` - Presence log for change detection
- `users` - Enriched user profiles
- `user_handle_history` - Handle change tracking
- `change_events` - Detected changes
- `discovered_organizations` - Phase 1 discovery results
- `user_enrichment_queue` - Phase 4 queue

## Differences from Python Implementation

| Python | C# .NET |
|--------|---------|
| `asyncio`, `aiohttp` | `Task`, `HttpClient` |
| `BeautifulSoup` | `HtmlAgilityPack` |
| SQLAlchemy | Entity Framework Core |
| Manual retry loops | Polly (declarative) |
| Scripts | BackgroundService/Orchestrator |

## Logging

Logs are written to:
- Console (stdout)
- `data/logs/collector-{date}.log`

Log levels can be configured in `appsettings.json` under `Serilog`.

## License

MIT