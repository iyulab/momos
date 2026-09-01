namespace Momos.Worker.SelfUpdate;

public sealed class WorkerSelfUpdateOptions
{
    public const string SectionName = "Momos:Worker:SelfUpdate";

    public string Repo { get; set; } = "iyulab/momos";

    /// <summary>installs/&lt;version&gt;/ + current 포인터가 사는 루트 — 기본값은 실행 파일이 있는
    /// 디렉터리의 부모(설치 스크립트가 만드는 $INSTALL_DIR과 같은 자리, Task 6 참고).</summary>
    public string InstallRoot { get; set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));

    /// <summary>OS/아키텍처 식별자 — install-worker.sh/.ps1과 동일한 값(linux-x64/win-x64).</summary>
    public string Rid { get; set; } = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
}
