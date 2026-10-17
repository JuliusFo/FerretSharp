using System.Diagnostics;
using FerretSharp.Core.ClrModel;

namespace FerretSharp.Core.Tests.ClrModel;

/// <summary>
/// Review 3.40.0, A18: every dotnet process FerretSharp starts is handed to the host, which puts it into a job object that
/// ends it with FerretSharp. Other tests start dotnet too and may pass the hook meanwhile – only this test's process counts.
/// </summary>
public sealed class ChildProcessesTests
{
    [Fact]
    public async Task Each_started_dotnet_process_is_adopted()
    {
        var adopted = new List<int>();
        var previous = ChildProcesses.Adopt;
        ChildProcesses.Adopt = process =>
        {
            lock (adopted)
            {
                adopted.Add(process.Id);
            }
        };
        try
        {
            using var started = DotNetCli.Start(["--version"], Path.GetTempPath(), _ => { }, _ => { });
            await started.WaitForExitAsync(TestContext.Current.CancellationToken);

            lock (adopted)
            {
                Assert.Contains(started.Id, adopted);
            }
        }
        finally
        {
            ChildProcesses.Adopt = previous;
        }
    }

    [Fact]
    public void A_hook_that_fails_does_not_stop_the_start()
    {
        var previous = ChildProcesses.Adopt;
        ChildProcesses.Adopt = _ => throw new System.ComponentModel.Win32Exception(5);
        try
        {
            using var process = Process.GetCurrentProcess();

            ChildProcesses.Started(process); // must not throw
        }
        finally
        {
            ChildProcesses.Adopt = previous;
        }
    }
}
