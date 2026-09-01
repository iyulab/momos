namespace Momos.Worker.SelfUpdate;

public sealed class WorkerSelfUpdateOptions
{
    public const string SectionName = "Momos:Worker:SelfUpdate";

    /// <summary>
    /// Off unless an operator turns it on. Staging an update ends in <c>Environment.Exit</c>, which
    /// only produces a running Worker again if whatever supervises the process is configured to
    /// restart it on exit. On a machine where that is not set up yet, an armed self-updater would
    /// take the Worker down for good on the first update hint, so arming it stays an explicit
    /// deployment decision rather than something a Host-side setting can switch on remotely.
    /// While it is off the Worker still reports that an update is due — it just does not act on it.
    /// </summary>
    public bool Enabled { get; set; }

    public string Repo { get; set; } = "iyulab/momos";

    /// <summary>installs/&lt;version&gt;/ + the current pointer live directly under this — the
    /// directory the install scripts deploy into. Derived from where the running binary sits; the
    /// fallback only applies to a Worker that is not running out of an installed layout at all,
    /// where self-update has nothing to swap and is expected to stay off.</summary>
    public string InstallRoot { get; set; } =
        WorkerInstallLayout.ResolveInstallRoot(AppContext.BaseDirectory)
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

    /// <summary>OS/아키텍처 식별자 — install-worker.sh/.ps1과 동일한 값(linux-x64/win-x64).</summary>
    public string Rid { get; set; } = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
}
