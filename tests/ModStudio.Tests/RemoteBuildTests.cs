using ModStudio.Core;
using static ModStudio.Core.Storage;

internal static class RemoteBuildTests
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        var projectRoot = Path.Combine(root, "remote-build-project");
        Directory.CreateDirectory(projectRoot);
        var project = new ModProject(projectRoot, "remote-build-project", "RemoteBuild");

        var disabled = new CMakeBuildSettings();
        disabled.Save(project);
        var disabledJson = File.ReadAllText(CMakeBuildSettings.SettingsFile(project));
        check(!disabledJson.Contains("token", StringComparison.OrdinalIgnoreCase), "Remote build settings never persist a GitHub token");

        var settings = new CMakeBuildSettings(
            "https://github.com/Lukaszpg/d2rl-sanctuary-of-exile.git",
            "pull-request",
            "12",
            ".",
            "soe",
            "bin/{config}/d2rl-soe.dll",
            "d2rloader/plugins",
            "",
            ["-DSOE_WARNINGS_AS_ERRORS=OFF"]);
        settings.Save(project);
        var loaded = CMakeBuildSettings.Load(project);
        check(loaded.Repository == settings.Repository
            && loaded.RevisionKind == settings.RevisionKind
            && loaded.Revision == settings.Revision
            && loaded.CMakeSource == settings.CMakeSource
            && loaded.CMakeTarget == settings.CMakeTarget
            && loaded.DllRelativePath == settings.DllRelativePath
            && loaded.DeploySubdirectory == settings.DeploySubdirectory
            && loaded.CMakeExecutable == settings.CMakeExecutable
            && (loaded.ConfigureArguments ?? []).SequenceEqual(settings.ConfigureArguments ?? []),
            "GitHub/CMake settings round trip in the project-local .studio cache");

        var repository = GitHubRepository.Parse(settings.Repository);
        check(repository.Owner == "Lukaszpg" && repository.Name == "d2rl-sanctuary-of-exile", "GitHub repository URLs normalize to owner/name");
        check(GitHubRepository.Parse("D2RLoader/PluginSDK").FullName == "D2RLoader/PluginSDK", "owner/name repository syntax is accepted");
        throws(() => GitHubRepository.Parse("https://example.com/owner/repo"), "Non-GitHub repository URLs are rejected");
        throws(() => (settings with { RevisionKind = "pull-request", Revision = "abc" }).Validate(), "Pull request revisions require a numeric PR");

        var artifactFolder = Path.Combine(projectRoot, ".studio", "fake-build");
        Directory.CreateDirectory(artifactFolder);
        var dll = Path.Combine(artifactFolder, "d2rl-soe.dll");
        File.WriteAllBytes(dll, [1, 2, 3, 4, 5]);
        var artifact = new RemoteBuildArtifact(new("branch", "main", "Lukaszpg/d2rl-sanctuary-of-exile", new string('a', 40)), "Debug", dll, Hash(File.ReadAllBytes(dll)));

        var buildFolder = Inside(project.Cache, "builds/current");
        var output = Inside(buildFolder, "output");
        var snapshot = Inside(buildFolder, "snapshot");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(snapshot);
        var build = new BuildResult("remote-build-id", "d2rl", project.Id, project.Name, output, [], snapshot);
        AtomicWrite(Inside(buildFolder, "build.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(build, Pretty));

        var attached = RemoteCMakeBuildService.AttachDll(project, build, artifact, settings);
        var relativeDll = "d2rloader/plugins/d2rl-soe.dll";
        var target = Inside(output, relativeDll);
        check(attached.Files.Single().Path == relativeDll
            && attached.Files.Single().Sha256 == artifact.Sha256
            && File.ReadAllBytes(target).SequenceEqual([1, 2, 3, 4, 5]),
            "Remote DLL is attached to the ordinary Studio build so DeploymentService owns it transactionally");
        var savedBuild = System.Text.Json.JsonSerializer.Deserialize<BuildResult>(File.ReadAllText(Inside(buildFolder, "build.json")), Pretty)!;
        check(savedBuild.Files.Single().Path == relativeDll && savedBuild.Files.Single().Sha256 == artifact.Sha256,
            "The current build manifest records the attached DLL before deployment");

        File.WriteAllBytes(dll, [9]);
        throws(() => RemoteCMakeBuildService.AttachDll(project, build, artifact, settings), "A DLL changed after the CMake build is not attached for deployment");
    }
}
