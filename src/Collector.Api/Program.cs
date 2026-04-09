using System.Net;
using System.Threading.RateLimiting;
using Collector.Api.Data;
using Collector.Api.Extensions;
using Collector.Api.Middleware;
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

// Secrets are layered: appsettings (empty placeholders) → user-secrets (dev-only, never
// deployed) → COLLECTOR_API_* env vars (prod). AddUserSecrets is always attempted with
// optional:true so the dev path works without manually setting ASPNETCORE_ENVIRONMENT;
// in production the store simply doesn't exist and is silently skipped.
builder.Configuration.AddUserSecrets<Program>(optional: true);
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

// Data directory is one level above the bin folder (mirrors the Collector convention).
var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../..", "data"));

// Data layer (TrackerDbContext + repositories)
builder.Services.AddCollectorDataServices(builder.Configuration, dataDir);

// API services (ApiDbContext, auth, JWT, application services)
builder.Services.AddApiServices(builder.Configuration, dataDir);

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

// Global rate limiting (per IP) to protect public endpoints from abuse.
var permitLimit = builder.Configuration.GetValue("Api:RateLimit:PermitLimit", 100);
var windowSeconds = builder.Configuration.GetValue("Api:RateLimit:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
        });
    });
});

// Strict transport security (prod) — only emits the header when HTTPS is actually used.
builder.Services.AddHsts(options =>
{
    options.Preload = true;
    options.IncludeSubDomains = true;
    options.MaxAge = TimeSpan.FromDays(365);
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

// Swagger is off by default; enable explicitly through Api:Swagger:Enabled (or Development env).
var swaggerEnabled = app.Environment.IsDevelopment()
    || builder.Configuration.GetValue("Api:Swagger:Enabled", false);
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
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
