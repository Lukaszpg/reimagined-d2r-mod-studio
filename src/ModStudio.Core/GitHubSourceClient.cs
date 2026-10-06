using System.Net.Http.Headers;
using System.Text.Json;

namespace ModStudio.Core;

/// <summary>Small GitHub REST client. Tokens are only placed in the Authorization header and are never logged.</summary>
public sealed class GitHubSourceClient : IDisposable
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public GitHubSourceClient(string? token)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Reimagined-D2R-Mod-Studio");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    public async Task<string> GetUserAsync(CancellationToken token = default)
    {
        using var document = await JsonAsync("https://api.github.com/user", token);
        return document.RootElement.GetProperty("login").GetString() ?? throw new InvalidDataException("GitHub did not return a user login.");
    }

    public async Task<string[]> GetBranchesAsync(GitHubRepository repository, CancellationToken token = default)
    {
        var result = new List<string>();
        for (int page = 1; ; page++)
        {
            using var document = await JsonAsync($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/branches?per_page=100&page={page}", token);
            var batch = document.RootElement.EnumerateArray().Select(x => x.GetProperty("name").GetString()).OfType<string>().ToArray();
            result.AddRange(batch);
            if (batch.Length < 100) break;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<GitHubPullRequest[]> GetPullRequestsAsync(GitHubRepository repository, CancellationToken token = default)
    {
        var result = new List<GitHubPullRequest>();
        for (int page = 1; ; page++)
        {
            using var document = await JsonAsync($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/pulls?state=open&per_page=100&page={page}", token);
            var batch = document.RootElement.EnumerateArray().Select(pr =>
            {
                var head = pr.GetProperty("head");
                var headRepo = head.GetProperty("repo");
                return new GitHubPullRequest(
                    pr.GetProperty("number").GetInt32(),
                    pr.GetProperty("title").GetString() ?? "",
                    head.GetProperty("ref").GetString() ?? "",
                    headRepo.ValueKind == JsonValueKind.Null ? repository.FullName : headRepo.GetProperty("full_name").GetString() ?? repository.FullName,
                    head.GetProperty("sha").GetString() ?? throw new InvalidDataException("Pull request has no head SHA."));
            }).ToArray();
            result.AddRange(batch);
            if (batch.Length < 100) break;
        }
        return result.OrderByDescending(pr => pr.Number).ToArray();
    }

    public async Task<GitHubResolvedRevision> ResolveAsync(CMakeBuildSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        var repository = GitHubRepository.Parse(settings.Repository);
        if (settings.RevisionKind == "branch")
        {
            using var document = await JsonAsync($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/commits/{Uri.EscapeDataString(settings.Revision)}", token);
            var sha = document.RootElement.GetProperty("sha").GetString() ?? throw new InvalidDataException("GitHub did not return a commit SHA.");
            return new("branch", settings.Revision, repository.FullName, sha);
        }

        var number = int.Parse(settings.Revision.TrimStart('#'), System.Globalization.CultureInfo.InvariantCulture);
        using (var document = await JsonAsync($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/pulls/{number}", token))
        {
            var root = document.RootElement;
            var head = root.GetProperty("head");
            var headRepo = head.GetProperty("repo");
            var archiveRepository = headRepo.ValueKind == JsonValueKind.Null ? repository.FullName : headRepo.GetProperty("full_name").GetString() ?? repository.FullName;
            var sha = head.GetProperty("sha").GetString() ?? throw new InvalidDataException("Pull request has no head SHA.");
            var branch = head.GetProperty("ref").GetString() ?? $"PR #{number}";
            return new("pull request", $"#{number} ({branch})", archiveRepository, sha);
        }
    }

    public async Task DownloadArchiveAsync(GitHubResolvedRevision revision, string destination, CancellationToken token = default)
    {
        var repository = GitHubRepository.Parse(revision.Repository);
        using var response = await client.GetAsync($"https://api.github.com/repos/{repository.Owner}/{repository.Name}/zipball/{revision.Sha}", HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(output, token);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<JsonDocument> JsonAsync(string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidDataException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}. Check repository access and token permissions.");
    }

    public void Dispose() => client.Dispose();
}
