using System.Diagnostics;
using FluentAssertions;

namespace Sportarr.Api.Tests.Services;

public class ContainerStartupScriptTests
{
    [Fact]
    public async Task RunsConfiguredScriptAndPropagatesItsEnvironment()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"sportarr-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "marker");
            var script = Path.Combine(directory, "startup.sh");
            await File.WriteAllTextAsync(script, $"printf '%s' \"$STARTUP_VALUE\" > '{marker}'\n");
            var helper = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "../../../../../docker-startup-script.sh"));
            var startInfo = new ProcessStartInfo("/bin/bash", helper)
            {
                UseShellExecute = false
            };
            startInfo.Environment["SPORTARR_STARTUP_SCRIPT"] = script;
            startInfo.Environment["STARTUP_VALUE"] = "ran-as-configured";

            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0);
            (await File.ReadAllTextAsync(marker)).Should().Be("ran-as-configured");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
