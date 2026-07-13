namespace ClothingRepacker.Core.Models;

public sealed record OperationProgress(
    string Operation,
    string Stage,
    int Current = 0,
    int Total = 0,
    string? Path = null,
    string? Message = null,
    int SourceCount = 0,
    int WarningCount = 0,
    int ErrorCount = 0,
    int TargetCount = 0,
    int WrittenFileCount = 0,
    int RenameCount = 0,
    int BackupCount = 0,
    int RemovedCount = 0,
    int SkippedCount = 0,
    string? MessageKey = null,
    IReadOnlyDictionary<string, object?>? MessageArguments = null);
