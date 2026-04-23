namespace GuideAntsApi.IntegrationTests.Infrastructure;

[TestClass]
public static class TestAssemblySetup
{
    private static TestContainerManager? _containerManager;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
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
    }
} 