using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class NotebookRunOutputWriterTests
{
    [TestMethod]
    public void ReserveUniqueWireFilename_ReturnsStampedName_WhenNoCollision()
    {
        using var storage = new TempDirectory();
        var timestamp = new DateTime(2026, 7, 8, 13, 53, 12, DateTimeKind.Utc);

        var filename = NotebookRunOutputWriter.ReserveUniqueWireFilename(storage.Path, "png", timestamp);

        filename.Should().Be("wire-20260708-135312.png");
    }

    [TestMethod]
    public void ReserveUniqueWireFilename_AppendsSequence_WhenBaseNameExists()
    {
        using var storage = new TempDirectory();
        var timestamp = new DateTime(2026, 7, 8, 13, 53, 12, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(storage.Path, "wire-20260708-135312.png"), "existing");

        var filename = NotebookRunOutputWriter.ReserveUniqueWireFilename(storage.Path, "png", timestamp);

        filename.Should().Be("wire-20260708-135312(1).png");
    }

    [TestMethod]
    public void ReserveUniqueWireFilename_IncrementsSequence_UntilFreeNameFound()
    {
        using var storage = new TempDirectory();
        var timestamp = new DateTime(2026, 7, 8, 13, 53, 12, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(storage.Path, "wire-20260708-135312.png"), "existing");
        File.WriteAllText(Path.Combine(storage.Path, "wire-20260708-135312(1).png"), "existing");
        File.WriteAllText(Path.Combine(storage.Path, "wire-20260708-135312(2).png"), "existing");

        var filename = NotebookRunOutputWriter.ReserveUniqueWireFilename(storage.Path, "png", timestamp);

        filename.Should().Be("wire-20260708-135312(3).png");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guideants-wire-names-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
