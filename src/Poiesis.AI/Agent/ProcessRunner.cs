using System.Diagnostics;
using System.Text;

namespace Poiesis.AI.Agent;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Lance une commande (dotnet, git) et capture sa sortie, avec un délai maximal.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-1, output + $"\n[délai dépassé : {timeout.TotalSeconds:0} s]");
        }
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    /// <summary>Garde la fin d'une sortie longue : c'est là que sont les erreurs de build et de tests.</summary>
    public static string Tail(string text, int maxChars = 12_000) =>
        text.Length <= maxChars ? text : "[…]\n" + text[^maxChars..];
}
