namespace MareSynchronos.FileCache
{
    public enum CacheEvictionMode
    {
        LeastRecentlyUsed,
        LeastFrequentlyUsed,
        ExpirationDate,
    }
}