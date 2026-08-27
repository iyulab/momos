using Microsoft.EntityFrameworkCore;
using Momos.Host.Data;
using Momos.Host.Endpoints;
using Momos.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddMomosWorker(builder.Configuration);
builder.Services.AddDbContext<MomosDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("MomosDb")
        ?? throw new InvalidOperationException("ConnectionStrings:MomosDb is required.")));

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

app.Run();

public partial class Program;
