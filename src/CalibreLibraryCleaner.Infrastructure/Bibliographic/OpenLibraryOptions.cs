namespace CalibreLibraryCleaner.Infrastructure.Bibliographic;

public sealed record OpenLibraryOptions
{
    public OpenLibraryOptions(
        bool enabled = true,
        string? contact = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? minimumRequestInterval = null,
        int maximumResponseBytes = 256 * 1024)
    {
        string? normalizedContact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim();
        if (normalizedContact is { Length: > 128 }
            || normalizedContact?.Any(value => char.IsControl(value) || char.IsWhiteSpace(value)) == true)
            throw new ArgumentException("Open Library contact is invalid.", nameof(contact));
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        TimeSpan interval = minimumRequestInterval
            ?? (normalizedContact is null ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(334));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResponseBytes, 16 * 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResponseBytes, 1024 * 1024);
        Enabled = enabled;
        Contact = normalizedContact;
        RequestTimeout = timeout;
        MinimumRequestInterval = interval;
        MaximumResponseBytes = maximumResponseBytes;
    }

    public bool Enabled { get; }
    public string? Contact { get; }
    public TimeSpan RequestTimeout { get; }
    public TimeSpan MinimumRequestInterval { get; }
    public int MaximumResponseBytes { get; }

    public override string ToString() =>
        $"OpenLibrary(Enabled={Enabled}, Timeout={RequestTimeout}, Interval={MinimumRequestInterval}, ResponseBytes={MaximumResponseBytes})";
}

public sealed record BibliographicResolutionCacheOptions
{
    public BibliographicResolutionCacheOptions(
        string? storageRoot = null,
        long maximumEntryBytes = 32 * 1024,
        long maximumTotalBytes = 64L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntryBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEntryBytes, 256 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, maximumEntryBytes);
        StorageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CalibreLibraryCleaner",
            "bibliographic-resolutions");
        MaximumEntryBytes = maximumEntryBytes;
        MaximumTotalBytes = maximumTotalBytes;
    }

    public string StorageRoot { get; }
    public long MaximumEntryBytes { get; }
    public long MaximumTotalBytes { get; }
}
