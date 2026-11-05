using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ch06.Ingestion.Worker.Store;

/// <summary>What the pipeline remembers about a source item between runs, for change detection.</summary>
public sealed record SourceState(string System, string Uri, string? ETag, DateTimeOffset LastModified, long Length, string Sha256, string? TextHash, string? DocId, string? Version);

/// <summary>A chunk as stored: the ID is derived from the content, so unchanged text keeps its ID and its embedding.</summary>
public sealed record StoredChunk(string ChunkId, string DocId, string Text, string Section, float[] Embedding);

/// <summary>
/// The pipeline's state in SQLite: source state for change detection, document versions with effective dates,
/// content-addressed chunks shared between versions, lineage records, runs and quarantined items.
/// Chapter 6 also shows the PostgreSQL schema for a shared deployment (<see cref="PostgresLineageStore"/>).
/// </summary>
public sealed class IngestionStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private SqliteTransaction? _transaction;

    public IngestionStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute(Schema);
    }

    public const string Schema = """
        CREATE TABLE IF NOT EXISTS source_state (
            system TEXT NOT NULL, uri TEXT NOT NULL, etag TEXT, last_modified TEXT, length INTEGER,
            sha256 TEXT, text_hash TEXT, doc_id TEXT, version TEXT, PRIMARY KEY (system, uri));
        CREATE TABLE IF NOT EXISTS checkpoints (system TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS documents (
            doc_id TEXT NOT NULL, version TEXT NOT NULL, system TEXT, uri TEXT, title TEXT, tenant TEXT,
            doc_type TEXT, sensitivity TEXT, groups TEXT, effective_from TEXT, effective_to TEXT,
            superseded_by TEXT, sha256 TEXT, text_hash TEXT, parser TEXT, artifact TEXT,
            duplicate_of TEXT, tombstoned INTEGER NOT NULL DEFAULT 0, run_id TEXT,
            PRIMARY KEY (doc_id, version));
        CREATE TABLE IF NOT EXISTS chunks (
            chunk_id TEXT PRIMARY KEY, doc_id TEXT NOT NULL, text TEXT NOT NULL, section TEXT,
            embedding BLOB NOT NULL, embedding_model TEXT NOT NULL, run_id TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS version_chunks (
            doc_id TEXT NOT NULL, version TEXT NOT NULL, chunk_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
            start_offset INTEGER, end_offset INTEGER, first_block INTEGER, last_block INTEGER,
            PRIMARY KEY (doc_id, version, ordinal));
        CREATE TABLE IF NOT EXISTS runs (run_id TEXT PRIMARY KEY, started TEXT, ended TEXT, state TEXT, summary TEXT);
        CREATE TABLE IF NOT EXISTS lineage_events (run_id TEXT, event_time TEXT, event_type TEXT, job TEXT, event TEXT);
        CREATE TABLE IF NOT EXISTS quarantine (
            system TEXT NOT NULL, uri TEXT NOT NULL, reason TEXT, detail TEXT, attempts INTEGER,
            first_seen TEXT, last_seen TEXT, PRIMARY KEY (system, uri));
        """;

    // ---- change detection ----

    public SourceState? State(string system, string uri) =>
        QuerySingle("SELECT system, uri, etag, last_modified, length, sha256, text_hash, doc_id, version FROM source_state WHERE system=$s AND uri=$u",
            r => new SourceState(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), r.GetInt64(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)),
            ("$s", system), ("$u", uri));

    public IReadOnlyList<string> KnownUris(string system) =>
        Query("SELECT uri FROM source_state WHERE system=$s", r => r.GetString(0), ("$s", system));

    public void SaveState(SourceState state) => Execute(
        "INSERT OR REPLACE INTO source_state VALUES ($s,$u,$e,$m,$l,$h,$t,$d,$v)",
        ("$s", state.System), ("$u", state.Uri), ("$e", state.ETag), ("$m", state.LastModified.ToString("O")), ("$l", state.Length),
        ("$h", state.Sha256), ("$t", state.TextHash), ("$d", state.DocId), ("$v", state.Version));

    public void ForgetState(string system, string uri) =>
        Execute("DELETE FROM source_state WHERE system=$s AND uri=$u", ("$s", system), ("$u", uri));

    public string? Checkpoint(string system) =>
        QuerySingle("SELECT value FROM checkpoints WHERE system=$s", r => r.GetString(0), ("$s", system));

    public void SaveCheckpoint(string system, string value) =>
        Execute("INSERT OR REPLACE INTO checkpoints VALUES ($s,$v)", ("$s", system), ("$v", value));

    // ---- documents and versions ----

    public void UpsertDocument(DocumentRow row) => Execute("""
        INSERT INTO documents (doc_id, version, system, uri, title, tenant, doc_type, sensitivity, groups, effective_from, effective_to,
                               superseded_by, sha256, text_hash, parser, artifact, duplicate_of, tombstoned, run_id)
        VALUES ($id,$v,$sys,$uri,$title,$tenant,$type,$sens,$groups,$from,$to,NULL,$sha,$th,$parser,$artifact,$dup,0,$run)
        ON CONFLICT (doc_id, version) DO UPDATE SET system=$sys, uri=$uri, title=$title, tenant=$tenant, doc_type=$type, sensitivity=$sens,
            groups=$groups, effective_from=$from, effective_to=$to, sha256=$sha, text_hash=$th, parser=$parser, artifact=$artifact,
            duplicate_of=$dup, tombstoned=0, run_id=$run
        """,
        ("$id", row.DocId), ("$v", row.Version), ("$sys", row.System), ("$uri", row.Uri), ("$title", row.Title), ("$tenant", row.Tenant),
        ("$type", row.DocType), ("$sens", row.Sensitivity), ("$groups", string.Join(',', row.Groups)), ("$from", row.EffectiveFrom?.ToString("yyyy-MM-dd")),
        ("$to", row.EffectiveTo?.ToString("yyyy-MM-dd")), ("$sha", row.Sha256), ("$th", row.TextHash), ("$parser", row.Parser),
        ("$artifact", row.Artifact), ("$dup", row.DuplicateOf), ("$run", row.RunId));

    /// <summary>
    /// Closes the effective window of every other version of the document that starts before this one
    /// and records which version superseded it. Versions arrive in any order; this keeps the windows consistent.
    /// </summary>
    public void LinkVersions(string docId)
    {
        var versions = Query("SELECT version, effective_from FROM documents WHERE doc_id=$id AND tombstoned=0 AND effective_from IS NOT NULL ORDER BY effective_from",
            r => (Version: r.GetString(0), From: DateOnly.Parse(r.GetString(1))), ("$id", docId));
        for (var i = 0; i < versions.Count - 1; i++)
        {
            Execute("UPDATE documents SET effective_to=$to, superseded_by=$by WHERE doc_id=$id AND version=$v",
                ("$to", versions[i + 1].From.AddDays(-1).ToString("yyyy-MM-dd")), ("$by", versions[i + 1].Version), ("$id", docId), ("$v", versions[i].Version));
        }
    }

    public IReadOnlyList<DocumentRow> Documents(string? docId = null) => Query(
        "SELECT doc_id, version, system, uri, title, tenant, doc_type, sensitivity, groups, effective_from, effective_to, superseded_by, sha256, text_hash, parser, artifact, duplicate_of, tombstoned, run_id FROM documents" +
        (docId is null ? "" : " WHERE doc_id=$id") + " ORDER BY doc_id, effective_from",
        r => new DocumentRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
            r.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries), r.IsDBNull(9) ? null : DateOnly.Parse(r.GetString(9)),
            r.IsDBNull(10) ? null : DateOnly.Parse(r.GetString(10)), r.GetString(12), r.GetString(13), r.GetString(14), r.GetString(15),
            r.IsDBNull(16) ? null : r.GetString(16), r.GetString(18))
        {
            SupersededBy = r.IsDBNull(11) ? null : r.GetString(11),
            Tombstoned = r.GetInt64(17) == 1,
        },
        docId is null ? [] : [("$id", docId)]);

    /// <summary>
    /// Among live documents with the same normalized text, keeps one canonical copy and points the others at it.
    /// The canonical copy comes from the most authoritative source (document control's file share first), then the
    /// shortest path, so the choice doesn't depend on the order files were processed in.
    /// </summary>
    public IReadOnlyList<(string System, string Uri, string RunId)> ResolveDuplicates()
    {
        static int Authority(string system) => system switch { "fileshare" => 0, "sharepoint" => 1, "intranet" => 2, "mail" => 3, _ => 4 };
        var marked = new List<(string, string, string)>();
        InTransaction(() =>
        {
            var groups = Documents().Where(d => !d.Tombstoned).GroupBy(d => d.TextHash).Where(g => g.Select(d => d.DocId).Distinct().Count() > 1);
            foreach (var group in groups)
            {
                var ordered = group.OrderBy(d => Authority(d.System)).ThenBy(d => d.Uri.Count(c => c == '/')).ThenBy(d => d.Uri, StringComparer.Ordinal).ToList();
                var canonical = $"{ordered[0].DocId}@{ordered[0].Version}";
                Execute("UPDATE documents SET duplicate_of=NULL WHERE doc_id=$d AND version=$v", ("$d", ordered[0].DocId), ("$v", ordered[0].Version));
                foreach (var copy in ordered.Skip(1).Where(c => c.DocId != ordered[0].DocId))
                {
                    Execute("UPDATE documents SET duplicate_of=$c WHERE doc_id=$d AND version=$v", ("$c", canonical), ("$d", copy.DocId), ("$v", copy.Version));
                    marked.Add((copy.System, copy.Uri, copy.RunId));
                }
            }
        });
        return marked;
    }

    public void Tombstone(string docId, string version, string runId) =>
        Execute("UPDATE documents SET tombstoned=1, run_id=$run WHERE doc_id=$id AND version=$v", ("$id", docId), ("$v", version), ("$run", runId));

    // ---- chunks ----

    public bool HasChunk(string chunkId) =>
        QuerySingle("SELECT 1 FROM chunks WHERE chunk_id=$c", r => true, ("$c", chunkId));

    public void AddChunk(string chunkId, string docId, string text, string section, float[] embedding, string model, string runId)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        Execute("INSERT OR IGNORE INTO chunks VALUES ($c,$d,$t,$s,$e,$m,$r)",
            ("$c", chunkId), ("$d", docId), ("$t", text), ("$s", section), ("$e", bytes), ("$m", model), ("$r", runId));
    }

    public void SetVersionChunks(string docId, string version, IReadOnlyList<(string ChunkId, int Start, int End, int FirstBlock, int LastBlock)> chunks)
    {
        Execute("DELETE FROM version_chunks WHERE doc_id=$d AND version=$v", ("$d", docId), ("$v", version));
        for (var i = 0; i < chunks.Count; i++)
        {
            Execute("INSERT INTO version_chunks VALUES ($d,$v,$c,$o,$s,$e,$f,$l)", ("$d", docId), ("$v", version), ("$c", chunks[i].ChunkId),
                ("$o", i), ("$s", chunks[i].Start), ("$e", chunks[i].End), ("$f", chunks[i].FirstBlock), ("$l", chunks[i].LastBlock));
        }
    }

    /// <summary>
    /// The chunks of every document version a reader may see on <paramref name="asOf"/>: not tombstoned, not a duplicate,
    /// the reader in one of its groups and the date inside its effective window. <see langword="null"/> ignores dates.
    /// </summary>
    public IReadOnlyList<(StoredChunk Chunk, string Version, DateOnly? From, DateOnly? To)> Searchable(IReadOnlySet<string> groups, DateOnly? asOf) => Query("""
        SELECT c.chunk_id, c.doc_id, c.text, c.section, c.embedding, d.version, d.effective_from, d.effective_to, d.groups
        FROM version_chunks vc JOIN chunks c ON c.chunk_id = vc.chunk_id
        JOIN documents d ON d.doc_id = vc.doc_id AND d.version = vc.version
        WHERE d.tombstoned = 0 AND d.duplicate_of IS NULL
          AND ($asof IS NULL OR ((d.effective_from IS NULL OR d.effective_from <= $asof) AND (d.effective_to IS NULL OR d.effective_to >= $asof)))
        """,
        r => (new StoredChunk(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Floats((byte[])r[4])), r.GetString(5),
              r.IsDBNull(6) ? (DateOnly?)null : DateOnly.Parse(r.GetString(6)), r.IsDBNull(7) ? (DateOnly?)null : DateOnly.Parse(r.GetString(7)), r.GetString(8)),
        ("$asof", asOf?.ToString("yyyy-MM-dd")))
        .Where(x => x.Item5.Split(',').Any(groups.Contains))
        .Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).ToList();

    public (int Chunks, int Embedded) ChunkCounts() =>
        (QuerySingle("SELECT COUNT(*) FROM version_chunks", r => r.GetInt32(0)), QuerySingle("SELECT COUNT(*) FROM chunks", r => r.GetInt32(0)));

    // ---- lineage, runs and quarantine ----

    public void AddLineageEvent(string runId, string eventType, string job, JsonElement json) =>
        Execute("INSERT INTO lineage_events VALUES ($r,$t,$e,$j,$x)", ("$r", runId), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$e", eventType), ("$j", job), ("$x", json.GetRawText()));

    public void SaveRun(string runId, DateTimeOffset started, DateTimeOffset ended, string state, string summary) =>
        Execute("INSERT OR REPLACE INTO runs VALUES ($r,$s,$e,$st,$su)", ("$r", runId), ("$s", started.ToString("O")), ("$e", ended.ToString("O")), ("$st", state), ("$su", summary));

    public int Quarantine(string system, string uri, string reason, string detail)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute("""
            INSERT INTO quarantine VALUES ($s,$u,$r,$d,1,$n,$n)
            ON CONFLICT (system, uri) DO UPDATE SET reason=$r, detail=$d, attempts=attempts+1, last_seen=$n
            """, ("$s", system), ("$u", uri), ("$r", reason), ("$d", detail), ("$n", now));
        return QuerySingle("SELECT attempts FROM quarantine WHERE system=$s AND uri=$u", r => r.GetInt32(0), ("$s", system), ("$u", uri));
    }

    public void Release(string system, string uri) =>
        Execute("DELETE FROM quarantine WHERE system=$s AND uri=$u", ("$s", system), ("$u", uri));

    public IReadOnlyList<(string System, string Uri, string Reason, string Detail, int Attempts)> Quarantined() =>
        Query("SELECT system, uri, reason, detail, attempts FROM quarantine ORDER BY system, uri", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4)));

    // ---- plumbing ----

    // A SQLite connection isn't thread-safe and pipeline stages run in parallel, so every call takes one lock.
    // The lock is reentrant, so a transaction can hold it while its own commands run.
    public T? QuerySingle<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            return reader.Read() ? map(reader) : default;
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(map(reader));
            return result;
        }
    }

    public void Execute(string sql, params (string, object?)[] parameters)
    {
        lock (_gate)
        {
            using var command = Command(sql, parameters);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Runs <paramref name="work"/> in one transaction, holding the lock so that no other stage's writes interleave.</summary>
    public void InTransaction(Action work)
    {
        lock (_gate)
        {
            _transaction = _connection.BeginTransaction();
            try
            {
                work();
                _transaction.Commit();
            }
            catch
            {
                _transaction.Rollback();
                throw;
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
            }
        }
    }

    private SqliteCommand Command(string sql, (string, object?)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static float[] Floats(byte[] bytes)
    {
        var result = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>One document version as the store keeps it.</summary>
public sealed record DocumentRow(
    string DocId, string Version, string System, string Uri, string Title, string Tenant, string DocType, string Sensitivity,
    IReadOnlyList<string> Groups, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string Sha256, string TextHash, string Parser,
    string Artifact, string? DuplicateOf, string RunId)
{
    public string? SupersededBy { get; init; }

    public bool Tombstoned { get; init; }
}
