namespace Snowcloak.Infrastructure.FileCache;

public readonly record struct CachePathReference(CachePathRoot Root, string RelativePath);
