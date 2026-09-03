using System.ComponentModel.DataAnnotations;

namespace PQA.Web.ImageSearch;

public sealed class ImageSearchOptions
{
    public const string SectionName = "ImageSearch";
    public bool Enabled { get; set; }
    [Required] public string ModelPath { get; set; } = "OnnxModels/vision_model.onnx";
    public string LibraryRoot { get; set; } = "";
    public string DefaultFolder { get; set; } = "";
    public string VectorCachePath { get; set; } = "image-search-cache/vectors.bin";
    [Required] public string InputName { get; set; } = "pixel_values";
    [Required] public string OutputName { get; set; } = "image_embeds";
    [Range(1, 4096)] public int InputWidth { get; set; } = 224;
    [Range(1, 4096)] public int InputHeight { get; set; } = 224;
    // 保持空陣列，避免 .NET 10 ConfigurationBinder 把 JSON 陣列附加到預設值而產生重複項目。
    public float[] Mean { get; set; } = [];
    public float[] Std { get; set; } = [];
    [Range(1, 100)] public int DefaultTopK { get; set; } = 12;
    [Range(1, 500)] public int MaxTopK { get; set; } = 50;
    [Range(1024, 200_000_000)] public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;
    public int IndexDegreeOfParallelism { get; set; }
    public int SearchDegreeOfParallelism { get; set; }
    public string[] AllowedExtensions { get; set; } = [];
}

public sealed record RebuildImageIndexRequest(string? Folder);
public sealed record ImageFolderInfo(string Value, string Name);
public sealed record ImageSearchHit(string ImageId, string FileName, string RelativePath, float Similarity, string ImageUrl);
public sealed record ImageSearchResponse(int IndexedImages, string Folder, double ElapsedMilliseconds, IReadOnlyList<ImageSearchHit> Results);
public sealed record ImageSearchStatus(bool Enabled, bool Ready, bool IsBuilding, int ImageCount, int VectorDimension,
    string Folder, DateTimeOffset? LoadedAt, string? Error, int ProcessedImages, int TotalImages);
