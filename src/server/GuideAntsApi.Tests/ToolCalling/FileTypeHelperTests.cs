using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class FileTypeHelperTests
{
    [TestMethod]
    public void GetContentType_Returns_known_mappings()
    {
        FileTypeHelper.GetContentType("notes.md").ContentType.Should().Be("text/markdown");
        FileTypeHelper.GetContentType("notes.md").IsBinary.Should().BeFalse();

        FileTypeHelper.GetContentType("photo.PNG").ContentType.Should().Be("image/png");
        FileTypeHelper.GetContentType("photo.PNG").IsBinary.Should().BeTrue();
    }

    [TestMethod]
    public void GetContentType_Throws_for_unknown_extension()
    {
        var act = () => FileTypeHelper.GetContentType("file.unknownext");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No content type mapping found*");
    }

    [TestMethod]
    public void FileDetails_Get_and_Write_round_trip_text_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"file-helper-{Guid.NewGuid():N}.txt");
        try
        {
            FileDetails.WriteFile(path, "hello file helper");

            var details = FileDetails.Get(path);

            details.Name.Should().EndWith(".txt");
            details.Content.Should().Be("hello file helper");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
