using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AINexus.App;

public sealed record CatalogRecord(string Id, DiscoveredItem Item,
    IReadOnlyList<DiscoveryInterest> Interests, DateTimeOffset LastCheckedAt);

/// <summary>Public source metadata only. Installation paths, prompts, settings and local test results
/// belong to the private library, never this catalog. Listing an item grants no execution authority.</summary>
public sealed class CatalogDatabase
{
    private const int SchemaVersion = 1;
    private const int ApplicationId = 0x41494E58;
    internal const int MaximumEntries = 10_000;
    private const int MaximumItemBytes = 1_048_576;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    private readonly SemaphoreSlim _gate;
    public string DatabasePath { get; }

    public CatalogDatabase(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute catalog path.", nameof(path));
        DatabasePath = Path.GetFullPath(path);
        CheckPaths();
        _gate = Gates.GetOrAdd(DatabasePath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task UpsertDiscoveryAsync(OnlineDiscoverySnapshot snapshot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        if (snapshot.IsCached) throw new InvalidDataException("Saved search data cannot be recorded as a fresh source check.");
        RequireTime(snapshot.RetrievedAt);
        if (snapshot.Items is null || snapshot.Items.Count > MaximumEntries) throw new InvalidDataException("Catalog import exceeds its supported bounds.");
        // Copy and validate before crossing the worker boundary or touching an existing database.
        var incoming = snapshot.Items.Select(item => Deserialize(Serialize(item with { Id = CanonicalId(item) }))).ToArray();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                CheckPaths();
                Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                using var connection = Open(create: true);
                using var transaction = connection.BeginTransaction();
                foreach (var item in incoming)
                {
                    token.ThrowIfCancellationRequested();
                    var prior = ReadOne(connection, transaction, item.Id);
                    if (prior is not null) RequireCompatible(prior.Item.Artifact, item.Artifact);
                    if (prior is null || snapshot.RetrievedAt > prior.LastCheckedAt)
                    {
                        var latest = item;
                        if (prior?.Item.Artifact is { } known && latest.Artifact is { } received)
                            latest = latest with { Artifact = received with { SizeBytes = received.SizeBytes ?? known.SizeBytes, Sha256 = received.Sha256 ?? known.Sha256 } };
                        using var command = Command(connection, transaction, """
                            INSERT INTO catalog_item(id,name,publisher,kind,payload,checked_at) VALUES($id,$name,$publisher,$kind,$payload,$at)
                            ON CONFLICT(id) DO UPDATE SET name=excluded.name,publisher=excluded.publisher,kind=excluded.kind,payload=excluded.payload,checked_at=excluded.checked_at;
                            """);
                        command.Parameters.AddWithValue("$id", item.Id);
                        command.Parameters.AddWithValue("$name", latest.Name);
                        command.Parameters.AddWithValue("$publisher", latest.Publisher);
                        command.Parameters.AddWithValue("$kind", latest.Kind);
                        command.Parameters.AddWithValue("$payload", Serialize(latest));
                        command.Parameters.AddWithValue("$at", snapshot.RetrievedAt.ToUniversalTime().ToString("O"));
                        command.ExecuteNonQuery();
                    }
                    using var category = Command(connection, transaction, "INSERT OR IGNORE INTO catalog_category(item_id,interest) VALUES($id,$interest);");
                    category.Parameters.AddWithValue("$id", item.Id);
                    category.Parameters.AddWithValue("$interest", (int)item.Interest);
                    category.ExecuteNonQuery();
                }
                using var count = Command(connection, transaction, "SELECT COUNT(*) FROM catalog_item;");
                if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > MaximumEntries)
                    throw new InvalidDataException("The catalog has reached its supported entry limit. Existing entries were preserved; none were evicted.");
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<CatalogRecord>> ReadAsync(string query = "", DiscoveryInterest? interest = null,
        CancellationToken token = default)
    {
        if (query is null || query.Length > 160 || query.Any(char.IsControl)) throw new ArgumentException("Catalog search must be at most 160 characters without control characters.", nameof(query));
        if (interest is { } category && !Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(interest));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<CatalogRecord>>(() =>
            {
                token.ThrowIfCancellationRequested();
                CheckPaths();
                if (!File.Exists(DatabasePath)) return Array.Empty<CatalogRecord>();
                using var connection = Open(create: false);
                using var command = Command(connection, null, """
                    SELECT i.id,i.payload,i.checked_at FROM catalog_item i
                    WHERE ($query='' OR instr(lower(i.name),lower($query))>0 OR instr(lower(i.publisher),lower($query))>0)
                    AND ($interest=-1 OR EXISTS(SELECT 1 FROM catalog_category c WHERE c.item_id=i.id AND c.interest=$interest))
                    ORDER BY i.name COLLATE NOCASE,i.id LIMIT 10001;
                    """);
                command.Parameters.AddWithValue("$query", query);
                command.Parameters.AddWithValue("$interest", interest is { } value ? (int)value : -1);
                var rows = new List<(string Id, DiscoveredItem Item, DateTimeOffset At)>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (rows.Count == MaximumEntries) throw new InvalidDataException("Existing catalog exceeds the supported entry limit and was preserved.");
                        rows.Add((reader.GetString(0), Deserialize(reader.GetString(1)), ReadTime(reader.GetString(2))));
                    }
                return Array.AsReadOnly(rows.Select(row =>
                {
                    if (row.Id != row.Item.Id) throw new InvalidDataException("Stored catalog identity is inconsistent.");
                    var categories = ReadCategories(connection, row.Id);
                    if (categories.Count == 0) throw new InvalidDataException("Stored catalog entry has no categories.");
                    return new CatalogRecord(row.Id, row.Item, categories, row.At);
                }).ToArray());
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static string CanonicalId(DiscoveredItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Artifact is { } artifact)
        {
            if (!Repository(artifact.Repository) || !Hex(artifact.Revision, 40) || !SafePath(artifact.FileName))
                throw new InvalidDataException("A model catalog entry needs a pinned repository revision and safe artifact path.");
            return $"huggingface:{artifact.Repository}@{artifact.Revision.ToLowerInvariant()}:{artifact.FileName}";
        }
        var source = item.Sources?.FirstOrDefault(s => s?.Url is { IsAbsoluteUri: true, Host: "github.com" });
        if (source is null) throw new InvalidDataException("A tool/runtime catalog entry needs a canonical public GitHub repository source.");
        RequireUrl(source.Url);
        var parts = source.Url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || !Repository(parts[0] + "/" + parts[1])) throw new InvalidDataException("Tool source repository is invalid.");
        var repo = parts[0] + "/" + parts[1];
        if (item.Kind == "Runtime" && item.RuntimeRelease is { } runtime)
        {
            RequireText(runtime.Tag, 128);
            return "github:" + repo.ToLowerInvariant() + "@" + runtime.Tag;
        }
        if (item.Kind != "Tool") throw new InvalidDataException("Unsupported catalog item identity.");
        return "github:" + repo.ToLowerInvariant();
    }

    private SqliteConnection Open(bool create)
    {
        CheckPaths();
        var existed = File.Exists(DatabasePath);
        if (existed && new FileInfo(DatabasePath).Length > 256L * 1024 * 1024) throw new InvalidDataException("Existing catalog is oversized and was preserved.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            var version = Convert.ToInt32(Command(connection, null, "PRAGMA user_version;").ExecuteScalar(), CultureInfo.InvariantCulture);
            var app = Convert.ToInt32(Command(connection, null, "PRAGMA application_id;").ExecuteScalar(), CultureInfo.InvariantCulture);
            if (existed && (version != SchemaVersion || app != ApplicationId)) throw new InvalidDataException("Existing database has an unknown catalog schema and was preserved.");
            if (!existed)
            {
                if (!create) throw new FileNotFoundException("Catalog disappeared before it could be read.");
                using var transaction = connection.BeginTransaction();
                using var setup = Command(connection, transaction, $"""
                    CREATE TABLE catalog_item(id TEXT PRIMARY KEY,name TEXT NOT NULL,publisher TEXT NOT NULL,kind TEXT NOT NULL,payload TEXT NOT NULL,checked_at TEXT NOT NULL);
                    CREATE TABLE catalog_category(item_id TEXT NOT NULL REFERENCES catalog_item(id),interest INTEGER NOT NULL,PRIMARY KEY(item_id,interest));
                    CREATE INDEX catalog_interest ON catalog_category(interest,item_id);
                    PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};
                    """);
                setup.ExecuteNonQuery();
                transaction.Commit();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = 5; return command;
    }
    private static CatalogRecord? ReadOne(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(connection, transaction, "SELECT payload,checked_at FROM catalog_item WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(id, Deserialize(reader.GetString(0)), [], ReadTime(reader.GetString(1))) : null;
    }
    private static IReadOnlyList<DiscoveryInterest> ReadCategories(SqliteConnection connection, string id)
    {
        using var command = Command(connection, null, "SELECT interest FROM catalog_category WHERE item_id=$id ORDER BY interest;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var result = new List<DiscoveryInterest>();
        while (reader.Read())
        {
            var value = (DiscoveryInterest)reader.GetInt32(0);
            if (!Enum.IsDefined(value) || result.Count >= Enum.GetValues<DiscoveryInterest>().Length) throw new InvalidDataException("Stored catalog category is invalid.");
            result.Add(value);
        }
        return result.AsReadOnly();
    }
    private void CheckPaths()
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) OfflineModelVerifier.CheckNoReparsePath(DatabasePath + suffix);
    }
    private static string Serialize(DiscoveredItem item)
    {
        Validate(item);
        var value = JsonSerializer.Serialize(item, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaximumItemBytes) throw new InvalidDataException("Catalog item exceeds the supported size.");
        return value;
    }
    private static DiscoveredItem Deserialize(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumItemBytes) throw new InvalidDataException("Stored catalog item exceeds the supported size.");
        var item = JsonSerializer.Deserialize<DiscoveredItem>(json, JsonOptions) ?? throw new InvalidDataException("Catalog item is missing.");
        Validate(item); return item;
    }
    private static void Validate(DiscoveredItem item)
    {
        RequireText(item.Id, 1024); RequireText(item.Name, 512); RequireText(item.Publisher, 256); RequireText(item.Description, 8192);
        if (!Enum.IsDefined(item.Interest) || item.Kind is not ("Model" or "Runtime" or "Tool") || item.Id != CanonicalId(item)) throw new InvalidDataException("Catalog identity/type is invalid.");
        if (item.License is { } license) RequireText(license, 256);
        if (item.PipelineTag is { } pipeline) RequireText(pipeline, 128);
        if (item.Sources is null || item.Sources.Count is < 1 or > 16) throw new InvalidDataException("Catalog source evidence is missing or oversized.");
        foreach (var source in item.Sources)
        {
            if (source is null) throw new InvalidDataException("Catalog source is missing.");
            RequireText(source.Name, 256); RequireUrl(source.Url); RequireTime(source.RetrievedAt);
        }
        if (item.Artifact is { } artifact)
        {
            if (item.Kind != "Model" || artifact.SizeBytes is <= 0 || artifact.Sha256 is { } hash && !Hex(hash, 64)) throw new InvalidDataException("Catalog artifact requirements are invalid.");
            RequireText(artifact.Format, 32);
        }
        if (item.RuntimeRelease is { } runtime)
        {
            if (item.Kind != "Runtime" || runtime.Archives is null || runtime.Archives.Count > 200 || runtime.Notes is null || runtime.Notes.Count > 64) throw new InvalidDataException("Catalog runtime metadata is invalid.");
            foreach (var archive in runtime.Archives)
            {
                if (archive is null || archive.SizeBytes <= 0 || archive.Sha256 is { } hash && !Hex(hash, 64)) throw new InvalidDataException("Catalog runtime archive identity is invalid.");
                RequireText(archive.Name, 512); RequireUrl(archive.DownloadUrl);
            }
            foreach (var note in runtime.Notes) RequireText(note, 2048);
        }
    }
    private static void RequireCompatible(ModelArtifact? prior, ModelArtifact? incoming)
    {
        if (prior is null ? incoming is not null : incoming is null || prior.Repository != incoming.Repository || prior.Revision != incoming.Revision
            || prior.FileName != incoming.FileName || prior.Format != incoming.Format
            || prior.SizeBytes is { } a && incoming.SizeBytes is { } b && a != b
            || prior.Sha256 is { } x && incoming.Sha256 is { } y && !x.Equals(y, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A pinned catalog artifact cannot change its size, hash, format or identity. Existing data was preserved.");
    }
    private static DateTimeOffset ReadTime(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) throw new InvalidDataException("Catalog timestamp is malformed.");
        RequireTime(at); return at;
    }
    private static void RequireTime(DateTimeOffset value) { if (value == DateTimeOffset.MinValue || value > DateTimeOffset.UtcNow.AddMinutes(1)) throw new InvalidDataException("Catalog timestamp is invalid."); }
    private static void RequireText(string? value, int max) { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw new InvalidDataException("Catalog text is missing or outside supported bounds."); }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    private static bool Repository(string? value) => value is not null && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant);
    private static bool SafePath(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Contains('\\')
        && value.Split('/').All(s => s.Length > 0 && s is not ("." or "..") && !s.Any(c => char.IsControl(c) || c is ':' or '?' or '#'));
    private static void RequireUrl(Uri? value)
    {
        if (value is null || !value.IsAbsoluteUri || value.AbsoluteUri.Length > 2048 || value.Scheme != "https" || value.Port != 443
            || value.UserInfo.Length != 0 || value.Query.Length != 0 || value.Fragment.Length != 0
            || value.Host is not ("huggingface.co" or "github.com" or "api.github.com")) throw new InvalidDataException("Catalog source URLs must be public, supported HTTPS links without credentials or query tokens.");
    }
}
