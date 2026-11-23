namespace PersonalServerManager.Stash;

public sealed class StashJobOptionNotSupportedException(string message) : InvalidOperationException(message);

public sealed class StashJobSettings
{
    public StashScanOptions Scan { get; set; } = new();
    public StashGenerateOptions Generate { get; set; } = new();
}

public sealed class StashScanOptions
{
    public string[]? Paths { get; set; }
    public bool Rescan { get; set; }
    public bool ScanGenerateCovers { get; set; }
    public bool ScanGeneratePreviews { get; set; }
    public bool ScanGenerateImagePreviews { get; set; }
    public bool ScanGenerateSprites { get; set; }
    public bool ScanGeneratePhashes { get; set; }
    public bool ScanGenerateImagePhashes { get; set; }
    public bool ScanGenerateThumbnails { get; set; }
    public bool ScanGenerateClipPreviews { get; set; }
    public StashScanFilter? Filter { get; set; }
}

public sealed class StashScanFilter
{
    public string? MinModTime { get; set; }
}

public sealed class StashGenerateOptions
{
    public bool Covers { get; set; }
    public bool Sprites { get; set; }
    public bool Previews { get; set; }
    public bool ImagePreviews { get; set; }
    public StashPreviewOptions? PreviewOptions { get; set; }
    public bool Markers { get; set; }
    public bool MarkerImagePreviews { get; set; }
    public bool MarkerScreenshots { get; set; }
    public bool Transcodes { get; set; }
    public bool ForceTranscodes { get; set; }
    public bool Phashes { get; set; }
    public bool InteractiveHeatmapsSpeeds { get; set; }
    public bool ImagePhashes { get; set; }
    public bool ImageThumbnails { get; set; }
    public bool ClipPreviews { get; set; }
    public string[]? SceneIDs { get; set; }
    public string[]? MarkerIDs { get; set; }
    public string[]? ImageIDs { get; set; }
    public string[]? GalleryIDs { get; set; }
    public string[]? Paths { get; set; }
    public bool Overwrite { get; set; }
}

public sealed class StashPreviewOptions
{
    public int? PreviewSegments { get; set; }
    public double? PreviewSegmentDuration { get; set; }
    public string? PreviewExcludeStart { get; set; }
    public string? PreviewExcludeEnd { get; set; }
    public string? PreviewPreset { get; set; }
}