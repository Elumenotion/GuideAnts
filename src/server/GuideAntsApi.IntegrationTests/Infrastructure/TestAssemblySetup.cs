namespace GuideAntsApi.IntegrationTests.Infrastructure;

[TestClass]
public static class TestAssemblySetup
{
    private static TestContainerManager? _containerManager;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        CleanupCodeqlBlockingDockerArtifacts();

        // Initialize the shared container manager once for all tests
        _containerManager = TestContainerManager.Instance;
        await _containerManager.EnsureInitializedAsync();
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        // Clean up the shared container manager
        if (_containerManager != null)
        {
            await _containerManager.DisposeAsync();
        }

        CleanupCodeqlBlockingDockerArtifacts();
    }

    private static void CleanupCodeqlBlockingDockerArtifacts()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var dockerVolumes = Path.Combine(projectRoot, "docker", "volumes");
        TryDeleteDirectory(dockerVolumes);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only; tests must not fail on locked files.
        }
    }
} 