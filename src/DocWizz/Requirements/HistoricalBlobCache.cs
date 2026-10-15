// Immutable Git blob bytes may be reused across historical commits. This cache is
// process-local and bounded by the import caller; semantic models are never cached.
static class HistoricalBlobCache
{
    internal static readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal);
}
