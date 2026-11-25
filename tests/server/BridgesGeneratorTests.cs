using KoboLogicPuzzles.Server.Hashi;

namespace KoboLogicPuzzles.Server.Tests;

public sealed class BridgesGeneratorTests
{
    private static readonly HashiGenerationParameters Parameters = new("00000001", 10, 10, 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReapsTheHelperOnTimeoutAndCancellation(bool cancel)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var directory = Directory.CreateTempSubdirectory("bridges-test-");
        try
        {
            var pidFile = Path.Combine(directory.FullName, "pid");
            var generator = Stub(directory, $"printf '%s' \"$$\" > '{pidFile}'\nexec sleep 60\n");
            using var cancellation = new CancellationTokenSource();
            var generated = generator.GenerateAsync(20, Parameters, TimeSpan.FromSeconds(cancel ? 10 : 1), cancellation.Token);
            if (cancel)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!File.Exists(pidFile))
                    await Task.Delay(10, wait.Token);
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generated);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => generated);
            }
            var pid = await File.ReadAllTextAsync(pidFile);
            Assert.False(Directory.Exists($"/proc/{pid}"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RejectsOversizedOutput()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var directory = Directory.CreateTempSubdirectory("bridges-test-");
        try
        {
            var generator = Stub(directory, "exec yes x\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                generator.GenerateAsync(20, Parameters, TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.Contains("output limit", error.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static BridgesGenerator Stub(DirectoryInfo directory, string commands)
    {
        var executable = Path.Combine(directory.FullName, "bridgegen");
        File.WriteAllText(executable, "#!/bin/sh\n" + commands);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new BridgesGenerator(new HashiGenerationOptions { ExecutablePath = executable });
    }
}
