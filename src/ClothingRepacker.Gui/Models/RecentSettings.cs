namespace ClothingRepacker.Gui.Models;

public sealed class RecentSettings
{
    public string ResourcesPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;
    public string PlanPath { get; set; } = string.Empty;
    public string TargetResource { get; set; } = "zz_merged_clothing_meta";
    public string TargetPrefix { get; set; } = "merged";
    public string FemalePrefix { get; set; } = "merged_f";
    public string MalePrefix { get; set; } = "merged_m";
    public int MaxDrawablesPerComponent { get; set; } = ClothingRepacker.Core.ClothingConstants.DefaultMaxDrawablesPerComponent;
    public int MaxDrawablesPerProp { get; set; } = ClothingRepacker.Core.ClothingConstants.DefaultMaxDrawablesPerProp;
    public bool IncludeYmtXml { get; set; } = true;
    public bool IncludeDebugClient { get; set; } = true;
    public bool OverwriteXml { get; set; }
    public bool SavePlan { get; set; } = true;
}
