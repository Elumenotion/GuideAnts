using FluentAssertions;
using GuideAntsApi.Configuration;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.Components;

[TestClass]
public sealed class HostFolderMountComposeOverridePlanTests
{
    [TestMethod]
    public void BuildComposeOverridePlan_LocalPath_ProducesBindVolumesForAffectedServices()
    {
        var mountId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var mount = new HostFolderMount
        {
            Id = mountId,
            ProjectId = projectId,
            Scope = HostFolderMountScope.Notebook,
            SourceKind = SourceKind.LocalPath,
            DisplayName = "Shared",
            LeafName = "Shared",
            MountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId),
            SourceSpec = @"D:\Data\Shared",
            ContainerSourcePath = "/app/HostMounts/test",
            CreatedByUserId = Guid.NewGuid()
        };

        var service = new HostFolderMountService(
            scopeFactory: null!,
            pathResolver: null!,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsRuntimeOptions
            {
                AffectedMountServices = "guideants-webapi-ui;guideants-ai;plantuml"
            }),
            configuration: new ConfigurationBuilder().Build());

        var plan = service.BuildComposeOverridePlan(mount);

        plan.SourceKind.Should().Be(SourceKind.LocalPath);
        plan.BindVolumes.Should().NotBeNull();
        plan.BindVolumes!.Should().HaveCount(3);
        plan.BindVolumes.Should().OnlyContain(v => v.HostSourcePath == "D:/Data/Shared");
        plan.BindVolumes.Should().OnlyContain(v => v.ContainerTargetPath == mount.ContainerSourcePath);
        plan.BindVolumes.Should().OnlyContain(v => v.ReadOnly == false);
        plan.SmbVolume.Should().BeNull();
    }

    [TestMethod]
    public void BuildComposeOverridePlan_Smb_ProducesInertVolumeShape()
    {
        var mountId = Guid.NewGuid();
        var mount = new HostFolderMount
        {
            Id = mountId,
            ProjectId = Guid.NewGuid(),
            Scope = HostFolderMountScope.Project,
            SourceKind = SourceKind.Smb,
            DisplayName = "Share",
            LeafName = "Share",
            MountKey = HostFolderMountKeyDeriver.DeriveMountKey(mountId),
            SourceSpec = @"\\server\share",
            ContainerSourcePath = "/app/HostMounts/share",
            NetworkDevice = "//server/share/folder",
            NetworkOptions = "uid=1000,gid=1000,file_mode=0664,dir_mode=0775,vers=3.0",
            CredentialRef = "SMB_CREDENTIALS_REF",
            CreatedByUserId = Guid.NewGuid()
        };

        var service = new HostFolderMountService(
            scopeFactory: null!,
            pathResolver: null!,
            Microsoft.Extensions.Options.Options.Create(new GuideAntsRuntimeOptions()),
            configuration: new ConfigurationBuilder().Build());

        var plan = service.BuildComposeOverridePlan(mount);

        plan.SourceKind.Should().Be(SourceKind.Smb);
        plan.BindVolumes.Should().BeNull();
        plan.SmbVolume.Should().NotBeNull();
        plan.SmbVolume!.Device.Should().Be("//server/share/folder");
        plan.SmbVolume.CredentialRef.Should().Be("SMB_CREDENTIALS_REF");
        plan.SmbVolume.DriverOptions.Should().Contain("${SMB_CREDENTIALS_REF}");
        plan.SmbVolume.DriverOptions.Should().NotContain("password");
    }
}
