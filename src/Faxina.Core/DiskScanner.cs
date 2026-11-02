using System.Collections.Concurrent;

namespace Faxina.Core;

public sealed class DirNode
{
    public required string Name;
    public required string Path;
    public DirNode? Parent;
    public long Size;          // total da subárvore (bytes no disco, sem placeholders da nuvem)
    public long FilesSize;     // só arquivos diretamente nesta pasta
    public long FileCount;     // total da subárvore
    public long DirCount;      // total da subárvore
    public DateTime Modified;
    public bool IsLink;        // junção/symlink: não seguimos
    public bool IsMount;       // outro sistema de arquivos montado aqui (Linux): não atravessamos
    public List<DirNode> Children = new();
}

public sealed record BigFile(string Path, long Size, DateTime Modified);

/// <summary>Varre uma pasta em paralelo montando a árvore de tamanhos, os maiores arquivos e o total por tipo.</summary>
public sealed class DiskScanner
{
    // Arquivos do OneDrive/Files On-Demand que existem só na nuvem não ocupam disco.
    private const FileAttributes CloudOnly = (FileAttributes)0x400000 /* RecallOnDataAccess */
                                            | (FileAttributes)0x40000 /* RecallOnOpen */
                                            | FileAttributes.Offline;

    private static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private const int TopN = 300;
    private readonly PriorityQueue<BigFile, long> _top = new();
    private long _topMin;
    private readonly CancellationToken _ct;
    private readonly IReadOnlySet<string> _excludes;

    public long FilesScanned;
    public long BytesScanned;
    public long CloudBytes;
    public long Inaccessible;
    public volatile string CurrentPath = "";
    public readonly ConcurrentDictionary<string, long> BytesByExt = new(StringComparer.OrdinalIgnoreCase);

    public DirNode? Root { get; private set; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime? FinishedAt { get; private set; }

    public DiskScanner(CancellationToken ct, IReadOnlySet<string>? excludes = null)
    {
        _ct = ct;
        _excludes = excludes ?? new HashSet<string>();
    }

    public DirNode Scan(string rootPath)
    {
        var di = new DirectoryInfo(rootPath);
        var name = di.Parent == null ? di.FullName : di.Name;
        Root = ScanDir(di, name, null, 0);
        FinishedAt = DateTime.Now;
        return Root;
    }

    private DirNode ScanDir(DirectoryInfo di, string name, DirNode? parent, int depth)
    {
        _ct.ThrowIfCancellationRequested();
        var node = new DirNode { Name = name, Path = di.FullName, Parent = parent };
        try { node.Modified = di.LastWriteTime; } catch { }
        CurrentPath = di.FullName;

        var subdirs = new List<DirectoryInfo>();
        try
        {
            foreach (var fsi in di.EnumerateFileSystemInfos("*", Opts))
            {
                if (fsi is FileInfo fi)
                {
                    long len;
                    FileAttributes attrs;
                    try { len = fi.Length; attrs = fi.Attributes; } catch { continue; }
                    if (OperatingSystem.IsWindows())
                    {
                        if ((attrs & CloudOnly) != 0) { Interlocked.Add(ref CloudBytes, len); continue; }
                    }
                    else if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue; // symlink: o alvo é contado onde estiver
                    node.FilesSize += len;
                    node.FileCount++;
                    Interlocked.Increment(ref FilesScanned);
                    Interlocked.Add(ref BytesScanned, len);
                    BytesByExt.AddOrUpdate(fi.Extension, len, (_, v) => v + len);
                    if (len > Volatile.Read(ref _topMin)) TrackBig(fi, len);
                }
                else if (fsi is DirectoryInfo sd)
                {
                    if (_excludes.Contains(sd.FullName))
                    {
                        node.Children.Add(new DirNode { Name = sd.Name, Path = sd.FullName, Parent = node, IsMount = true });
                        continue;
                    }
                    if (sd.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Junções e symlinks apontam para outro lugar (contaria em dobro). A raiz do OneDrive
                        // também é reparse point, mas sem LinkTarget — essa a gente percorre.
                        string? target = null;
                        try { target = sd.LinkTarget; } catch { }
                        if (target != null)
                        {
                            node.Children.Add(new DirNode { Name = sd.Name, Path = sd.FullName, Parent = node, IsLink = true });
                            continue;
                        }
                    }
                    subdirs.Add(sd);
                }
            }
        }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref Inaccessible); }
        catch (IOException) { Interlocked.Increment(ref Inaccessible); }

        var children = new DirNode[subdirs.Count];
        if (depth < 3 && subdirs.Count > 1)
        {
            Parallel.For(0, subdirs.Count,
                new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                i => children[i] = ScanDir(subdirs[i], subdirs[i].Name, node, depth + 1));
        }
        else
        {
            for (var i = 0; i < subdirs.Count; i++) children[i] = ScanDir(subdirs[i], subdirs[i].Name, node, depth + 1);
        }

        node.Children.AddRange(children);
        node.Size = node.FilesSize;
        foreach (var c in children)
        {
            node.Size += c.Size;
            node.FileCount += c.FileCount;
            node.DirCount += c.DirCount + 1;
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (node.Children.Capacity > node.Children.Count * 2) node.Children.TrimExcess();
        return node;
    }

    private void TrackBig(FileInfo fi, long len)
    {
        lock (_top)
        {
            if (_top.Count >= TopN && len <= _topMin) return;
            DateTime mod;
            try { mod = fi.LastWriteTime; } catch { mod = default; }
            _top.Enqueue(new BigFile(fi.FullName, len, mod), len);
            if (_top.Count > TopN) _top.Dequeue();
            if (_top.Count >= TopN) Volatile.Write(ref _topMin, _top.TryPeek(out _, out var min) ? min : 0);
        }
    }

    public List<BigFile> TopFiles()
    {
        lock (_top) return _top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
    }

    public DirNode? Find(string path)
    {
        var node = Root;
        if (node == null) return null;
        path = Fs.TrimSep(path);
        var sep = System.IO.Path.DirectorySeparatorChar;
        while (node != null)
        {
            if (string.Equals(Fs.TrimSep(node.Path), path, Fs.PathComparison)) return node;
            node = node.Children.FirstOrDefault(c =>
                path.StartsWith(Fs.TrimSep(c.Path) + sep, Fs.PathComparison) ||
                string.Equals(Fs.TrimSep(c.Path), path, Fs.PathComparison));
        }
        return null;
    }

    /// <summary>Remove um item da árvore depois de apagado e desconta o tamanho dos ancestrais.</summary>
    public void Forget(string path, long bytes, long files)
    {
        var node = Find(path);
        if (node?.Parent != null)
        {
            node.Parent.Children.Remove(node);
            for (var p = node.Parent; p != null; p = p.Parent) { p.Size -= node.Size; p.FileCount -= node.FileCount; }
            return;
        }
        // Era um arquivo: desconta da pasta onde ele estava.
        var dir = Find(System.IO.Path.GetDirectoryName(path) ?? "");
        if (dir == null) return;
        dir.FilesSize -= bytes;
        for (var p = dir; p != null; p = p.Parent) { p.Size -= bytes; p.FileCount -= files; }
        lock (_top)
        {
            var keep = _top.UnorderedItems.Where(x => !string.Equals(x.Element.Path, path, Fs.PathComparison)).ToList();
            _top.Clear();
            foreach (var k in keep) _top.Enqueue(k.Element, k.Priority);
        }
    }

    /// <summary>Pastas "pesadas" bem conhecidas (node_modules, .git…) somadas pela árvore toda.</summary>
    public List<object> HeavyFolders()
    {
        var names = new[] { "node_modules", ".git", "bin", "obj", "build", ".gradle", "__pycache__", ".venv", "venv" };
        var totals = names.ToDictionary(n => n, _ => (bytes: 0L, count: 0), StringComparer.OrdinalIgnoreCase);
        void Walk(DirNode n)
        {
            if (totals.TryGetValue(n.Name, out var t)) { totals[n.Name] = (t.bytes + n.Size, t.count + 1); return; }
            foreach (var c in n.Children) Walk(c);
        }
        if (Root != null) Walk(Root);
        return totals.Where(t => t.Value.bytes > 0).OrderByDescending(t => t.Value.bytes)
            .Select(t => (object)new { name = t.Key, bytes = t.Value.bytes, count = t.Value.count }).ToList();
    }

    public static readonly Dictionary<string, string> Categories = BuildCategories();

    private static Dictionary<string, string> BuildCategories()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) map["." + e] = cat; }
        Add("Vídeos", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts 3gp");
        Add("Imagens", "jpg jpeg png gif bmp tif tiff webp heic raw cr2 nef psd svg ico");
        Add("Áudio", "mp3 wav flac aac ogg m4a wma opus");
        Add("Documentos", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods csv md epub");
        Add("Compactados", "zip rar 7z tar gz tgz bz2 xz zst cab");
        Add("Instaladores", "exe msi msix msixbundle appx iso img deb rpm snap appimage flatpak");
        Add("Discos virtuais", "vhdx vhd vmdk vdi qcow2 avhdx raw");
        Add("Bibliotecas e código", "dll so lib pdb js ts jsx tsx map cs java class jar py pyc go rs c cpp h json xml node wasm");
        Add("Bancos de dados", "db sqlite sqlite3 mdf ldf ndf bak edb");
        Add("Sistema e cache", "sys dat cache tmp log etl evtx mui cat pak blob idx journal");
        return map;
    }
}
