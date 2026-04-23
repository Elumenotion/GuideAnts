# GuideAntsApi.DataModel

This project contains the Entity Framework data models and DbContext for the GuideAnts API.

## Entity Relationship Diagram

The following diagram shows the relationships between the core entities in the data model.

```mermaid
erDiagram
    User {
        Guid Id PK
        string Name
        string Email
        DateTime Created
    }

    Project {
        Guid Id PK
        string Title
        string Description
        bool Deleted
        DateTime Created
    }

    ProjectRole {
        int Id PK
        string Name
    }

    ProjectUserRole {
        Guid Id PK
        Guid ProjectId FK
        Guid UserId FK
        int ProjectRoleId FK
        DateTime Created
    }

    ProjectFolder {
        Guid Id PK
        string Name
        string RelativePath
        Guid ProjectId FK
        Guid ParentFolderId FK
        DateTime Created
    }

    ContentFile {
        Guid Id PK
        string FileName
        string FilePath
        string RelativePath
        long FileSize
        string ContentType
        string DocumentId
        Guid ProjectId FK
        Guid FolderId FK
        int LatestVersion
        Guid SourceContentFileId FK
        Guid NotebookId FK
        DateTime Created
    }

    ContentFileVersion {
        Guid Id PK
        Guid ContentFileId FK
        int VersionNumber
        string FileName
        string ContentHash
        string StoragePath
        string OriginalRelativePath
        Guid OriginalFolderId FK
        long FileSize
        string ContentType
        bool Indexed
        Guid OriginVersionId FK
        Guid OriginNotebookFileId FK
        DateTime Created
    }

    Notebook {
        Guid Id PK
        string Title
        Guid ProjectId FK
        Guid NotebookTemplateId FK
        Guid SourceNotebookId FK
        Guid SourceConversationMessageId FK
        DateTime Created
    }

    NotebookFile {
        Guid Id PK
        Guid NotebookId FK
        string RelativePath
        long FileSize
        DateTime LastModifiedUtc
        string FileHash
        Guid OriginContentFileVersionId FK
        string DocumentId
        DateTime Created
    }

    NotebookTemplate {
        Guid Id PK
        string TemplateName
        DateTime Created
    }

    NotebookConversation {
        Guid Id PK
        Guid NotebookId FK
        string AssistantName
        string ModelDeploymentId
        DateTime Created
    }

    NotebookConversationMessage {
        Guid Id PK
        string Role
        string Content
        string ToolName
        Guid NotebookConversationId FK
        DateTime Created
    }

    FileLineageEvent {
        Guid Id PK
        string UserId
        string Action
        string FileKind
        Guid FileId
        int VersionNumber
        Guid ProjectId
        Guid NotebookId
        DateTime Timestamp
    }

    User ||--o{ ProjectUserRole : "has"
    Project ||--o{ ProjectUserRole : "has"
    ProjectRole ||--o{ ProjectUserRole : "has"

    Project ||--o{ Notebook : "contains"
    Project ||--o{ ContentFile : "contains"
    Project ||--o{ ProjectFolder : "contains"

    ProjectFolder }o--o{ ProjectFolder : "parent/sub-folders"
    ProjectFolder ||--o{ ContentFile : "contains"

    ContentFile ||--o{ ContentFileVersion : "versions"
    ContentFile }o--o{ ContentFile : "source"
    ContentFile }o--|| Notebook : "snapshot"
    
    ContentFileVersion }o--o{ ContentFileVersion : "origin"
    ContentFileVersion }o--|| NotebookFile : "origin"
    ContentFileVersion }o--|| ProjectFolder : "original folder"

    Notebook ||--o{ NotebookFile : "contains"
    Notebook ||--o{ NotebookConversation : "has one"
    Notebook }o--o{ Notebook : "source"
    Notebook }o--|| NotebookConversationMessage : "source"
    NotebookTemplate ||--o{ Notebook : "uses"

    NotebookConversation ||--o{ NotebookConversationMessage : "messages"
``` 