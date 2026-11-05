using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using DuckDB.NET.Data;
using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Agents;

/// <summary>Which work orders a search shows, read from the model's status slot.</summary>
public enum WorkStatus { Any, Open, Closed, Overdue }

/// <summary>Preventive (scheduled) or corrective (repair) work, read from the model's type slot.</summary>
public enum WorkType { Any, Preventive, Corrective }

[JsonConverter(typeof(JsonStringEnumConverter<Priority>))]
public enum Priority
{
    [JsonStringEnumMemberName("P1")] P1,
    [JsonStringEnumMemberName("P2")] P2,
    [JsonStringEnumMemberName("P3")] P3,
}

/// <summary>Which tools a person may use. The model never sees the others, and the invoker refuses them if it asks anyway.</summary>
public sealed record ToolPolicy(IReadOnlySet<string> Allowed)
{
    public bool Allows(string tool) => Allowed.Contains(tool);

    public static ToolPolicy For(Person person)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { ContosoTools.SearchDocuments };
        if (AssetRegister.UnitsFor(person).Count > 0) allowed.UnionWith([ContosoTools.FindWorkOrders, ContosoTools.DraftWorkOrder]);
        if (person.Groups.Contains("employees")) allowed.UnionWith([ContosoTools.FindParts, ContosoTools.QueryRecords]);
        return new ToolPolicy(allowed);
    }
}

/// <summary>What one request's tools share: who asks, their records session, the evidence so far and the trajectory.</summary>
public sealed class AgentContext(Person asker, RecordsSession records, ToolPolicy policy) : IDisposable
{
    public Person Asker => asker;

    public RecordsSession Records => records;

    public ToolPolicy Policy => policy;

    public EvidenceLedger Ledger { get; } = new();

    public TrajectoryLog Trajectory { get; } = new();

    /// <summary>Work orders drafted after a person approved them, for the maintenance planner; nothing writes to the records.</summary>
    public List<string> Drafts { get; } = [];

    public void Dispose() => records.Dispose();
}

/// <summary>
/// The assistant's tools for one request. Who is asking comes from <see cref="AgentContext"/>, never from an
/// argument, so a model can't search as someone else; every result goes into the request's evidence ledger with the
/// number the answer will cite. The model fills typed slots; the application writes the SQL.
/// </summary>
public sealed partial class ContosoTools(AgentContext context, IRetriever retriever, ContextAssembler assembler, ExtractiveCompressor? compressor = null)
{
    public const string SearchDocuments = "search_documents", FindWorkOrders = "find_work_orders", FindParts = "find_parts",
        QueryRecords = "query_records", DraftWorkOrder = "draft_work_order";

    /// <summary>
    /// The tools the person may use. Model-written SQL (<see cref="QueryRecords"/>) is left out unless asked for: in
    /// the first build it drew document questions to the database (Section 17.3). Drafting waits for a person's approval.
    /// </summary>
    public IList<AITool> Create(bool sql = false)
    {
        var all = new List<AITool>
        {
            AIFunctionFactory.Create(SearchAsync, new AIFunctionFactoryOptions
            {
                Name = SearchDocuments,
                Description = "Searches the Contoso documents the caller may read: equipment manuals, procedures, safety bulletins, asset records, change notices and service reports. Returns up to four sources.",
            }),
            AIFunctionFactory.Create(FindAsync, new AIFunctionFactoryOptions
            {
                Name = FindWorkOrders,
                Description = "Finds and counts maintenance work orders (the history of maintenance done on equipment): how many, which ones, when they were due or done, by whom, which are open or overdue. Newest first.",
            }),
            AIFunctionFactory.Create(PartsAsync, new AIFunctionFactoryOptions
            {
                Name = FindParts,
                Description = "Looks up spare parts in the parts catalog: supplier, lead time, stock at Riverside, price and the part that replaced a superseded one.",
            }),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(Draft, new AIFunctionFactoryOptions
            {
                Name = DraftWorkOrder,
                Description = "Drafts a corrective work order for the maintenance planner to review. A person must approve it first.",
            })),
        };
        if (sql)
        {
            all.Add(AIFunctionFactory.Create(QueryAsync, new AIFunctionFactoryOptions
            {
                Name = QueryRecords,
                Description = $"Runs one read-only DuckDB SELECT statement against the maintenance records and returns at most {context.Records.MaxRows} rows. Tables:\n{context.Records.Describe()}",
            }));
        }

        return [.. all.Where(t => context.Policy.Allows(t.Name))];
    }

    private async Task<string> SearchAsync(
        [Description("What to look for, in a few plain words with any equipment tag, model or part number.")] string query,
        CancellationToken cancellationToken = default)
    {
        var result = await retriever.RetrieveAsync(new RetrievalRequest(query, context.Asker, 4), cancellationToken);
        var found = assembler.Assemble(result.Hits);
        if (found.IsEmpty) return "No relevant passages in the documents you can read.";
        return EvidenceLedger.Describe(context.Ledger.AddPassages((compressor?.Compress(query, found) ?? found).Blocks));
    }

    private Task<string> FindAsync(
        [Description("Equipment tag, such as P-201. Leave empty for all equipment.")] string? assetTag = null,
        [Description("Riverside, Northgate, Lakeside or Eastfield. Leave empty for all sites.")] string? site = null,
        [Description("Due dates to include: any, last_quarter, this_quarter, last_month, this_year, last_year, a year such as 2026, a quarter such as 2026-Q4 or a month such as 2026-11.")] string? period = null,
        [Description("open, closed, overdue (completed after the due date or still open after it) or any.")] string? status = null,
        [Description("preventive (scheduled), corrective (repairs) or any.")] string? type = null,
        [Description("A word or code the description must contain, such as 'seal' or 'E-217'.")] string? text = null)
    {
        if (!context.Records.Views.Contains("work_orders")) return Task.FromResult("You can't read work orders.");
        (assetTag, site, period, status, type, text) = (Slot(assetTag), Slot(site), Slot(period), Slot(status), Slot(type), Slot(text));

        // A small model fills slots loosely: a work type in the status slot is read as the type it names.
        if (status is not null && WorkTypeOf(status) is not null && type is null) (type, status) = (status, null);
        var (shown, kind) = (WorkStatusOf(status), WorkTypeOf(type));
        if (shown is null || kind is null || !TryPeriod(period, RecordsDatabase.Today, out var range, out var when))
        {
            return Task.FromResult("Unknown value. Use status open, closed, overdue or any; type preventive, corrective or any; period any, last_quarter, " +
                "this_quarter, last_month, this_year, last_year, a year (2026), a quarter (2026-Q4) or a month (2026-11).");
        }

        // The application writes the SQL, with parameters: the model fills typed slots and never writes a query.
        var (where, args, scope) = (new List<string>(), new List<object?>(), new List<string>());
        if (assetTag is not null) { where.Add("replace(upper(asset_tag), '-', '') = replace(upper(?), '-', '')"); args.Add(assetTag); scope.Add(assetTag.ToUpperInvariant()); }
        if (site is not null) { where.Add("lower(site) = lower(?)"); args.Add(site); scope.Add($"at {site}"); }
        if (kind != WorkType.Any) { where.Add("type = ?"); args.Add(kind == WorkType.Preventive ? "PM" : "CM"); scope.Add(kind == WorkType.Preventive ? "preventive" : "corrective"); }
        if (text is not null) { where.Add("replace(lower(description), '-', '') LIKE '%' || replace(lower(?), '-', '') || '%'"); args.Add(text); scope.Add($"mentioning {text}"); }
        if (range is var (from, to)) { where.Add("due BETWEEN ? AND ?"); args.AddRange([from, to]); }
        var filter = where.Count == 0 ? " WHERE TRUE" : " WHERE " + string.Join(" AND ", where);
        var today = RecordsDatabase.Today;

        // Counts over everything that matches, so the answer has a sentence to quote: "0 of the 3 ... were overdue".
        var counts = context.Records.QueryTrusted(
            $"SELECT count(*), count(*) FILTER (WHERE coalesce(completed, ?) > due), count(*) FILTER (WHERE status = 'open') FROM work_orders{filter}", [today, .. args]);
        var (matched, overdue, open) = (int.Parse(counts.Rows[0][0], CultureInfo.InvariantCulture), counts.Rows[0][1], counts.Rows[0][2]);
        var condition = shown switch
        {
            WorkStatus.Overdue => " AND coalesce(completed, ?) > due",
            WorkStatus.Open => " AND status = 'open'",
            WorkStatus.Closed => " AND status = 'closed'",
            _ => "",
        };
        var rows = context.Records.QueryTrusted(
            $"SELECT work_order, asset_tag, site, type, status, due, completed, date_diff('day', due, coalesce(completed, ?)) AS days_late, description, technician " +
            $"FROM work_orders{filter}{condition} ORDER BY due DESC", [today, .. args, .. (shown == WorkStatus.Overdue ? new object?[] { today } : Array.Empty<object?>())]);

        var subject = scope.Count == 0 ? "work orders" : $"work orders ({string.Join(", ", scope)})";
        var due = when.Length == 0 ? "" : $" due {when}";
        var summary = matched == 0 ? $"No {subject} you can read{due}." : shown switch
        {
            WorkStatus.Overdue => $"{overdue} of the {matched} {subject} you can read{due} were overdue (completed after the due date or still open after it).",
            WorkStatus.Open => $"{open} of the {matched} {subject} you can read{due} are still open.",
            WorkStatus.Closed => $"{rows.Rows.Count} of the {matched} {subject} you can read{due} are closed.",
            _ => $"{matched} {subject} you can read{due}: {overdue} of them overdue (completed after the due date or still open after it), {open} still open.",
        };
        var block = context.Ledger.AddQuery(rows, $"Work orders: {string.Join(", ", scope.DefaultIfEmpty("all"))}{(when.Length == 0 ? "" : $", due {when}")}", summary);
        return Task.FromResult(EvidenceLedger.Describe([block]));
    }

    private Task<string> PartsAsync(
        [Description("A part number such as PN-4471-C, a supplier such as Fabrikam Seals, a model such as CP-200 or words such as 'mechanical seal'.")] string query)
    {
        query = Slot(query) ?? "";
        var rows = PartNumber().IsMatch(query)
            // A part number brings its replacement and what it replaced along: the follow-up question is usually about them.
            ? context.Records.QueryTrusted(Parts + " WHERE upper(p.part_number) = upper(?) OR p.part_number IN (SELECT replaced_by FROM superseded_parts WHERE upper(old_part) = upper(?))" +
                " OR p.part_number IN (SELECT old_part FROM superseded_parts WHERE upper(replaced_by) = upper(?)) ORDER BY p.part_number", query, query, query)
            : context.Records.QueryTrusted(Parts + " WHERE lower(p.supplier) LIKE '%' || lower(?) || '%' OR lower(p.fits) LIKE '%' || lower(?) || '%'" +
                " OR lower(p.description) LIKE '%' || lower(?) || '%' ORDER BY p.part_number", query, query, query);
        return Task.FromResult(EvidenceLedger.Describe([context.Ledger.AddQuery(rows, $"Parts catalog: {(query.Length == 0 ? "all parts" : query)}")]));
    }

    private const string Parts =
        "SELECT p.part_number, p.description, p.fits, p.supplier, p.lead_time_weeks, p.stock_riverside, p.net_price, s.replaced_by, s.change_notice " +
        "FROM parts p LEFT JOIN superseded_parts s ON s.old_part = p.part_number";

    /// <summary>A slot as the model filled it, or null for the ways a small model writes "nothing": "", "null", "none", "any".</summary>
    public static string? Slot(string? value) =>
        value?.Trim() is { Length: > 0 } v && v.ToLowerInvariant() is not ("null" or "none" or "n/a" or "any" or "all" or "*") ? v : null;

    private static WorkStatus? WorkStatusOf(string? value) => value?.ToLowerInvariant() switch
    {
        null => WorkStatus.Any,
        "open" => WorkStatus.Open,
        "closed" or "done" or "completed" => WorkStatus.Closed,
        "overdue" or "late" => WorkStatus.Overdue,
        _ => null,
    };

    private static WorkType? WorkTypeOf(string? value) => value?.ToLowerInvariant() switch
    {
        null => WorkType.Any,
        "preventive" or "pm" or "scheduled" => WorkType.Preventive,
        "corrective" or "cm" or "repair" or "repairs" => WorkType.Corrective,
        _ => null,
    };

    private Task<string> QueryAsync([Description("One DuckDB SELECT statement over the tables listed.")] string sql)
    {
        try
        {
            var result = context.Records.Query(sql);
            return Task.FromResult(EvidenceLedger.Describe([context.Ledger.AddQuery(result, "Maintenance records: query")]));
        }
        catch (SqlRejectedException e)
        {
            return Task.FromResult($"Rejected: {e.Message}");
        }
        catch (DuckDBException e)
        {
            return Task.FromResult($"The query failed: {e.Message.Split('\n')[0]}");
        }
    }

    private string Draft(
        [Description("Equipment tag, such as P-202.")] string assetTag,
        [Description("What needs doing, in a sentence.")] string description,
        [Description("P1 (most urgent) to P3.")] Priority priority = Priority.P3)
    {
        context.Drafts.Add($"{assetTag} {priority}: {description}");
        return $"Drafted a {priority} work order for {assetTag} for the maintenance planner: {description}";
    }

    /// <summary>
    /// A period as the model wrote it, to due dates counted back from Contoso's today. Ollama doesn't hold tool
    /// arguments to their schema, so this slot is a string that code parses, with an error that lists what it accepts.
    /// </summary>
    public static bool TryPeriod(string? period, DateOnly today, out (DateOnly From, DateOnly To)? range, out string label)
    {
        var quarter = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        var month = new DateOnly(today.Year, today.Month, 1);
        var text = (period ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        (DateOnly From, DateOnly To)? found = text switch
        {
            "last_quarter" => (quarter.AddMonths(-3), quarter.AddDays(-1)),
            "this_quarter" => (quarter, quarter.AddMonths(3).AddDays(-1)),
            "last_month" => (month.AddMonths(-1), month.AddDays(-1)),
            "this_year" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            "last_year" => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
            _ when Year().Match(text) is { Success: true } y => YearOf(int.Parse(y.Value, CultureInfo.InvariantCulture)),
            _ when Quarter().Match(text) is { Success: true } q => QuarterOf(int.Parse(q.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(q.Groups["q"].Value, CultureInfo.InvariantCulture)),
            _ when Month().Match(text) is { Success: true } m => MonthOf(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture)),
            _ => null,
        };
        range = found;
        label = found is { } r ? $"from {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}" : "";
        return found is not null || text is "" or "any" or "all";
    }

    private static (DateOnly, DateOnly) YearOf(int year) => (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    private static (DateOnly, DateOnly) QuarterOf(int year, int q) => (new DateOnly(year, q * 3 - 2, 1), new DateOnly(year, q * 3 - 2, 1).AddMonths(3).AddDays(-1));

    private static (DateOnly, DateOnly) MonthOf(int year, int m) => (new DateOnly(year, m, 1), new DateOnly(year, m, 1).AddMonths(1).AddDays(-1));

    [GeneratedRegex("^PN-[0-9]{4}-[A-Z]$", RegexOptions.IgnoreCase)]
    private static partial Regex PartNumber();

    [GeneratedRegex("^(19|20)[0-9]{2}$")]
    private static partial Regex Year();

    [GeneratedRegex("^(?:(?<y>(?:19|20)[0-9]{2})_?-?_?q(?<q>[1-4])|q(?<q>[1-4])_?-?_?(?<y>(?:19|20)[0-9]{2}))$")]
    private static partial Regex Quarter();

    [GeneratedRegex("^(?<y>(?:19|20)[0-9]{2})-(?<m>0?[1-9]|1[0-2])$")]
    private static partial Regex Month();
}
