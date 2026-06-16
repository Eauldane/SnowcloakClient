using System.Globalization;

namespace Snowcloak.Infrastructure.FileCache;

public sealed class CachePathResolver
{
    public const string CachePrefix = "{cache}";
    public const string PenumbraPrefix = "{penumbra}";
    public const string SubstitutePrefix = "{subst}";

    private const string TempFolderName = ".tmp";
    private readonly string _cacheRoot;
    private readonly string? _penumbraRoot;
    private readonly string _substituteRoot;

    public CachePathResolver(string? penumbraRoot, string? cacheRoot, string substituteFolderName)
    {
        _penumbraRoot = NormaliseRoot(penumbraRoot);
        _cacheRoot = NormaliseRoot(cacheRoot) ?? string.Empty;
        _substituteRoot = string.IsNullOrEmpty(_cacheRoot)
            ? string.Empty
            : NormaliseRoot(System.IO.Path.Combine(_cacheRoot, substituteFolderName)) ?? string.Empty;
    }

    public string CacheRoot => _cacheRoot;

    public string SubstituteRoot => _substituteRoot;

    public static CachePathReference CreateObjectReference(CachePathRoot root, string hash, string extension)
    {
        var normalisedHash = NormaliseIdentifier(hash, nameof(hash));
        var normalisedExtension = NormaliseExtension(extension);
        return new CachePathReference(root, CombineRelative(normalisedHash[..2], normalisedHash + "." + normalisedExtension));
    }

    public static CachePathReference CreateTemporaryReference(CachePathRoot root, string name, string extension)
    {
        var normalisedName = NormaliseIdentifier(name, nameof(name));
        var normalisedExtension = NormaliseExtension(extension);
        return new CachePathReference(root, CombineRelative(TempFolderName, normalisedName + "." + normalisedExtension));
    }

    public string GetObjectPath(CachePathRoot root, string hash, string extension) =>
        Resolve(CreateObjectReference(root, hash, extension));

    public string GetTemporaryPath(CachePathRoot root, string name, string extension) =>
        Resolve(CreateTemporaryReference(root, name, extension));

    public string GetFlatObjectPath(CachePathRoot root, string hash, string extension)
    {
        var normalisedHash = NormaliseIdentifier(hash, nameof(hash));
        var normalisedExtension = NormaliseExtension(extension);
        return Resolve(new CachePathReference(root, normalisedHash + "." + normalisedExtension));
    }

    public string Resolve(CachePathReference reference)
    {
        var root = GetRootPath(reference.Root);
        if (string.IsNullOrEmpty(root))
        {
            return string.Empty;
        }

        return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, ToPlatformRelativePath(reference.RelativePath)));
    }

    public static string ToPrefixedPath(CachePathReference reference)
    {
        var relative = ToStorageRelativePath(reference.RelativePath);
        if (string.IsNullOrEmpty(relative))
        {
            return GetPrefix(reference.Root);
        }

        return GetPrefix(reference.Root) + "\\" + relative;
    }

    public bool TryCreateReference(string path, out CachePathReference reference)
    {
        if (TryParsePrefixedPath(path, out reference))
        {
            return true;
        }

        if (TryCreateReference(CachePathRoot.Substitute, path, out reference))
        {
            return true;
        }

        if (TryCreateReference(CachePathRoot.Cache, path, out reference))
        {
            return true;
        }

        return TryCreateReference(CachePathRoot.Penumbra, path, out reference);
    }

    public static bool TryParsePrefixedPath(string prefixedPath, out CachePathReference reference)
    {
        ArgumentNullException.ThrowIfNull(prefixedPath);

        foreach (var root in new[] { CachePathRoot.Substitute, CachePathRoot.Cache, CachePathRoot.Penumbra })
        {
            var prefix = GetPrefix(root);
            if (prefixedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reference = new CachePathReference(root, string.Empty);
                return true;
            }

            if (prefixedPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)
                || prefixedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                reference = new CachePathReference(root, NormaliseRelativePath(prefixedPath[prefix.Length..]));
                return true;
            }
        }

        reference = default;
        return false;
    }

    public static bool TryGetContentAddress(CachePathReference reference, out string hash, out string extension)
    {
        var fileName = System.IO.Path.GetFileName(ToPlatformRelativePath(reference.RelativePath));
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);

        if (name.Length == 64 && name.All(Uri.IsHexDigit) && ext.Length > 1)
        {
            hash = name.ToUpperInvariant();
            extension = ext[1..];
            return true;
        }

        hash = string.Empty;
        extension = string.Empty;
        return false;
    }

    public static bool IsCanonicalObjectReference(CachePathReference reference)
    {
        if (!TryGetContentAddress(reference, out var hash, out var extension))
        {
            return false;
        }

        var canonical = CreateObjectReference(reference.Root, hash, extension);
        return string.Equals(ToStorageRelativePath(reference.RelativePath),
            ToStorageRelativePath(canonical.RelativePath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTemporaryReference(CachePathReference reference)
    {
        var relative = ToStorageRelativePath(reference.RelativePath);
        return relative.StartsWith(TempFolderName + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetPrefix(CachePathRoot root) =>
        root switch
        {
            CachePathRoot.Penumbra => PenumbraPrefix,
            CachePathRoot.Cache => CachePrefix,
            CachePathRoot.Substitute => SubstitutePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, null)
        };

    private string GetRootPath(CachePathRoot root) =>
        root switch
        {
            CachePathRoot.Penumbra => _penumbraRoot ?? string.Empty,
            CachePathRoot.Cache => _cacheRoot,
            CachePathRoot.Substitute => _substituteRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(root), root, null)
        };

    private bool TryCreateReference(CachePathRoot root, string path, out CachePathReference reference)
    {
        var rootPath = GetRootPath(root);
        if (string.IsNullOrEmpty(rootPath))
        {
            reference = default;
            return false;
        }

        if (!TryGetRelativePath(rootPath, path, out var relativePath))
        {
            reference = default;
            return false;
        }

        reference = new CachePathReference(root, NormaliseRelativePath(relativePath));
        return true;
    }

    private static bool TryGetRelativePath(string root, string path, out string relativePath)
    {
        var normalisedRoot = NormaliseRoot(root);
        var normalisedPath = NormaliseRoot(path);
        if (string.IsNullOrEmpty(normalisedRoot) || string.IsNullOrEmpty(normalisedPath))
        {
            relativePath = string.Empty;
            return false;
        }

        if (string.Equals(normalisedRoot, normalisedPath, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = string.Empty;
            return true;
        }

        var rootWithSeparator = normalisedRoot.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? normalisedRoot
            : normalisedRoot + System.IO.Path.DirectorySeparatorChar;

        if (!normalisedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = System.IO.Path.GetRelativePath(normalisedRoot, normalisedPath);
        return true;
    }

    private static string? NormaliseRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private static string NormaliseIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim().ToUpper(CultureInfo.InvariantCulture);
    }

    private static string NormaliseExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("Extension cannot be empty.", nameof(extension));
        }

        return extension.Trim().TrimStart('.');
    }

    private static string CombineRelative(params string[] parts) =>
        NormaliseRelativePath(string.Join(System.IO.Path.DirectorySeparatorChar, parts));

    private static string NormaliseRelativePath(string relativePath)
    {
        var parts = relativePath
            .Replace('\\', System.IO.Path.DirectorySeparatorChar)
            .Replace('/', System.IO.Path.DirectorySeparatorChar)
            .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.Equals(part, ".", StringComparison.Ordinal))
            .ToArray();

        if (parts.Any(part => string.Equals(part, "..", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Relative path cannot escape its root.", nameof(relativePath));
        }

        return parts.Length == 0 ? string.Empty : System.IO.Path.Combine(parts);
    }

    private static string ToPlatformRelativePath(string relativePath) =>
        NormaliseRelativePath(relativePath);

    private static string ToStorageRelativePath(string relativePath) =>
        string.Join('\\', NormaliseRelativePath(relativePath)
            .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
}
