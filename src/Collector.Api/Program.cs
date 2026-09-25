using System.Net;
using Collector.Api.Data;
using Collector.Api.Extensions;
using Collector.Api.Middleware;
using Collector.Api.Services;
using Collector.Extensions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Secrets are layered: appsettings (empty placeholders) → user-secrets (Development only,
// added by CreateBuilder) → COLLECTOR_API_* env vars (production, /etc/sc-tracker/api.env).
builder.Configuration.AddEnvironmentVariables(prefix: "COLLECTOR_API_");

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .CreateBootstrapLogger();
builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("Application", "Collector.Api"));

// Data directory: COLLECTOR_DATA_DIR, else config DataDir, else the data folder above
// the bin folder (mirrors the Collector convention).
var dataDir = Collector.Extensions.DataDirectory.ResolveForCurrentProcess(builder.Configuration["DataDir"]);

// Data layer (TrackerDbContext + repositories)
builder.Services.AddCollectorDataServices(builder.Configuration, dataDir);

// API services (ApiDbContext, auth, JWT, application services)
builder.Services.AddApiServices(builder.Configuration, dataDir);

// Discord enrichment client. Token stored in data/discord.token (editable from the
// admin dashboard), falling back to config Discord:BotToken / env Discord__BotToken.
builder.Services.AddSingleton(sp => new DiscordTokenStore(sp.GetRequiredService<IConfiguration>(), dataDir));
builder.Services.AddHttpClient<DiscordClient>();

// CORS — strict whitelist from configuration (no more AllowAnyOrigin).
var corsOrigins = builder.Configuration.GetSection("Api:Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Length == 0)
    {
        // Explicit: deny everything rather than silently allowing any origin.
        policy.WithOrigins("https://invalid.localhost.cors").AllowAnyHeader().AllowAnyMethod();
    }
    else
    {
        policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}));

// Rate limiting per signed-in user / per anonymous client IP, plus a login policy.
builder.Services.AddApiRateLimiting(builder.Configuration);

// The only proxy in front of the API is the web front on the same host: trust the
// client IP it forwards (X-Forwarded-For) from loopback only, one hop deep, so a
// remote client can never pick the IP it is rate-limited and logged under.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SC Organizations Tracker API",
        Version = "v1",
        Description = "REST API for Star Citizen organization data",
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Bearer token",
    });

    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "x-api-key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "API Key",
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Apply EF Core migrations on startup (no more EnsureCreated). For pre-existing
// databases created by the old EnsureCreated path, DatabaseBootstrap adopts the
// schema as baselined so MigrateAsync becomes a no-op.
using (var scope = app.Services.CreateScope())
{
    var apiDb = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    var bootstrapLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ApiDatabaseBootstrap");
    await Collector.Data.DatabaseBootstrap.MigrateOrAdoptAsync(apiDb, "api_users", bootstrapLogger);
}

// ---- middleware pipeline --------------------------------------------------

app.UseForwardedHeaders();
app.UseMiddleware<RequestCorrelationMiddleware>();
app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// No HTTPS redirection / HSTS: the API only listens on 127.0.0.1 and is reached by the
// web front over plain HTTP; browsers never talk to it (TLS terminates at nginx).

// Swagger is off by default; enable explicitly through Api:Swagger:Enabled (or Development env).
var swaggerEnabled = app.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Api:Swagger:Enabled", false);
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors();
app.UseAuthentication();
// After authentication so signed-in users are limited per user, not per IP.
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

try
{
    await app.RunAsync();
}
finally
{
    Log.CloseAndFlush();
}
