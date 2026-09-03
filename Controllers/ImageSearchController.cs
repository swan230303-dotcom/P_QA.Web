using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using PQA.Web.ImageSearch;
using SixLabors.ImageSharp;

namespace PQA.Web.Controllers;

[ApiController]
[Route("api/image-search")]
public sealed class ImageSearchController(ImageSearchIndex index, IOptions<ImageSearchOptions> options) : ControllerBase
{
    private readonly ImageSearchOptions _options = options.Value;

    [HttpGet("status")]
    public ActionResult<ImageSearchStatus> Status() => Ok(index.Status);

    [HttpGet("folders")]
    public ActionResult<IReadOnlyList<ImageFolderInfo>> Folders()
    {
        EnsureEnabled();
        return Ok(index.GetFolders());
    }

    [HttpPost("index/rebuild")]
    public async Task<ActionResult<ImageSearchStatus>> Rebuild([FromBody] RebuildImageIndexRequest request, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await index.BuildAsync(request.Folder, cancellationToken);
        return Ok(index.Status);
    }

    [HttpPost("search")]
    [DisableRequestSizeLimit]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ImageSearchResponse>> Search([FromForm] IFormFile file, [FromQuery] int? topK, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (file is null || file.Length == 0) return BadRequest(new { error = "請上傳要比對的實物照片。" });
        if (file.Length > _options.MaxUploadBytes) return BadRequest(new { error = $"圖片不可超過 {_options.MaxUploadBytes / 1024 / 1024} MB。" });
        if (!_options.AllowedExtensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = "不支援此圖片格式。" });
        try
        {
            await using Stream stream = file.OpenReadStream();
            return Ok(await index.SearchAsync(stream, topK, cancellationToken));
        }
        catch (UnknownImageFormatException) { return BadRequest(new { error = "上傳內容不是有效的圖片檔。" }); }
        catch (InvalidImageContentException) { return BadRequest(new { error = "圖片內容已損毀或無法解碼。" }); }
    }

    [HttpGet("images/{imageId}")]
    public IActionResult Image(string imageId)
    {
        EnsureEnabled();
        (string path, string fileName) = index.ResolveImage(imageId);
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(path, out string? contentType)) contentType = "application/octet-stream";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private,max-age=3600";
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled) throw new InvalidOperationException("以圖搜圖功能尚未啟用，請設定 ImageSearch:Enabled=true。");
    }
}
