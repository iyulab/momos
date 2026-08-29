using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;
using Momos.Host.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
// Named enum values (not the underlying int) in request/response bodies — the
// OpenAPI metadata (.Produces<T>()) already promises a discoverable contract,
// and an integer alone doesn't tell a caller what it means. Momos.Worker's
// HostApiClient carries the matching converter on its own JsonSerializerOptions
// (separate deployable, ADR-0008 decision 1 — no shared serialization config).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<MomosDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("MomosDb")
        ?? throw new InvalidOperationException("ConnectionStrings:MomosDb is required.")));
builder.Services
    .AddOptions<InspectionClaimOptions>()
    .Bind(builder.Configuration.GetSection(InspectionClaimOptions.SectionName));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MomosDbContext>().Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapProjectEndpoints();
app.MapInspectionRequestEndpoints();
app.MapInspectionReportEndpoints();

app.Run();

public partial class Program;
