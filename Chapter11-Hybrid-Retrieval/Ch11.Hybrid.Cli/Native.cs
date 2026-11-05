using System.Diagnostics;
using Contoso.Knowledge.Embeddings;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Npgsql;
using Testcontainers.PostgreSql;
using PgVector = Pgvector.Vector;

namespace Ch11.Hybrid.Cli;

/// <summary>
/// Hybrid search inside PostgreSQL: full-text search over a tsvector column and pgvector's HNSW index, each ranked by
/// parent and cut to 20, fused with RRF in one SQL statement, the asker's groups in both WHERE clauses.
/// </summary>
public static class Native
{
    private const string Image = "pgvector/pgvector:0.8.7-pg17";

    // websearch_to_tsquery ANDs the question's words, and a long question rarely matches all of them, so the
    // keyword side ORs the question's lexemes instead.
    private const string Lexemes = "to_tsquery('english', array_to_string(tsvector_to_array(to_tsvector('english', @question)), ' | '))";

    public const string HybridSql = $"""
        WITH keyword AS (
            SELECT parent, row_number() OVER (ORDER BY max(ts_rank_cd(tsv, query)) DESC) AS rank
            FROM chunks, {Lexemes} AS query
            WHERE tsv @@ query AND allowed_groups && @groups
            GROUP BY parent ORDER BY rank LIMIT 20),
        dense AS (
            SELECT parent, row_number() OVER (ORDER BY min(distance)) AS rank
            FROM (SELECT parent, embedding <=> @embedding AS distance FROM chunks
                  WHERE allowed_groups && @groups ORDER BY embedding <=> @embedding LIMIT 100) AS nearest
            GROUP BY parent ORDER BY rank LIMIT 20)
        SELECT coalesce(k.parent, d.parent) AS parent,
               coalesce(1.0 / (60 + k.rank), 0) + coalesce(1.0 / (60 + d.rank), 0) AS score
        FROM keyword AS k FULL OUTER JOIN dense AS d ON k.parent = d.parent
        ORDER BY score DESC, parent LIMIT @top
        """;

    private const string KeywordAndSql = """
        SELECT parent, max(ts_rank_cd(tsv, query)) AS score
        FROM chunks, websearch_to_tsquery('english', @question) AS query
        WHERE tsv @@ query AND allowed_groups && @groups
        GROUP BY parent ORDER BY score DESC, parent LIMIT @top
        """;

    private const string KeywordOrSql = $"""
        SELECT parent, max(ts_rank_cd(tsv, query)) AS score
        FROM chunks, {Lexemes} AS query
        WHERE tsv @@ query AND allowed_groups && @groups
        GROUP BY parent ORDER BY score DESC, parent LIMIT @top
        """;

    public static async Task RunAsync(Lab lab)
    {
        await using var postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        var builder = new NpgsqlDataSourceBuilder(postgres.GetConnectionString());
        builder.UseVector();
        await using var dataSource = builder.Build();

        using var model = Lab.Bge();
        var passages = new OnnxEmbeddingGenerator(model);
        var vectors = await passages.GenerateAsync(lab.Chunks.Select(c => c.Chunk.IndexText));
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await Execute(connection, "CREATE EXTENSION IF NOT EXISTS vector");
            await connection.ReloadTypesAsync();   // the vector type exists only now
            await Execute(connection, """
                CREATE TABLE chunks (
                    ordinal int PRIMARY KEY, parent text NOT NULL, allowed_groups text[] NOT NULL, text text NOT NULL,
                    tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', text)) STORED,
                    embedding vector(384) NOT NULL)
                """);
            await using (var writer = await connection.BeginBinaryImportAsync("COPY chunks (ordinal, parent, allowed_groups, text, embedding) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var chunk in lab.Chunks)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(chunk.Ordinal);
                    await writer.WriteAsync(Parent(chunk));
                    await writer.WriteAsync(chunk.Document.AllowedGroups.ToArray());
                    await writer.WriteAsync(chunk.Chunk.IndexText);
                    await writer.WriteAsync(new PgVector(vectors[chunk.Ordinal].Vector));
                }

                await writer.CompleteAsync();
            }

            await Execute(connection, "CREATE INDEX ON chunks USING gin (tsv)");
            await Execute(connection, "CREATE INDEX ON chunks USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 200)");
            await Execute(connection, "ANALYZE chunks");
        }

        var parents = lab.Chunks.GroupBy(Parent).ToDictionary(g => g.Key, g => g.First());
        var queries = new OnnxEmbeddingGenerator(model, queries: true);
        using var keyword = Commands.Keyword(lab);
        var dense = await lab.DenseAsync(model);
        var configurations = new IRetriever[]
        {
            new SqlRetriever("PostgreSQL full-text search, all words", KeywordAndSql, dataSource, null, parents),
            new SqlRetriever("PostgreSQL full-text search, any word", KeywordOrSql, dataSource, null, parents),
            new SqlRetriever("PostgreSQL hybrid, one statement, RRF", HybridSql, dataSource, queries, parents),
            new HybridRetriever("application hybrid: Lucene + dense, RRF", [keyword, dense], new ReciprocalRankFusion(60)),
        };

        Console.WriteLine($"{Image}: {lab.Chunks.Count} chunks, GIN index on to_tsvector('english', text), HNSW (m 16, ef_construction 200), " +
            "ef_search 160, iterative scans");
        Console.WriteLine();
        Console.WriteLine($"{"Configuration",-44} {"Recall@4",8} {"nDCG@4",7} {"Suff.",6} {"p50 ms",7} {"p95 ms",7}");
        var tops = new List<Dictionary<string, HashSet<(string, int, int)>>>();
        foreach (var retriever in configurations)
        {
            var run = await lab.RunAsync(retriever);
            var (latencies, top) = (new List<double>(), new Dictionary<string, HashSet<(string, int, int)>>());
            foreach (var question in lab.Answerable)
            {
                var asker = Contoso.Corpus.People.Find(question.Asker)!;
                var stopwatch = Stopwatch.StartNew();
                var result = await retriever.RetrieveAsync(new RetrievalRequest(question.Question, asker, 4));
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                top[question.Id] = [.. result.Hits.Select(h => h.Chunk.ReturnKey)];
            }

            tops.Add(top);
            latencies.Sort();
            Console.WriteLine($"{run.Configuration,-44} {run.Mean("Recall"),8:F2} {run.Mean("nDCG"),7:F2} {run.Mean("Sufficient"),6:F2} " +
                $"{latencies[latencies.Count / 2],7:F1} {latencies[(int)(latencies.Count * 0.95)],7:F1}");
        }

        var same = lab.Answerable.Count(q => tops[2][q.Id].SetEquals(tops[3][q.Id]));
        Console.WriteLine($"Same four parents from both hybrids: {same} of {lab.Answerable.Count()} questions; latency includes embedding the question");
    }

    private static string Parent(IndexedChunk chunk) => $"{chunk.Chunk.Doc}|{chunk.Chunk.ReturnStart}|{chunk.Chunk.ReturnEnd}";

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An SQL statement as a retriever: it returns parent keys and scores, mapped back to chunks for the harness.</summary>
    private sealed class SqlRetriever(string name, string sql, NpgsqlDataSource dataSource, IEmbeddingGenerator<string, Embedding<float>>? queries,
        IReadOnlyDictionary<string, IndexedChunk> parents) : IRetriever
    {
        public string Name => name;

        public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var settings = new NpgsqlCommand("SET hnsw.ef_search = 160; SET hnsw.iterative_scan = relaxed_order", connection))
            {
                await settings.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("question", request.Question);
            command.Parameters.AddWithValue("groups", request.Asker.Groups.ToArray());
            command.Parameters.AddWithValue("top", request.Top);
            if (queries is not null)
            {
                var embedding = (await queries.GenerateAsync([request.Question], cancellationToken: cancellationToken))[0].Vector;
                command.Parameters.AddWithValue("embedding", new PgVector(embedding));
            }

            var hits = new List<Hit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new Hit(parents[reader.GetString(0)], Convert.ToDouble(reader.GetValue(1)), name));
            }

            return RetrievalResult.Of(hits);
        }
    }
}
