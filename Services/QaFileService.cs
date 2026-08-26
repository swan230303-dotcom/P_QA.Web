using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using PQA.Web.Models;

namespace PQA.Web.Services;

public sealed class QaFileService : IDisposable
{
    private static readonly HashSet<string> PreviewableImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };
    private readonly string root;
    private readonly bool connected;
    private readonly bool ownsConnection;
    private readonly FileExtensionContentTypeProvider contentTypes = new();

    public QaFileService(IConfiguration configuration, SecureSettingsProvider settings)
    {
        root = configuration["AttachmentRoot"] ?? throw new InvalidOperationException("未設定 AttachmentRoot。");
        string? user = settings.TryGet("QaFileUser");
        string? password = settings.TryGet("QaFilePassword");
        ownsConnection = !string.IsNullOrWhiteSpace(user);
        connected = ownsConnection ? Connect(root, user!, password ?? "") : Directory.Exists(root);
    }

    public async Task<string> SaveAsync(WorkflowDefinition definition, IFormFile file, CancellationToken ct)
    {
        EnsureAvailable(definition);
        if (file.Length <= 0) throw new InvalidDataException("附件不可為空白。");
        if (file.Length > 100L * 1024 * 1024) throw new InvalidDataException("單一附件不可超過 100 MB。");
        string original = Path.GetFileName(file.FileName);
        string safe = string.Concat(original.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safe)) throw new InvalidDataException("附件檔名無效。");
        string folder = Folder(definition);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, safe);
        if (File.Exists(target)) target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(safe)}_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(safe)}");
        await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await file.CopyToAsync(output, ct);
        return Path.GetFileName(target);
    }

    public string GetPath(WorkflowDefinition definition, string fileName)
    {
        EnsureAvailable(definition);
        string safe = Path.GetFileName(fileName);
        if (!safe.Equals(fileName, StringComparison.Ordinal)) throw new UnauthorizedAccessException("附件路徑不合法。");
        string path = Path.Combine(Folder(definition), safe);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到附件：{safe}");
        return path;
    }

    public void Delete(WorkflowDefinition definition, string fileName)
    {
        string path = GetPath(definition, fileName);
        File.Delete(path);
    }

    public string ContentType(string path) => contentTypes.TryGetContentType(path, out string? value) ? value : "application/octet-stream";
    public bool IsPreviewableImage(string path) => PreviewableImageExtensions.Contains(Path.GetExtension(path));
    private string Folder(WorkflowDefinition definition) => Path.Combine(root, definition.AttachmentFolder ?? throw new InvalidOperationException("此模組沒有附件資料夾。"));
    private void EnsureAvailable(WorkflowDefinition definition)
    {
        if (definition.AttachmentTable is null) throw new InvalidOperationException("此模組不支援附件。");
        if (!connected || !Directory.Exists(root)) throw new UnauthorizedAccessException("Web 伺服器無法存取品保附件分享區，請設定 IIS 執行帳號或 QA NAS 帳密。");
    }

    private static bool Connect(string remotePath, string user, string password)
    {
        var resource = new NetResource { Scope = 2, ResourceType = 1, DisplayType = 3, RemoteName = remotePath };
        int result = WNetAddConnection2(ref resource, password, user, 0);
        if (result != 0 && result != 1219) throw new Win32Exception(result, "無法連接品保附件分享區。");
        return Directory.Exists(remotePath);
    }

    public void Dispose() { if (ownsConnection && connected) WNetCancelConnection2(root, 0, false); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource { public int Scope, ResourceType, DisplayType, Usage; public string? LocalName, RemoteName, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string password, string userName, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
