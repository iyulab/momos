using Microsoft.Extensions.Hosting;
using Momos.Worker.SelfUpdate;

namespace Momos.Worker;

// Named explicitly (not a top-level-statement `Program`) because `Momos.Worker.Tests`
// references both this assembly and `Momos.Host`'s assembly, and Host's own top-level
// `Program` is deliberately public (WebApplicationFactory<Program> needs it) — an
// implicit `Program` here would collide (CS0433) in any file that references both.
internal static class WorkerProgram
{
    private static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The content root above is the directory this binary sits in. On an installed Worker that
        // is a versioned directory, one level below where the operator's settings file lives, so the
        // default content-root-relative lookup alone would never find it.
        WorkerInstallLayout.AddInstalledSettings(builder.Configuration, AppContext.BaseDirectory);

        builder.Services.AddMomosWorker(builder.Configuration);

        var host = builder.Build();
        host.Run();
    }
}
