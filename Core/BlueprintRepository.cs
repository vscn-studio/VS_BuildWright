namespace Buildwright.Core;

/// <summary>
/// File access for the local blueprint library. Every relative path is resolved
/// below <see cref="RootDirectory"/> so library actions cannot read or mutate
/// arbitrary player files.
/// </summary>
public sealed class BlueprintRepository
{
    private readonly string directory;
    private readonly IBlueprintCodec codec;

    public BlueprintRepository(string directory, IBlueprintCodec? codec = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.codec = codec ?? new BlueprintCodec();
        Directory.CreateDirectory(this.directory);
    }

    public string RootDirectory => directory;

    // Compatibility methods used by existing chat commands and workbench saves.
    public string GetPath(string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip)
    {
        var safe = SanitizeName(name);
        return ResolveRelativePath(safe + ExtensionFor(kind));
    }

    public bool Exists(string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip) => File.Exists(GetPath(name, kind));

    public void Save(BlueprintDocument document, string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip, bool overwrite = false)
    {
        document.Metadata.Name = SanitizeName(name);
        SavePath(Path.GetFileName(GetPath(name, kind)), document, overwrite);
    }

    public BlueprintDocument Load(string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip) => codec.Read(GetPath(name, kind));

    public void Delete(string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip)
    {
        var path = GetPath(name, kind);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Restore(string name, BlueprintFileKind kind = BlueprintFileKind.BuildwrightZip)
    {
        var path = GetPath(name, kind);
        var backup = path + ".bak";
        if (!File.Exists(backup)) throw new FileNotFoundException("No backup exists", backup);
        File.Move(backup, path, true);
    }

    public BlueprintDocument LoadPath(string relativePath)
    {
        var path = ResolveBlueprintPath(relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Blueprint does not exist", path);
        return codec.Read(path);
    }

    public void SavePath(string relativePath, BlueprintDocument document, bool overwrite = false)
    {
        var path = ResolveBlueprintPath(relativePath);
        if (string.IsNullOrWhiteSpace(document.Metadata.Name))
            document.Metadata.Name = Path.GetFileNameWithoutExtension(path);
        codec.Write(path, document, overwrite);
    }

    public IEnumerable<BlueprintSummary> List(string relativeDirectory = "")
    {
        var path = ResolveDirectory(relativeDirectory, mustExist: true);
        foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (!IsBlueprintFile(file)) continue;
            BlueprintDocument document;
            try { document = codec.Read(file); }
            catch { continue; }
            yield return Summary(file, document);
        }
    }

    public IEnumerable<BlueprintSummary> ListAll()
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (!IsBlueprintFile(file)) continue;
            BlueprintDocument document;
            try { document = codec.Read(file); }
            catch { continue; }
            yield return Summary(file, document);
        }
    }

    public IEnumerable<BlueprintLibraryEntry> ListEntries(string relativeDirectory = "")
    {
        var path = ResolveDirectory(relativeDirectory, mustExist: true);
        foreach (var childDirectory in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            yield return new BlueprintLibraryEntry
            {
                Name = Path.GetFileName(childDirectory),
                RelativePath = ToRelativePath(childDirectory),
                IsDirectory = true,
                ModifiedUtc = Directory.GetLastWriteTimeUtc(childDirectory)
            };
        }

        foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsBlueprintFile).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            BlueprintDocument? document = null;
            try { document = codec.Read(file); }
            catch { }
            if (document == null)
            {
                yield return new BlueprintLibraryEntry
                {
                    Name = Path.GetFileName(file),
                    RelativePath = ToRelativePath(file),
                    IsUnreadable = true,
                    ModifiedUtc = File.GetLastWriteTimeUtc(file)
                };
                continue;
            }

            yield return new BlueprintLibraryEntry
            {
                Name = Path.GetFileName(file),
                RelativePath = ToRelativePath(file),
                Summary = Summary(file, document),
                ModifiedUtc = File.GetLastWriteTimeUtc(file)
            };
        }
    }

    public void CreateDirectory(string relativeDirectory, string name)
    {
        var parent = ResolveDirectory(relativeDirectory, mustExist: true);
        Directory.CreateDirectory(ResolveRelativePath(Path.Combine(ToRelativePath(parent), SanitizeName(name))));
    }

    public string Copy(string relativePath, string targetDirectory)
    {
        var source = ResolveExistingPath(relativePath);
        var target = ResolveDirectory(targetDirectory, mustExist: true);
        var targetPath = UniqueDestination(target, Path.GetFileName(source));
        if (Directory.Exists(source)) CopyDirectory(source, targetPath);
        else File.Copy(source, targetPath, false);
        return ToRelativePath(targetPath);
    }

    public string Move(string relativePath, string targetDirectory)
    {
        var source = ResolveExistingPath(relativePath);
        var target = ResolveDirectory(targetDirectory, mustExist: true);
        if (Directory.Exists(source) && IsWithin(source, target))
            throw new InvalidOperationException("Cannot move a folder into itself.");
        var targetPath = UniqueDestination(target, Path.GetFileName(source));
        if (Directory.Exists(source)) Directory.Move(source, targetPath);
        else File.Move(source, targetPath);
        return ToRelativePath(targetPath);
    }

    public string Rename(string relativePath, string newName)
    {
        var source = ResolveExistingPath(relativePath);
        var parent = Path.GetDirectoryName(source)!;
        var fileExtension = File.Exists(source) ? Path.GetExtension(source) : string.Empty;
        var leaf = fileExtension.Length == 0
            ? SanitizeName(newName)
            : SanitizeName(Path.GetFileNameWithoutExtension(newName)) + fileExtension;
        var target = Path.Combine(parent, leaf);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return ToRelativePath(source);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("A file or folder with that name already exists.");

        if (File.Exists(source))
        {
            var document = codec.Read(source);
            document.Metadata.Name = Path.GetFileNameWithoutExtension(leaf);
            codec.Write(target, document, overwrite: false);
            File.Delete(source);
            var backup = source + ".bak";
            if (File.Exists(backup)) File.Move(backup, target + ".bak", true);
        }
        else Directory.Move(source, target);
        return ToRelativePath(target);
    }

    public void DeletePath(string relativePath, bool recursive = false)
    {
        var path = ResolveExistingPath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
            var backup = path + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            return;
        }

        if (!recursive && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("Folder is not empty. Confirm recursive deletion first.");
        Directory.Delete(path, recursive);
    }

    public string ResolveRelativePath(string relativePath)
    {
        if (relativePath == null) throw new ArgumentNullException(nameof(relativePath));
        if (Path.IsPathRooted(relativePath)) throw new ArgumentException("Absolute paths are not allowed.", nameof(relativePath));
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(directory, normalized));
        if (!IsWithinOrRoot(full)) throw new ArgumentException("Path escapes the blueprint library.", nameof(relativePath));
        return full;
    }

    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Blueprint name is required", nameof(name));
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim('.', ' ');
        if (safe.Length == 0 || safe == "." || safe == "..") throw new ArgumentException("Blueprint name is invalid", nameof(name));
        return safe.Length > 80 ? safe[..80] : safe;
    }

    private BlueprintSummary Summary(string path, BlueprintDocument document) => new()
    {
        Name = document.Metadata.Name,
        Path = path,
        RelativePath = ToRelativePath(path),
        Kind = Path.GetExtension(path).Equals(".vsbp", StringComparison.OrdinalIgnoreCase) ? BlueprintFileKind.BuildwrightZip : BlueprintFileKind.NativeJson,
        Dimensions = document.Metadata.Dimensions,
        BlockCount = document.Metadata.BlockCount,
        MaterialCount = document.Metadata.MaterialCount,
        ModifiedUtc = File.GetLastWriteTimeUtc(path),
        HasBackup = File.Exists(path + ".bak"),
        Author = document.Metadata.Author
    };

    private string ResolveBlueprintPath(string relativePath)
    {
        var path = ResolveRelativePath(relativePath);
        if (!IsBlueprintFile(path)) throw new ArgumentException("Only .vsbp and .json blueprint files are supported.", nameof(relativePath));
        return path;
    }

    private string ResolveDirectory(string relativePath, bool mustExist)
    {
        var path = ResolveRelativePath(relativePath);
        if (mustExist && !Directory.Exists(path)) throw new DirectoryNotFoundException("Blueprint folder does not exist: " + relativePath);
        return path;
    }

    private string ResolveExistingPath(string relativePath)
    {
        var path = ResolveRelativePath(relativePath);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Library entry does not exist", path);
        return path;
    }

    private string ToRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(directory, fullPath);
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private bool IsWithinOrRoot(string path) => string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) || IsWithin(directory, path);
    private static bool IsWithin(string parent, string candidate) => candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsBlueprintFile(string path) => Path.GetExtension(path).Equals(".vsbp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
    private static string ExtensionFor(BlueprintFileKind kind) => kind == BlueprintFileKind.BuildwrightZip ? ".vsbp" : ".json";

    private static string UniqueDestination(string parent, string leaf)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var extension = Path.GetExtension(leaf);
        var stem = extension.Length == 0 ? leaf : Path.GetFileNameWithoutExtension(leaf);
        for (var copy = 2; copy < 10000; copy++)
        {
            candidate = Path.Combine(parent, $"{stem} ({copy}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not create a unique copy name.");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        foreach (var child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }
}

public sealed class BlueprintLibraryEntry
{
    public string Name { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsUnreadable { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public BlueprintSummary? Summary { get; init; }
}
