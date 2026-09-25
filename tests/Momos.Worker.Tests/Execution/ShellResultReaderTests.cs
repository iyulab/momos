using CodeBeaker.Commands.Models;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class ShellResultReaderTests
{
    /// <summary>Stands in for a typed shell result record serialized with PascalCase properties.</summary>
    private sealed record TypedShellOutput(int ExitCode, string Stdout, string Stderr);

    [Fact]
    public void Read_StringResult_KeepsSuccessStdoutAndError()
    {
        // The native runtime's shape: stdout as the result, stderr as the error, and
        // success already derived from the exit code.
        var result = new CommandResult { Success = false, Result = "partial output", Error = "boom", DurationMs = 7 };

        var read = ShellResultReader.Read(result);

        Assert.False(read.Success);
        Assert.Equal("partial output", read.Stdout);
        Assert.Equal("boom", read.Error);
        Assert.Null(read.ExitCode);
    }

    [Fact]
    public void Read_AnonymousObjectWithZeroExit_IsSuccessWithStdout()
    {
        // The container runtime's shape: always reported as success, with stdout, stderr
        // and the exit code carried in an anonymous object.
        var result = CommandResult.Ok(new { stdout = "hello\n", stderr = "", exitCode = 0L });

        var read = ShellResultReader.Read(result);

        Assert.True(read.Success);
        Assert.Equal("hello\n", read.Stdout);
        Assert.Null(read.Error);
        Assert.Equal(0, read.ExitCode);
    }

    [Fact]
    public void Read_AnonymousObjectWithNonZeroExit_IsFailureWithStderrAsError()
    {
        var result = CommandResult.Ok(new { stdout = "3 tests failed", stderr = "fatal: not found", exitCode = 128L });

        var read = ShellResultReader.Read(result);

        Assert.False(read.Success);
        Assert.Equal("3 tests failed", read.Stdout);
        Assert.Equal("fatal: not found", read.Error);
        Assert.Equal(128, read.ExitCode);
    }

    [Fact]
    public void Read_NonZeroExitWithEmptyStderr_ReportsTheExitCodeAsError()
    {
        var result = CommandResult.Ok(new { stdout = "", stderr = "", exitCode = 1L });

        var read = ShellResultReader.Read(result);

        Assert.False(read.Success);
        Assert.Equal("exit code 1", read.Error);
        Assert.Equal(1, read.ExitCode);
    }

    [Fact]
    public void Read_ZeroExitWithStderr_IsSuccessAndKeepsStderrAsError()
    {
        // Mirrors the string shape, where stderr is reported regardless of the exit code.
        var result = CommandResult.Ok(new { stdout = "done", stderr = "warning: deprecated", exitCode = 0L });

        var read = ShellResultReader.Read(result);

        Assert.True(read.Success);
        Assert.Equal("warning: deprecated", read.Error);
    }

    [Fact]
    public void Read_NullResult_KeepsSuccessAndError()
    {
        var result = CommandResult.Fail("container is gone", durationMs: 3);

        var read = ShellResultReader.Read(result);

        Assert.False(read.Success);
        Assert.Null(read.Stdout);
        Assert.Equal("container is gone", read.Error);
        Assert.Null(read.ExitCode);
    }

    [Fact]
    public void Read_TypedRecordWithPascalCaseProperties_IsReadCaseInsensitively()
    {
        var result = new CommandResult { Success = false, Result = new TypedShellOutput(2, "out", "err") };

        var read = ShellResultReader.Read(result);

        Assert.False(read.Success);
        Assert.Equal("out", read.Stdout);
        Assert.Equal("err", read.Error);
        Assert.Equal(2, read.ExitCode);
    }

    [Fact]
    public void Read_ObjectWithoutExitCode_FallsBackToTheReportedSuccessAndError()
    {
        var result = new CommandResult { Success = true, Result = new { stdout = "listing" } };

        var read = ShellResultReader.Read(result);

        Assert.True(read.Success);
        Assert.Equal("listing", read.Stdout);
        Assert.Null(read.Error);
        Assert.Null(read.ExitCode);
    }
}
