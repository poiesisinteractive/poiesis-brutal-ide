using Poiesis.AI.Agent;

namespace Poiesis.AI.Features;

/// <summary>
/// Espace de travail isolé d'une feature : une branche <c>feature/&lt;slug&gt;</c> dans un worktree git sous
/// <c>.poiesis/worktrees/</c>. Le projet principal n'est jamais touché tant que le dev n'a pas accepté.
/// </summary>
public sealed class FeatureWorkspace
{
    FeatureWorkspace(string repoRoot, string slug, string path, string branch, string baseCommit)
    {
        RepoRoot = repoRoot;
        Slug = slug;
        Path = path;
        Branch = branch;
        BaseCommit = baseCommit;
    }

    public string RepoRoot { get; }
    public string Slug { get; }
    public string Path { get; }
    public string Branch { get; }
    public string BaseCommit { get; }

    public static async Task<FeatureWorkspace> CreateAsync(string repoRoot, string featureTitle, CancellationToken ct = default)
    {
        var slug = Slugify(featureTitle);
        var branch = $"feature/{slug}";
        var path = System.IO.Path.Combine(repoRoot, ".poiesis", "worktrees", slug);

        var head = await Git(repoRoot, ct, "rev-parse", "HEAD");
        await Git(repoRoot, ct, "worktree", "add", "-b", branch, path, "HEAD");
        return new FeatureWorkspace(repoRoot, slug, path, branch, head.Trim());
    }

    /// <summary>Commite tout le travail de l'agent sur la branche de la feature.</summary>
    public async Task CommitAsync(string message, CancellationToken ct = default)
    {
        await Git(Path, ct, "add", "-A");
        var status = await Git(Path, ct, "status", "--porcelain");
        if (status.Length > 0)
            await Git(Path, ct, "commit", "-m", message);
    }

    /// <summary>Le diff complet de la feature (code et données) par rapport au point de départ.</summary>
    public Task<string> DiffAsync(CancellationToken ct = default) => Git(Path, ct, "diff", BaseCommit, "--stat", "--patch");

    /// <summary>Acceptée : la branche est fusionnée dans la branche courante du projet, puis le worktree est retiré.</summary>
    public async Task AcceptAsync(CancellationToken ct = default)
    {
        await Git(RepoRoot, ct, "merge", "--no-ff", "--no-edit", Branch);
        await Git(RepoRoot, ct, "worktree", "remove", Path);
    }

    /// <summary>Refusée : le worktree est retiré ; la branche reste, pour qu'on puisse y revenir.</summary>
    public Task RejectAsync(CancellationToken ct = default) => Git(RepoRoot, ct, "worktree", "remove", "--force", Path);

    static async Task<string> Git(string cwd, CancellationToken ct, params string[] args)
    {
        var r = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromMinutes(2), ct);
        if (!r.Succeeded)
            throw new InvalidOperationException($"git {string.Join(' ', args)} : {r.Output}");
        return r.Output;
    }

    public static string Slugify(string title)
    {
        var chars = title.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? "feature" : slug;
    }
}
