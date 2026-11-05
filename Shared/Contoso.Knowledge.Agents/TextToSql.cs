using System.Globalization;
using System.Numerics.Tensors;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Knowledge.Query;
using DuckDB.NET.Data;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Agents;

/// <summary>One question answered with model-written SQL: the tables shown, the SQL, what the guard or DuckDB said, the rows and the cost.</summary>
public sealed record SqlAttempt(string Question, IReadOnlyList<string> Tables, string Sql, string? Rejected, string? Failed, QueryResult? Result,
    int Calls, double Milliseconds, int PromptTokens)
{
    public bool Ran => Result is not null;
}

/// <summary>
/// Schema subsetting: the tables whose descriptions are closest to the question (bge-small), so the prompt carries
/// only what the question needs. Contoso has four tables; a maintenance system's real schema has hundreds.
/// </summary>
public sealed class TableSelector
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _queries;
    private readonly Dictionary<string, float[]> _tables;

    private TableSelector(IEmbeddingGenerator<string, Embedding<float>> queries, Dictionary<string, float[]> tables) => (_queries, _tables) = (queries, tables);

    public static async Task<TableSelector> CreateAsync(IEmbeddingGenerator<string, Embedding<float>> passages, IEmbeddingGenerator<string, Embedding<float>> queries)
    {
        var cards = RecordsSchema.Tables.Select(t => (t.Key, Text: $"{t.Key.Replace('_', ' ')}: {t.Value}. " +
            string.Join("; ", RecordsSchema.Columns.Where(c => c.Key.StartsWith(t.Key + ".", StringComparison.Ordinal)).Select(c => $"{c.Key[(t.Key.Length + 1)..].Replace('_', ' ')} ({c.Value})")))).ToList();
        var vectors = await passages.GenerateAsync(cards.Select(c => c.Text));
        return new TableSelector(queries, cards.Select((c, i) => (c.Key, vectors[i].Vector.ToArray())).ToDictionary(x => x.Key, x => x.Item2));
    }

    public async Task<IReadOnlyList<string>> SelectAsync(string question, IReadOnlyList<string> views, int top = 2, CancellationToken cancellationToken = default)
    {
        var q = (await _queries.GenerateAsync([question], cancellationToken: cancellationToken))[0].Vector.ToArray();
        return [.. views.Where(_tables.ContainsKey).OrderByDescending(v => TensorPrimitives.CosineSimilarity(q, _tables[v])).Take(top)];
    }
}

/// <summary>
/// Text-to-SQL with llama3.2: the prompt carries the schema of the tables chosen, optionally three worked examples,
/// and the question; the SQL runs only if <see cref="SqlGuard"/> accepts it, and a rejected or failed statement can
/// be sent back once with the error.
/// </summary>
public sealed partial class TextToSql(IChatClient chat, LlmCache cache, Tokenizer tokenizer)
{
    public const string Instructions = """
        Write one DuckDB SELECT statement that answers the question from the tables below. Today is {today}.
        Use only these tables and columns. Reply with the SQL statement only.
        """;

    /// <summary>Worked examples, written for the prompt and not taken from the evaluation's questions.</summary>
    public static IReadOnlyList<(string Question, string Sql)> Examples { get; } =
    [
        ("How many work orders for V-114 were overdue in 2026?", "SELECT count(*) AS overdue FROM work_orders WHERE asset_tag = 'V-114' AND overdue AND year(due) = 2026"),
        ("Which open work orders at Northgate are past their due date?", "SELECT work_order, asset_tag, due FROM work_orders WHERE site = 'Northgate' AND status = 'open' AND overdue"),
        ("Which parts does Litware Bearings supply, and how many are in stock at Riverside?", "SELECT part_number, description, stock_riverside FROM parts WHERE supplier = 'Litware Bearings'"),
    ];

    public async Task<SqlAttempt> RunAsync(string question, RecordsSession session, IReadOnlyList<string> tables, bool examples, bool repair, CancellationToken cancellationToken = default)
    {
        var (calls, ms) = (cache.Calls, cache.Milliseconds);
        var prompt = Prompt(question, session.Describe(tables), examples && session.Semantic);
        var tokens = tokenizer.CountTokens(prompt);
        var sql = Extract(await cache.GetAsync(chat, "text-to-sql", prompt, 200, cancellationToken));
        var (rejected, failed, result) = Execute(session, sql);
        if (repair && result is null)
        {
            var retry = $"{prompt}\n{sql}\n\nThat statement didn't run: {rejected ?? failed}\nWrite a corrected statement.\nSQL:";
            tokens += tokenizer.CountTokens(retry);
            sql = Extract(await cache.GetAsync(chat, "text-to-sql-repair", retry, 200, cancellationToken));
            (rejected, failed, result) = Execute(session, sql);
        }

        return new SqlAttempt(question, tables, sql, rejected, failed, result, cache.Calls - calls, cache.Milliseconds - ms, tokens);
    }

    public static string Prompt(string question, string schema, bool examples)
    {
        var text = new StringBuilder(Instructions.ReplaceLineEndings("\n").Replace("{today}", RecordsDatabase.Today.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)));
        text.Append("\n\n").Append(schema).Append("\n\n");
        if (examples)
        {
            foreach (var (q, s) in Examples) text.Append($"Question: {q}\nSQL: {s}\n\n");
        }

        return text.Append($"Question: {question}\nSQL:").ToString();
    }

    private static (string? Rejected, string? Failed, QueryResult? Result) Execute(RecordsSession session, string sql)
    {
        try
        {
            return (null, null, session.Query(sql));
        }
        catch (SqlRejectedException e)
        {
            return (e.Message, null, null);
        }
        catch (DuckDBException e)
        {
            return (null, e.Message.Split('\n')[0], null);
        }
    }

    /// <summary>The statement in a reply: inside a code fence if there is one, without a trailing semicolon.</summary>
    public static string Extract(string reply)
    {
        var fenced = Fence().Match(reply);
        var sql = (fenced.Success ? fenced.Groups[1].Value : reply).Trim();
        return sql.EndsWith(';') ? sql[..^1].TrimEnd() : sql;
    }

    [GeneratedRegex(@"```(?:sql)?\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fence();
}
