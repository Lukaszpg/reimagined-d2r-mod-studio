using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using static ModStudio.Core.Storage;

namespace ModStudio.Core;

public sealed record RemoteBuildArtifact(GitHubResolvedRevision Revision, string BuildType, string DllPath, string Sha256);

public static class RemoteCMakeBuildService
{
    public static async Task<RemoteBuildArtifact> BuildAsync(ModProject project, CMakeBuildSettings settings, string buildType, CancellationToken token = default, Action<string>? progress = null)
    {
        settings.Validate();
        Require(settings.Configured, "Configure the GitHub/CMake build first.");
        Require(buildType is "Release" or "Debug", "Build type must be Release or Debug.");

        using var github = new GitHubSourceClient(GitHubCredentialStore.Read());
        var revision = await github.ResolveAsync(settings, token);
        progress?.Invoke($"GitHub source resolved: {revision}.");

        var cache = Inside(project.Cache, "remote-build");
        var source = Inside(cache, $"sources/{revision.Sha}");
        if (!Directory.Exists(source))
        {
            var archive = Inside(cache, $"downloads/{revision.Sha}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            progress?.Invoke($"Downloading {revision.Repository}@{revision.ShortSha}…");
            await github.DownloadArchiveAsync(revision, archive, token);
            try { ExtractArchive(archive, source, token); }
            finally { if (File.Exists(archive)) File.Delete(archive); }
        }
        token.ThrowIfCancellationRequested();

        var package = CharsiPackageManifest.Load(source);
        progress?.Invoke($"Charsi package: {package.Plugin} · target {package.CMakeTarget} · output {package.DllFileName}.");
        var cmakeSource = package.CMakeSource == "." ? source : Inside(source, CMakeBuildSettings.NormalizeRelative(package.CMakeSource));
        Require(File.Exists(Path.Combine(cmakeSource, "CMakeLists.txt")), $"No CMakeLists.txt found under charsi-package.json cmakeSource '{package.CMakeSource}'.");
        var build = Inside(cache, $"build/{revision.Sha}/{buildType.ToLowerInvariant()}");
        // A commit SHA has immutable sources, so its CMake tree is safe to reuse. This preserves
        // FetchContent/dependency and compiler caches across repeated Build/Deploy clicks.
        Directory.CreateDirectory(build);

        var cmake = ResolveCMake();
        progress?.Invoke($"CMake detected: {cmake}");
        var configure = new List<string> { "-S", cmakeSource, "-B", build };
        if (OperatingSystem.IsWindows()) { configure.Add("-A"); configure.Add("x64"); }
        configure.AddRange(settings.ConfigureArguments ?? []);
        progress?.Invoke($"Configuring CMake · {buildType} · {revision.ShortSha}…");
        await RunProcessAsync(cmake, configure, source, token, progress);

        var buildArguments = new[] { "--build", build, "--config", buildType, "--target", package.CMakeTarget, "--parallel" };
        progress?.Invoke($"Building CMake target {package.CMakeTarget} · {buildType}…");
        await RunProcessAsync(cmake, buildArguments, source, token, progress);

        var dll = FindArtifact(build, package.DllFileName, buildType);
        var hash = Hash(File.ReadAllBytes(dll));
        progress?.Invoke($"CMake build complete: {Path.GetFileName(dll)} · {hash[..12]}.");
        return new(revision, buildType, dll, hash);
    }

    /// <summary>
    /// Adds the CMake DLL to Studio's existing build manifest rather than copying it separately.
    /// DeploymentService then stages, owns, journals and rolls back the DLL in the same transaction as mod data.
    /// </summary>
    public static BuildResult AttachDll(ModProject project, BuildResult build, RemoteBuildArtifact artifact, CMakeBuildSettings settings, CancellationToken token = default, Action<string>? progress = null)
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        using var buildLock = BuildCache.Lock(project);
        var manifest = Inside(project.Cache, "builds/current/build.json");
        Require(File.Exists(manifest) && JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(manifest), Pretty)?.Id == build.Id, "Build was replaced before the CMake DLL could be attached. Build again.");
        Require(build.ProjectId == project.Id, "Build belongs to a different project.");
        Require(File.Exists(artifact.DllPath) && Hash(File.ReadAllBytes(artifact.DllPath)) == artifact.Sha256, "Built DLL changed before deployment. Build again.");

        var subdirectory = CMakeBuildSettings.NormalizeRelative(settings.DeploySubdirectory);
        var relative = (subdirectory.Length == 0 ? "" : subdirectory + "/") + Path.GetFileName(artifact.DllPath);
        SafeRelative(relative);
        Require(build.Files.All(file => !file.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)), $"CMake DLL target conflicts with project build output: {relative}");

        var target = Inside(build.Output, relative);
        var bytes = File.ReadAllBytes(artifact.DllPath);
        token.ThrowIfCancellationRequested();
        AtomicWrite(target, bytes);
        var file = new FileInfo(target);
        Require(Hash(File.ReadAllBytes(target)) == artifact.Sha256, "Attached DLL hash mismatch.");

        var nextFiles = build.Files.Append(new BuildFile(relative, artifact.Sha256, file.Length)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var next = build with { Files = nextFiles, SourceRepository = artifact.Revision.Repository, SourceRevisionSha = artifact.Revision.Sha };
        AtomicWrite(manifest, JsonSerializer.SerializeToUtf8Bytes(next, Pretty));
        progress?.Invoke($"Attached DLL to transactional deployment · {relative} · {artifact.BuildType} · {artifact.Revision.ShortSha}");
        return next;
    }

    private static void ExtractArchive(string archivePath, string destination, CancellationToken token)
    {
        var stage = destination + ".extract-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var files = archive.Entries.Where(e => e.FullName.Length > 0).ToArray();
            Require(files.Length > 0, "GitHub source archive is empty.");
            var first = files[0].FullName.Replace('\\', '/');
            var slash = first.IndexOf('/');
            Require(slash > 0, "GitHub source archive has no repository root.");
            var prefix = first[..(slash + 1)];
            foreach (var entry in files)
            {
                token.ThrowIfCancellationRequested();
                var full = entry.FullName.Replace('\\', '/');
                Require(full.StartsWith(prefix, StringComparison.Ordinal), "GitHub source archive contains inconsistent roots.");
                var unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
                Require(unixKind != 0xA000, "GitHub source archive contains a symbolic link.");
                var relative = full[prefix.Length..].TrimEnd('/');
                if (relative.Length == 0) continue;
                SafeRelative(relative);
                var target = Inside(stage, relative);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static string FindArtifact(string buildRoot, string dllFileName, string buildType)
    {
        var matches = Directory.EnumerateFiles(buildRoot, dllFileName, SearchOption.AllDirectories).ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
        {
            var configMatches = matches.Where(path => Relative(buildRoot, path).Split('/').Contains(buildType, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (configMatches.Length == 1) return configMatches[0];
            throw new InvalidDataException($"CMake produced more than one {dllFileName}. Matches: {string.Join(", ", matches.Take(5).Select(p => Relative(buildRoot, p)))}");
        }
        throw new FileNotFoundException($"CMake target completed but charsi-package.json output '{dllFileName}' was not found under {buildRoot}.");
    }

    internal static string ResolveCMake()
    {
        var executable = OperatingSystem.IsWindows() ? "cmake.exe" : "cmake";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), executable);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(Directory.Exists))
            {
                var visualStudio = Path.Combine(root, "Microsoft Visual Studio");
                if (!Directory.Exists(visualStudio)) continue;
                foreach (var year in Directory.EnumerateDirectories(visualStudio))
                    foreach (var edition in Directory.EnumerateDirectories(year))
                    {
                        var candidate = Path.Combine(edition, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
            }
        }
        throw new FileNotFoundException("CMake was not found. Install CMake or Visual Studio with C++/CMake tools. Mod Studio detects CMake automatically from PATH and Visual Studio installations.");
    }

    private static async Task RunProcessAsync(string executable, IEnumerable<string> arguments, string workingDirectory, CancellationToken token, Action<string>? progress)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var tail = new Queue<string>();
        void Line(string? line)
        {
            if (line == null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 12) tail.Dequeue();
            }
            progress?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        try { Require(process.Start(), $"Could not start {Path.GetFileName(executable)}."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new InvalidDataException($"Could not start CMake: {ex.Message}", ex); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var wait = process.WaitForExitAsync(CancellationToken.None);
        try { await wait.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await wait;
            throw;
        }
        if (process.ExitCode != 0)
        {
            string details;
            lock (tail) details = string.Join(Environment.NewLine, tail);
            throw new InvalidDataException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.{(details.Length > 0 ? Environment.NewLine + details : "")}");
        }
    }
}
