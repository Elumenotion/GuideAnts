using FluentAssertions;
using GuideAntsApi.Services;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class MediaAttachmentHelperTests
{
    [TestMethod]
    public void IsImageFile_Recognizes_common_image_extensions()
    {
        MediaAttachmentHelper.IsImageFile("photo.png").Should().BeTrue();
        MediaAttachmentHelper.IsImageFile("photo.JPG").Should().BeTrue();
        MediaAttachmentHelper.IsImageFile("notes.md").Should().BeFalse();
    }
}
