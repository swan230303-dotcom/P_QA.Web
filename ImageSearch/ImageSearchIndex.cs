using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PQA.Web.ImageSearch;

public sealed class ImageSearchIndex
{
    private const string CacheMagic = "PQA-IMAGE-V1";
    private readonly ImageSearchOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly OnnxImageEncoder _encoder;
    private readonly ILogger<ImageSearchIndex> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private Snapshot _snapshot = Snapshot.Empty;
    private volatile bool _isBuilding;
    private volatile int _processed;
    private volatile int _total;
    private string? _error;

    public ImageSearchIndex(IOptions<ImageSearchOptions> options, IWebHostEnvironment environment,
        OnnxImageEncoder encoder, ILogger<ImageSearchIndex> logger)
    {
        _options = options.Value;
        _environment = environment;
        _encoder = encoder;
        _logger = logger;
    }

    public ImageSearchStatus Status
    {
        get
        {
            Snapshot snapshot = Volatile.Read(ref _snapshot);
            return new(_options.Enabled, snapshot.Entries.Length > 0, _isBuilding, snapshot.Entries.Length,
                snapshot.Dimension, snapshot.Folder, snapshot.LoadedAt, _error, _processed, _total);
        }
    }

    public IReadOnlyList<ImageFolderInfo> GetFolders()
    {
        string root = GetLibraryRoot();
        var folders = new List<ImageFolderInfo> { new("", "圖庫根目錄") };
        folders.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new ImageFolderInfo(path.Replace('\\', '/'), path)));
        return folders;
    }

    public async Task BuildAsync(string? folder, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        await _buildGate.WaitAsync(cancellationToken);
        try
        {
            _isBuilding = true; _processed = 0; _total = 0; _error = null;
            string selectedFolder = NormalizeFolder(folder ?? _options.DefaultFolder);
            string absoluteFolder = ResolveSelectedFolder(selectedFolder);
            string[] files = Directory.EnumerateFiles(absoluteFolder, "*", SearchOption.AllDirectories)
                .Where(IsAllowed).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("選取的圖庫資料夾沒有可索引的圖片。");
            _total = files.Length;

            Dictionary<string, CacheEntry> cached = ReadCache(absoluteFolder);
            var entries = new ConcurrentBag<ImageEntry>();
            int degree = Degree(_options.IndexDegreeOfParallelism);
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
                async (path, ct) =>
                {
                    string relative = Path.GetRelativePath(absoluteFolder, path).Replace('\\', '/');
                    var info = new FileInfo(path);
                    float[] vector;
                    if (cached.TryGetValue(relative, out CacheEntry? item) && item.Length == info.Length && item.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                        vector = item.Vector;
                    else
                    {
                        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        vector = await _encoder.EncodeAsync(stream, ct);
                    }
                    entries.Add(new(CreateId(relative), relative, path, info.Length, info.LastWriteTimeUtc.Ticks, vector));
                    Interlocked.Increment(ref _processed);
                });

            ImageEntry[] ordered = entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            int dimension = ordered[0].Vector.Length;
            if (ordered.Any(x => x.Vector.Length != dimension)) throw new InvalidDataException("ONNX 輸出向量維度不一致，索引已取消。");
            var byId = ordered.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var snapshot = new Snapshot(ordered, byId, dimension, selectedFolder, DateTimeOffset.Now);
            Volatile.Write(ref _snapshot, snapshot);
            WriteCache(absoluteFolder, ordered);
            _logger.LogInformation("以圖搜圖已載入 {Count} 張圖片、{Dimension} 維向量，資料夾 {Folder}", ordered.Length, dimension, absoluteFolder);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _logger.LogError(ex, "建立以圖搜圖索引失敗");
            throw;
        }
        finally { _isBuilding = false; _buildGate.Release(); }
    }

    public async Task<ImageSearchResponse> SearchAsync(Stream uploadedImage, int? requestedTopK, CancellationToken cancellationToken)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Entries.Length == 0) throw new InvalidOperationException("圖片索引尚未就緒，請先完成圖庫載入。");
        var watch = Stopwatch.StartNew();
        float[] query = await _encoder.EncodeAsync(uploadedImage, cancellationToken);
        if (query.Length != snapshot.Dimension) throw new InvalidDataException("查詢圖片向量維度與圖庫不一致。");
        int topK = Math.Clamp(requestedTopK ?? _options.DefaultTopK, 1, _options.MaxTopK);
        int degree = Degree(_options.SearchDegreeOfParallelism);
        ImageSearchHit[] hits = snapshot.Entries.AsParallel().WithDegreeOfParallelism(degree)
            .Select(entry => new ImageSearchHit(entry.Id, Path.GetFileName(entry.RelativePath), entry.RelativePath,
                Dot(query, entry.Vector), $"/api/image-search/images/{entry.Id}"))
            .OrderByDescending(x => x.Similarity).Take(topK).ToArray();
        watch.Stop();
        return new(snapshot.Entries.Length, snapshot.Folder, watch.Elapsed.TotalMilliseconds, hits);
    }

    public (string Path, string FileName) ResolveImage(string id)
    {
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.ById.TryGetValue(id, out ImageEntry? entry)) throw new KeyNotFoundException("找不到圖庫圖片。");
        return (entry.AbsolutePath, Path.GetFileName(entry.RelativePath));
    }

    private string GetLibraryRoot()
    {
        if (string.IsNullOrWhiteSpace(_options.LibraryRoot)) throw new InvalidOperationException("尚未設定 ImageSearch:LibraryRoot。");
        string root = Resolve(_options.LibraryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到圖片圖庫根目錄：{root}");
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private string ResolveSelectedFolder(string folder)
    {
        string root = GetLibraryRoot();
        string candidate = Path.GetFullPath(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("選取的圖片資料夾超出允許的圖庫根目錄。");
        if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException("找不到選取的圖片資料夾。");
        return candidate;
    }

    private static string NormalizeFolder(string? folder)
    {
        folder = (folder ?? "").Trim().Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(folder) || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(x => x == ".."))
            throw new UnauthorizedAccessException("資料夾必須是圖庫根目錄下的相對路徑。");
        return folder;
    }

    private bool IsAllowed(string path) => _options.AllowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path));
    private static int Degree(int configured) => Math.Clamp(configured > 0 ? configured : Environment.ProcessorCount, 1, 512);
    private static string CreateId(string relative) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..24];
    private static float Dot(float[] left, float[] right) { float sum = 0; for (int i = 0; i < left.Length; i++) sum += left[i] * right[i]; return sum; }

    private string CachePath => Resolve(_options.VectorCachePath);
    private string CacheKey(string folder)
    {
        string model = Resolve(_options.ModelPath);
        var info = new FileInfo(model);
        return $"{folder}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{_options.InputName}|{_options.OutputName}|{_options.InputWidth}x{_options.InputHeight}|{string.Join(',', _options.Mean)}|{string.Join(',', _options.Std)}";
    }

    private Dictionary<string, CacheEntry> ReadCache(string folder)
    {
        var result = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CachePath)) return result;
            using var reader = new BinaryReader(File.OpenRead(CachePath), Encoding.UTF8);
            if (reader.ReadString() != CacheMagic || reader.ReadString() != CacheKey(folder)) return result;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString(); long length = reader.ReadInt64(); long ticks = reader.ReadInt64(); int dimension = reader.ReadInt32();
                var vector = new float[dimension]; for (int j = 0; j < dimension; j++) vector[j] = reader.ReadSingle();
                result[path] = new(length, ticks, vector);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "向量快取無法讀取，將重新建立"); result.Clear(); }
        return result;
    }

    private void WriteCache(string folder, ImageEntry[] entries)
    {
        try
        {
            string path = CachePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temporary = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary), Encoding.UTF8))
            {
                writer.Write(CacheMagic); writer.Write(CacheKey(folder)); writer.Write(entries.Length);
                foreach (ImageEntry entry in entries)
                {
                    writer.Write(entry.RelativePath); writer.Write(entry.Length); writer.Write(entry.LastWriteTicks); writer.Write(entry.Vector.Length);
                    foreach (float value in entry.Vector) writer.Write(value);
                }
            }
            File.Move(temporary, path, true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "向量快取無法寫入；記憶體索引仍可正常使用"); }
    }

    private sealed record ImageEntry(string Id, string RelativePath, string AbsolutePath, long Length, long LastWriteTicks, float[] Vector);
    private sealed record CacheEntry(long Length, long LastWriteTicks, float[] Vector);
    private sealed record Snapshot(ImageEntry[] Entries, IReadOnlyDictionary<string, ImageEntry> ById, int Dimension, string Folder, DateTimeOffset? LoadedAt)
    {
        public static Snapshot Empty { get; } = new([], new Dictionary<string, ImageEntry>(), 0, "", null);
    }
}

public sealed class ImageSearchWarmupService(ImageSearchIndex index, IOptions<ImageSearchOptions> options,
    ILogger<ImageSearchWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓 Kestrel/IIS 先完成啟動；NAS 回應緩慢時不應阻斷登入及既有案件功能。
        await Task.Yield();
        if (!options.Value.Enabled) { logger.LogInformation("以圖搜圖未啟用"); return; }
        try
        {
            await index.BuildAsync(options.Value.DefaultFolder, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關閉網站。
        }
        catch (Exception ex)
        {
            // 圖庫或模型暫時無法存取時，保留案件登入及既有流程功能；錯誤會由狀態 API 顯示。
            logger.LogError(ex, "以圖搜圖啟動載入失敗，網站仍會啟動；請檢查模型、圖庫路徑及 IIS 權限");
        }
    }
}
