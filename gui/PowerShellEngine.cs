using System.Diagnostics;
using System.Reflection;

namespace XCTrackMapSync;

internal sealed class PowerShellEngine
{
    private readonly string _scriptPath;

    public PowerShellEngine()
    {
        _scriptPath = ExtractEmbeddedScript();
    }

    public async Task<PowerShellResult> RunAsync(
        IEnumerable<string> scriptArguments,
        Action<PowerShellLine>? onLine,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        bool useChildPowerShell = false)
    {
        var arguments = scriptArguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_scriptPath)!
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        if (useChildPowerShell)
        {
            // A fresh intermediate process isolates Windows Shell MTP state.
            // Some long-lived GUI sessions otherwise leave every subsequent
            // direct Shell scan blocked until its timeout.
            startInfo.ArgumentList.Add("-Command");
            var quotedEnginePath = QuotePowerShellArgument(_scriptPath);
            var quotedArguments = string.Join(" ", arguments.Select(QuotePowerShellArgument));
            startInfo.ArgumentList.Add(
                $"& powershell.exe -NoProfile -ExecutionPolicy Bypass -File {quotedEnginePath} {quotedArguments}; " +
                "exit $LASTEXITCODE");
        }
        else
        {
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var lines = new List<PowerShellLine>();
        var gate = new object();

        void Capture(string? text, bool isError)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var line = new PowerShellLine(text, isError);
            lock (gate)
            {
                lines.Add(line);
            }
            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, args) => Capture(args.Data, false);
        process.ErrorDataReceived += (_, args) => Capture(args.Data, true);

        if (!process.Start())
        {
            throw new InvalidOperationException("Nepavyko paleisti Windows PowerShell.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may finish between HasExited and Kill.
            }
        });

        var exitTask = process.WaitForExitAsync(cancellationToken);
        if (timeout is not null)
        {
            var timeoutTask = Task.Delay(timeout.Value, cancellationToken);
            var completedTask = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
            if (completedTask != exitTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The process may finish while the timeout is handled.
                }

                try
                {
                    process.WaitForExit(2_000);
                }
                catch
                {
                    // The timeout result is more useful than a cleanup error.
                }

                throw new TimeoutException($"PowerShell operation timed out after {timeout.Value.TotalSeconds:N0} seconds.");
            }
        }

        await exitTask.ConfigureAwait(false);
        process.WaitForExit(); // Flush asynchronous output events.

        lock (gate)
        {
            return new PowerShellResult(process.ExitCode, lines.ToArray());
        }
    }

    private static string ExtractEmbeddedScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("Sync-XCTrackMaps.ps1", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Programoje nerastas įterptas sinchronizavimo variklis.");
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XCTrackMapSync");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "Sync-XCTrackMaps.ps1");
        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Nepavyko atverti įterpto sinchronizavimo variklio.");
        using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        source.CopyTo(destination);
        return path;
    }

    private static string QuotePowerShellArgument(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
