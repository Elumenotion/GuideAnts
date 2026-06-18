namespace GuideAntsApi.DataModel.Models;

public enum SourceKind
{
    LocalPath = 0,
    Smb = 1
}

public enum HostFolderMountScope
{
    Notebook = 0,
    Project = 1
}

public enum HostFolderMountStatus
{
    PendingRestart = 0,
    Active = 1,
    PendingRemoval = 2,
    Removed = 3,
    Error = 4
}

public enum HostFolderMountLinkStatus
{
    PendingRestart = 0,
    PendingLink = 1,
    Linked = 2,
    Unlinked = 3,
    LinkError = 4,
    UnlinkError = 5
}
