using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;
using Momos.Host.Endpoints;
using Momos.Host.Knowledge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
// Named enum values (not the underlying int) in request/response bodies — the
// OpenAPI metadata (.Produces<T>()) already promises a discoverable contract,
// and an integer alone doesn't tell a caller what it means. Momos.Worker's
// HostApiClient carries the matching converter on its own JsonSerializerOptions
// (separate deployable with no shared serialization config between the two).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<MomosDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("MomosDb")
        ?? throw new InvalidOperationException("ConnectionStrings:MomosDb is required.")));
builder.Services
    .AddOptions<InspectionClaimOptions>()
    .Bind(builder.Configuration.GetSection(InspectionClaimOptions.SectionName));
builder.Services
    .AddOptions<WorkerAuthOptions>()
    .Bind(builder.Configuration.GetSection(WorkerAuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services
    .AddOptions<WorkerCompatibilityOptions>()
    .Bind(builder.Configuration.GetSection(WorkerCompatibilityOptions.SectionName));
builder.Services
    .AddOptions<KnowledgeOptions>()
    .Bind(builder.Configuration.GetSection(KnowledgeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddKnowledgeIndex();

var app = builder.Build();

// Retry budget kept well under Azure Container Apps' default ~4-minute startup probe grace
// window (TCP probe, periodSeconds=1, failureThreshold=240 when no custom probe is defined) —
// exhausting it without succeeding should fail fast into a visible crash, not get killed
// mid-retry by the platform and look like the same crash loop this exists to fix.
DatabaseMigrator.MigrateWithRetry(
    migrate: () =>
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MomosDbContext>().Database.Migrate();
    },
    logger: app.Logger,
    retryDelays: [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)]);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // A reverse proxy's own IP isn't known in advance in a platform-managed
    // deployment, so the default known-networks/known-proxies allowlist
    // (loopback only) would reject every real forwarded header — clear it
    // to trust the immediate upstream unconditionally.
    KnownIPNetworks = { },
    KnownProxies = { },
});
app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapProjectEndpoints();
app.MapInspectionRequestEndpoints();
app.MapInspectionReportEndpoints();
app.MapKnowledgeEndpoints();

app.Run();

public partial class Program;
