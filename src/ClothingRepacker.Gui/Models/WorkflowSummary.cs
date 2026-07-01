namespace ClothingRepacker.Gui.Models;

public sealed record WorkflowSummary(
    int SourceYmtCount,
    int TargetCollectionCount,
    int StreamRenameCount,
    int WarningCount,
    int ErrorCount,
    int WrittenFileCount = 0,
    int SkippedFileCount = 0,
    int BackupEntryCount = 0);
