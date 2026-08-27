using Microsoft.Extensions.Hosting;

namespace Momos.Worker;

// Named explicitly (not a top-level-statement `Program`) because `Momos.Worker.Tests`
// references both this assembly and `Momos.Host`'s assembly, and Host's own top-level
// `Program` is deliberately public (WebApplicationFactory<Program> needs it) — an
// implicit `Program` here would collide (CS0433) in any file that references both.
internal static class WorkerProgram
{
    private static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddMomosWorker(builder.Configuration);

        var host = builder.Build();
        host.Run();
    }
}
