namespace Explorer.Core.Models;

public sealed record DriveInfoDto(
    string Name,
    string DisplayName,
    string DriveType,
    string Format,
    long? TotalBytes,
    long? FreeBytes,
    bool Ready,
    bool ReadOnly);

public sealed record KnownFolderDto(string Id, string Name, string Path, bool Available);

public sealed record RecycleBinEntryDto(string Id, string Name, string OriginalPath, long? Size, DateTimeOffset? DeletedAt, bool IsDirectory);

public sealed record FilePropertiesDto(
    string Path,
    string Name,
    bool IsDirectory,
    long? Size,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    DateTimeOffset Accessed,
    IReadOnlyList<string> Attributes,
    string? Owner,
    IReadOnlyList<string> Permissions,
    string? FileVersion,
    string? Company,
    string? OneDriveAvailability,
    string? HashAlgorithm = null,
    string? Hash = null,
    DateTimeOffset? PhotoTaken = null,
    string? ImageDimensions = null,
    string? CameraManufacturer = null,
    string? CameraModel = null,
    double? MediaDurationSeconds = null);

public sealed record TextDocumentDto(string Path, string Name, string Content, string ETag, DateTimeOffset Modified);

public sealed record TextSaveRequest(string Path, string Content, string ETag);

public sealed record UploadCreateRequest(string FileName, long Size, string Destination, string? ResumeId = null);

public sealed record UploadSessionDto(string Id, string FileName, string Destination, long Size, long Offset, string State, DateTimeOffset CreatedAt);

public sealed record UploadChunkDto(string Id, long Offset, bool Complete);

public sealed record UploadCommitRequest(string? ConflictMode = null, string? ExpectedSha256 = null);

public sealed record UploadCommitDto(bool Completed, bool Skipped, string? Path, string Sha256);

public sealed record FileEntryDto(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTimeOffset Modified,
    DateTimeOffset Created,
    string Extension,
    string ContentType,
    bool IsHidden,
    bool IsReadOnly,
    bool IsLink,
    bool IsShortcut = false,
    bool? SizeIncomplete = null);

public sealed record ShortcutTargetDto(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTimeOffset Modified,
    DateTimeOffset Created,
    string Extension,
    string ContentType,
    bool IsHidden,
    bool IsReadOnly,
    bool IsLink,
    bool IsShortcut = false);

public sealed record FolderPageDto(
    string Path,
    IReadOnlyList<FileEntryDto> Entries,
    int Skip,
    int PageSize,
    int? TotalCount,
    bool HasMore);

public sealed record ApiErrorDto(string Message, string? Code = null);

public sealed record OperationRequest(
    string Kind,
    IReadOnlyList<string> Sources,
    string? Destination = null,
    string? NewName = null,
    bool Permanent = false,
    string? ConflictMode = null,
    IReadOnlyList<OperationUndoAction>? UndoActions = null,
    string? Template = null);

public sealed record OperationUndoAction(string Kind, string SourcePath, string TargetPath);

public sealed record JobSnapshot(
    string Id,
    string Kind,
    string Status,
    string Message,
    long CompletedBytes,
    long? TotalBytes,
    int CompletedItems,
    int? TotalItems,
    double? BytesPerSecond,
    DateTimeOffset CreatedAt,
    string? Error = null,
    string? Operation = null,
    ConflictPrompt? Conflict = null,
    double? RemainingSeconds = null,
    int SkippedItems = 0);

public sealed record ConflictPrompt(string SourceName, string DestinationName, string DestinationPath);

public sealed record ConflictResolution(string Choice, bool ApplyToAll = false);
