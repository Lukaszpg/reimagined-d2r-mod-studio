using System.Text.Json;
using System.Text.RegularExpressions;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

/// <summary>
/// Local-only settings for the optional GitHub -> CMake plugin build. Repository build details come
/// from charsi-package.json in the selected revision. The GitHub token is deliberately not part of
/// this record; it lives in Windows Credential Manager.
/// </summary>
public sealed record CMakeBuildSettings(
    string Repository = "",
    string RevisionKind = "branch",
    string Revision = "main",
    string DeploySubdirectory = "d2rloader/plugins",
    string[]? ConfigureArguments = null)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Repository);
    public static string SettingsFile(ModProject project) => Inside(project.Cache, "settings/cmake.json");

    public void Validate()
    {
        if (!Configured) return;
        _ = GitHubRepository.Parse(Repository);
        Require(RevisionKind is "branch" or "pull-request", "Revision type must be Branch or Pull request.");
        Require(!string.IsNullOrWhiteSpace(Revision), "Choose a branch or pull request.");
        if (RevisionKind == "pull-request") Require(int.TryParse(Revision.TrimStart('#'), out var number) && number > 0, "Pull request must be a positive PR number.");
        if (!string.IsNullOrWhiteSpace(DeploySubdirectory)) SafeRelative(NormalizeRelative(DeploySubdirectory));
        Require((ConfigureArguments ?? []).All(a => a.IndexOf('\0') < 0), "CMake arguments cannot contain NUL characters.");
    }

    public void Save(ModProject project)
    {
        Validate();
        AtomicWrite(SettingsFile(project), JsonSerializer.SerializeToUtf8Bytes(this, Pretty));
    }

    public static CMakeBuildSettings Load(ModProject project)
    {
        if (!File.Exists(SettingsFile(project))) return new();
        var settings = JsonSerializer.Deserialize<CMakeBuildSettings>(File.ReadAllText(SettingsFile(project)), Pretty) ?? new();
        settings.Validate();
        return settings;
    }

    internal static string NormalizeRelative(string value) => value.Trim().Replace('\\', '/').Trim('/');
}

/// <summary>The Charsi repository build contract consumed by both Charsi and Mod Studio.</summary>
public sealed record CharsiPackageManifest(
    int SchemaVersion = 0,
    string Plugin = "",
    string ProjectMode = "",
    string InstallMode = "",
    string CMakeSource = ".",
    string CMakeTarget = "",
    string OutputName = "",
    string RuntimeData = "")
{
    public const string FileName = "charsi-package.json";

    public static CharsiPackageManifest Load(string sourceRoot)
    {
        var path = Inside(sourceRoot, FileName);
        Require(File.Exists(path), $"Selected revision does not contain {FileName}. Add Charsi package metadata to the repository before building it in Mod Studio.");
        CharsiPackageManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CharsiPackageManifest>(File.ReadAllText(path), Pretty)
                ?? throw new InvalidDataException($"{FileName} is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex); }

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        Require(SchemaVersion == 1, $"{FileName} schemaVersion must be 1.");
        Require(!string.IsNullOrWhiteSpace(CMakeSource), $"{FileName} cmakeSource is required.");
        Require(!string.IsNullOrWhiteSpace(CMakeTarget), $"{FileName} cmakeTarget is required.");
        Require(!string.IsNullOrWhiteSpace(OutputName), $"{FileName} outputName is required.");
        if (CMakeSource != ".") SafeRelative(CMakeBuildSettings.NormalizeRelative(CMakeSource));
        var output = OutputName.Trim();
        Require(!output.Contains('/') && !output.Contains('\\') && Path.GetFileName(output) == output && output is not "." and not "..", $"{FileName} outputName must be a file name, not a path.");
        Require(output.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, $"{FileName} outputName contains invalid file-name characters.");
    }

    public string DllFileName => OutputName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? OutputName : OutputName + ".dll";
}

public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => Owner + "/" + Name;

    public static GitHubRepository Parse(string value)
    {
        value = value.Trim();
        string[] parts;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            Require(uri.Scheme is "https" or "http" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase), "Repository URL must point to github.com.");
            parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        else parts = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        Require(parts.Length == 2, "Repository must be owner/name or a github.com repository URL.");
        var owner = parts[0];
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        Require(Regex.IsMatch(owner, "^[A-Za-z0-9_.-]+$") && Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$"), "Repository owner/name contains unsupported characters.");
        return new(owner, name);
    }
}

public sealed record GitHubPullRequest(int Number, string Title, string HeadRef, string HeadRepository, string HeadSha)
{
    public override string ToString() => $"#{Number} · {HeadRef} · {Title}";
}

public sealed record GitHubResolvedRevision(string Kind, string Name, string Repository, string Sha)
{
    public string ShortSha => Sha.Length > 12 ? Sha[..12] : Sha;
    public override string ToString() => $"{Kind} {Name} · {Repository}@{ShortSha}";
}
