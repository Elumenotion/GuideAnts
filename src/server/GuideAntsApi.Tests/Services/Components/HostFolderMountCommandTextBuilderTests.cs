using FluentAssertions;
using GuideAntsApi.Services.Components;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountCommandTextBuilderTests
{
    [TestMethod]
    public void QuoteForPowerShellArgument_EscapesEmbeddedQuotes()
    {
        HostFolderMountCommandTextBuilder.QuoteForPowerShellArgument(@"a""b")
            .Should().Be(@"""a\""b""");
    }

    [TestMethod]
    public void QuoteForBashArgument_EscapesEmbeddedSingleQuotes()
    {
        HostFolderMountCommandTextBuilder.QuoteForBashArgument("it's")
            .Should().Be(@"'it'\''s'");
    }

    [DataTestMethod]
    [DataRow(@"D:\Data\Shared", "D:/Data/Shared")]
    [DataRow(@"/home/me/shared", "/home/me/shared")]
    public void FormatHostPathForCompose_NormalizesSeparators(string input, string expected)
    {
        HostFolderMountCommandTextBuilder.FormatHostPathForCompose(input).Should().Be(expected);
    }

    [TestMethod]
    public void BuildApplyCommand_DoesNotInlineUnquotedHostPath()
    {
        var mountId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var projectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var command = HostFolderMountCommandTextBuilder.BuildApplyCommand(
            mountId,
            projectId,
            @"D:\Data\Shared; rm -rf /");

        command.Should().Contain(mountId.ToString());
        command.Should().Contain(projectId.ToString());
        if (OperatingSystem.IsWindows())
        {
            command.Should().StartWith(@".\scripts\guideants-host-mount.ps1 apply");
            command.Should().Contain(@"-HostPath ""D:\Data\Shared; rm -rf /""");
        }
        else
        {
            command.Should().StartWith("./scripts/guideants-host-mount.sh apply");
            command.Should().Contain("--host-path 'D:\\Data\\Shared; rm -rf /'");
        }
    }

    [TestMethod]
    public void BuildRemoveCommand_ContainsOnlyMountId()
    {
        var mountId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var command = HostFolderMountCommandTextBuilder.BuildRemoveCommand(mountId);

        command.Should().Contain(mountId.ToString());
        if (OperatingSystem.IsWindows())
        {
            command.Should().Be(@".\scripts\guideants-host-mount.ps1 remove -MountId ""11111111-2222-3333-4444-555555555555""");
        }
        else
        {
            command.Should().Be("./scripts/guideants-host-mount.sh remove --mount-id '11111111-2222-3333-4444-555555555555'");
        }
    }
}
