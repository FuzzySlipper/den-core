using System.Diagnostics;
using System.Text;

namespace DenMcp.Core.Services;

public sealed record ProcessRunResult
{
    public required int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>
/// Bounded process runner. Executable and argv are supplied separately and never
/// executed through a shell by this runner.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Executable is required.", nameof(executable));
        ArgumentNullException.ThrowIfNull(args);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
                return new ProcessRunResult { ExitCode = 127, Stderr = $"Unable to start {executable}." };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

            return new ProcessRunResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessRunResult { ExitCode = 124, Stderr = $"{executable} command timed out." };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessRunResult { ExitCode = 127, Stderr = ex.Message };
        }
    }
}
