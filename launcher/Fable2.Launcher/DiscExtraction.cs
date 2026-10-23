using System.IO;
using System.Security.Cryptography;
using X360Extract;

namespace Fable2Launcher;

/// <summary>
/// One top-level item on the extraction checklist: a root file (e.g. default.xex,
/// nxeart) or a root folder (e.g. data, $SystemUpdate).
/// </summary>
public sealed class RootItem
{
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Bytes { get; }

    /// <summary>Filesystem-safe name, used to match per-file progress events.</summary>
    public string Key { get; }

    public RootItem(string name, bool isDirectory, long bytes)
    {
        Name = name;
        IsDirectory = isDirectory;
        Bytes = bytes;
        Key = X360Disc.SanitizeFileName(name);
    }
}

/// <summary>
/// Thrown from the extraction callbacks when the user stops an in-progress
/// extraction. Caught by the launcher and treated as a clean stop, not an error.
/// </summary>
public sealed class ExtractionCancelledException : Exception
{
    public ExtractionCancelledException() : base("Извлечение остановлено.") { }
}

/// <summary>
/// Structured progress for a full extraction (both the hash-gated default.xex and
/// the remaining disc). Byte-accurate: the percentage is bytes written / total
/// bytes, so it advances smoothly through every file including large ones.
///
/// All members are written on the extraction thread and read by the UI thread via
/// the <see cref="Changed"/> event; the values are only ever displayed, so the
/// benign read/write race on a progress bar is acceptable (no locks needed).
/// </summary>
public sealed class ExtractionProgress
{
    private readonly Dictionary<string, RootItem> _roots;
    private readonly Dictionary<string, long> _rootDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootFired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Total number of bytes to write (sum of every root item). Set once.</summary>
    public long TotalBytes { get; }

    /// <summary>Bytes fully written (completed files) so far.</summary>
    public long CompletedBytes { get; private set; }

    /// <summary>Relative path of the file currently being written, if any.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>Bytes written so far in the current file.</summary>
    public long CurrentFileWritten { get; private set; }

    /// <summary>Declared size of the current file.</summary>
    public long CurrentFileSize { get; private set; }

    /// <summary>Human-readable stage, e.g. "Extracting data/audio".</summary>
    public string Phase { get; private set; } = "Запуск…";

    /// <summary>Percentage 0..100 across the whole extraction.</summary>
    public double Percent
    {
        get
        {
            if (TotalBytes <= 0) return 0.0;
            long done = CompletedBytes + CurrentFileWritten;
            return Math.Clamp((double)done / (double)TotalBytes * 100.0, 0.0, 100.0);
        }
    }

    /// <summary>Raised whenever the state changes; marshaled to the UI by the consumer.</summary>
    public event Action<ExtractionProgress>? Changed;

    /// <summary>Raised (on the extraction thread) when a root item finishes, by name.</summary>
    public event Action<string>? RootCompleted;

    private readonly Func<bool> _isCancelled;

    public ExtractionProgress(IReadOnlyList<RootItem> plan, Func<bool>? isCancelled = null)
    {
        _roots = new Dictionary<string, RootItem>(plan.Count, StringComparer.OrdinalIgnoreCase);
        foreach (RootItem item in plan)
            _roots[item.Key] = item;
        TotalBytes = plan.Sum(item => item.Bytes);
        _isCancelled = isCancelled ?? (() => false);
    }

    /// <summary>
    /// Update the in-flight file position. Call per chunk. Throws
    /// <see cref="ExtractionCancelledException"/> once the user requests a stop,
    /// which unwinds the extraction loop on the worker thread.
    /// </summary>
    public void OnFileChunk(string rel, long written, long size)
    {
        ThrowIfCancelled();
        CurrentFile = rel;
        CurrentFileWritten = written;
        CurrentFileSize = size;
        Changed?.Invoke(this);
    }

    /// <summary>
    /// Mark a file as fully written. Advances the overall counter and fires
    /// <see cref="RootCompleted"/> once its top-level item has fully landed.
    /// </summary>
    public void OnFileCompleted(string rel, long size)
    {
        ThrowIfCancelled();
        CompletedBytes += size;
        CurrentFileWritten = 0;

        string key = RootKeyOf(rel);
        long done = _rootDone.GetValueOrDefault(key) + size;
        _rootDone[key] = done;
        if (_roots.TryGetValue(key, out RootItem? item) && item is not null && done >= item.Bytes)
            FireRootDone(item);

        Changed?.Invoke(this);
    }

    public void SetPhase(string phase)
    {
        Phase = phase;
        Changed?.Invoke(this);
    }

    private void ThrowIfCancelled()
    {
        if (_isCancelled())
            throw new ExtractionCancelledException();
    }

    private void FireRootDone(RootItem item)
    {
        if (_rootFired.Add(item.Key))
            RootCompleted?.Invoke(item.Name);
    }

    private static string RootKeyOf(string rel)
    {
        int slash = rel.IndexOf('/');
        return slash < 0 ? rel : rel[..slash];
    }
}

/// <summary>
/// Extracts Fable II content from a raw Xbox 360 disc image (GDFX). The low-level
/// reading/extraction is delegated to the reusable <see cref="X360Disc"/> facade in
/// the X360Extract library (tools/x360extract/lib), which is shared with the
/// standalone x360extract tool.
///
/// Extraction is hash-gated: default.xex is extracted and SHA-256-hashed first, and
/// the remaining disc content is written only once the hash matches a known-good
/// version (see GameCompatibilityInspector.CheckHash). A disc this project does not
/// support costs the user one ~21 MB file (deleted on failure) instead of a
/// ~6.3 GB extraction.
///
/// Files are written byte-for-byte with FileMode.Create, matching the standalone
/// tool: re-running over an existing output tree overwrites each file but does not
/// delete files that are no longer in the disc tree.
/// </summary>
public sealed class DiscExtraction : IDisposable
{
    private readonly X360Disc _disc;

    private DiscExtraction(X360Disc disc) => _disc = disc;

    public string IsoPath => _disc.ImagePath;
    public GdfxFilesystem Gdfx => _disc.Gdfx;
    public IReadOnlyList<GdfxEntry> RootEntries => _disc.RootEntries;

    /// <summary>The root-level default.xex entry, if this disc has one.</summary>
    public GdfxEntry? DefaultXex => _disc.DefaultXex;

    /// <summary>The top-level extraction checklist: every root file/folder with its byte size.</summary>
    public IReadOnlyList<RootItem> Plan
    {
        get
        {
            List<RootItem> plan = new(RootEntries.Count);
            foreach (GdfxEntry entry in RootEntries)
                plan.Add(new RootItem(entry.Name, entry.IsDirectory, SubtreeBytes(entry)));
            return plan;
        }
    }

    /// <summary>Total bytes across the whole disc (default.xex + everything else).</summary>
    public long TotalBytes => Plan.Sum(item => item.Bytes);

    /// <summary>
    /// Scans the image for the GDFX header and reads the root directory. The caller
    /// must Dispose the result.
    /// </summary>
    public static DiscExtraction Open(string isoPath, Action<string>? status = null)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException("ISO-образ не найден.", isoPath);

        status?.Invoke("Поиск заголовка GDFX на диске…");
        X360Disc disc = X360Disc.Open(isoPath, p =>
        {
            if (p.Phase == DiscProgressPhase.ScanningHeader)
                status?.Invoke("Поиск заголовка GDFX на диске…");
        });
        return new DiscExtraction(disc);
    }

    /// <summary>
    /// Writes the disc's root-level default.xex to <paramref name="outDir"/> and
    /// returns its SHA-256 as lower-case hex. Throws if the disc has no root-level
    /// default.xex. When <paramref name="progress"/> is given, the extraction feeds
    /// it per-chunk.
    /// </summary>
    /// <param name="xexFileName">
    /// The file name to write the XEX under (default <c>default.xex</c>). Passing a
    /// different name (e.g. <c>default_new.xex</c>) lets a caller hash-gate the XEX
    /// without clobbering an existing default.xex in the output folder.
    /// </param>
    public string ExtractXex(string outDir, Action<string>? status = null,
        ExtractionProgress? progress = null, string xexFileName = "default.xex")
    {
        GdfxEntry entry = DefaultXex
            ?? throw new InvalidDataException("В корне этого ISO-образа не найден default.xex.");

        status?.Invoke($"Извлечение default.xex ({FormatSize(entry.Size)})…");
        progress?.SetPhase("Извлечение default.xex");
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _disc.WriteFile(entry, Path.Combine(outDir, xexFileName),
            onBytes: (b, off, cnt) => sha.AppendData(b, off, cnt),
            onChunk: (written, total) => progress?.OnFileChunk("default.xex", written, total));
        progress?.OnFileCompleted("default.xex", entry.Size);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts every root entry except default.xex. STFS containers (e.g. nxeart)
    /// are copied raw, matching the default behavior of x360extract. Returns the
    /// number of files and total bytes written. When <paramref name="progress"/> is
    /// given, per-file and per-chunk progress are reported through it.
    /// </summary>
    public (int Files, long Bytes) ExtractRemaining(string outDir, Action<string>? status = null,
        ExtractionProgress? progress = null)
    {
        bool isXex(GdfxEntry entry) => !entry.IsDirectory &&
            string.Equals(entry.Name, "default.xex", StringComparison.OrdinalIgnoreCase);
        List<GdfxEntry> remaining = RootEntries.Where(entry => !isXex(entry)).ToList();

        ExtractionOptions options = new()
        {
            UnpackStfs = false,
            Progress = p =>
            {
                switch (p.Phase)
                {
                    case DiscProgressPhase.EnteringDirectory:
                        status?.Invoke($"Извлечение {p.Name}/ (файлов: {p.FileCount})…");
                        progress?.SetPhase($"Извлечение {p.Name}");
                        break;
                    case DiscProgressPhase.FileWritten:
                        progress?.OnFileCompleted(p.Name!, p.SizeBytes);
                        break;
                }
            },
            FileChunk = (rel, written, total) => progress?.OnFileChunk(rel, written, total),
        };

        ExtractionResult result = _disc.ExtractEntries(remaining, outDir, options);
        return (result.Files, result.Bytes);
    }

    private long SubtreeBytes(GdfxEntry entry)
    {
        if (!entry.IsDirectory) return entry.Size;
        long total = 0;
        foreach (GdfxEntry child in Gdfx.ReadDirectory(entry.Sector, entry.Size))
            total += SubtreeBytes(child);
        return total;
    }

    public void Dispose() => _disc.Dispose();

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024L
            ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
            : $"{bytes / 1024.0:0.#} KB";
}
