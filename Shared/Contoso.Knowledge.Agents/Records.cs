using System.Globalization;
using System.Numerics;
using System.Text;
using Contoso.Corpus;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;
using DuckDB.NET.Data;

namespace Contoso.Knowledge.Agents;

/// <summary>An asset in the maintenance system's register and the business unit whose people may read its records.</summary>
public sealed record Asset(string Tag, string Model, string Site, string BusinessUnit);

/// <summary>The assets in Contoso's work-order export, as the maintenance system registers them.</summary>
public static class AssetRegister
{
    public static IReadOnlyList<Asset> All { get; } =
    [
        new("P-201", "CP-200 centrifugal pump", "Riverside", "fluid"),
        new("P-202", "CP-200 centrifugal pump", "Riverside", "fluid"),
        new("P-301", "CP-200 centrifugal pump", "Northgate", "fluid"),
        new("V-114", "Seal-water valve", "Riverside", "fluid"),
        new("GBX-07", "HG-400 gearbox", "Lakeside", "motion"),
        new("HG-1102", "HG-400 gearbox", "Eastfield", "motion"),
        new("CNV-12", "Conveyor with VD-75 drive", "Lakeside", "motion"),
    ];

    /// <summary>
    /// The business units whose asset records a person may read: Field Services works on everything, the
    /// engineering tenants on their own equipment, and everyone else on none (the export's ACL is fluid, motion, service).
    /// </summary>
    public static IReadOnlyList<string> UnitsFor(Person person) =>
        person.Groups.Contains("service") ? ["fluid", "motion"] : [.. new[] { "fluid", "motion" }.Where(person.Groups.Contains)];
}

/// <summary>A query's rows as text, and whether the row limit cut them off.</summary>
public sealed record QueryResult(string Sql, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, double Milliseconds)
{
    /// <summary>One line per row with its column names, the form Chapter 14 found llama3.2 reads tables best in.</summary>
    public string Render()
    {
        var text = new StringBuilder(Rows.Count switch { 0 => "No rows.", 1 => "1 row.", var n => $"{n} rows{(Truncated ? " (more rows exist; only these were returned)" : "")}." });
        foreach (var row in Rows) text.Append('\n').AppendJoin("; ", Columns.Select((c, i) => $"{c}: {row[i]}"));
        return text.ToString();
    }
}

public sealed class SqlRejectedException(string reason) : Exception(reason);

/// <summary>
/// Contoso's maintenance records in DuckDB: the nightly work-order export (CSV), the parts catalog (a workbook with
/// two header rows on each sheet) and the asset register. <see cref="BuildAsync"/> writes them to one file once;
/// <see cref="Open"/> gives one person a session over that file, read-only and behind views that hold only their rows.
/// </summary>
public sealed class RecordsDatabase(string path)
{
    public string Path { get; } = path;

    // The catalog's header rows name its columns for people; this is the schema someone had to write down for SQL.
    private static readonly (string Header, string Column, string Type)[] PartColumns =
    [
        ("Part number", "part_number", "VARCHAR"), ("Description", "description", "VARCHAR"), ("Fits", "fits", "VARCHAR"),
        ("Supplier", "supplier", "VARCHAR"), ("Lead time (weeks)", "lead_time_weeks", "INTEGER"), ("Stock at Riverside", "stock_riverside", "INTEGER"),
        ("List price", "list_price", "DECIMAL(10,2)"), ("Discount", "discount_percent", "INTEGER"), ("Net price", "net_price", "DECIMAL(10,2)"),
    ];

    private static readonly (string Header, string Column, string Type)[] SupersededColumns =
    [
        ("Old part", "old_part", "VARCHAR"), ("Replaced by", "replaced_by", "VARCHAR"), ("Change notice", "change_notice", "VARCHAR"),
        ("Effective", "effective", "DATE"), ("Instruction", "instruction", "VARCHAR"),
    ];

    public static async Task<RecordsDatabase> BuildAsync(string path, string workOrdersCsv, string partsWorkbook, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var connection = new DuckDBConnection($"Data Source={path}");
        connection.Open();

        // The CSV goes in as DuckDB reads it; dates are typed so that "overdue" is date arithmetic, not string comparison.
        Execute(connection, $"""
            CREATE TABLE work_orders AS
            SELECT work_order, asset_tag, site, type, priority, description, status,
                   CAST(opened AS DATE) AS opened, CAST(due AS DATE) AS due, CAST(completed AS DATE) AS completed, technician
            FROM read_csv('{Literal(workOrdersCsv)}', header = true, all_varchar = true)
            """);

        Execute(connection, "CREATE TABLE assets (asset_tag VARCHAR PRIMARY KEY, model VARCHAR, site VARCHAR, business_unit VARCHAR)");
        foreach (var a in AssetRegister.All) Insert(connection, "assets", [a.Tag, a.Model, a.Site, a.BusinessUnit]);

        // The workbook through Chapter 7's parser: one table per sheet, merged header cells repeated, two header rows.
        await using var workbook = File.OpenRead(partsWorkbook);
        var parsed = await new XlsxParser().ParseAsync(workbook, new ParseContext(System.IO.Path.GetFileName(partsWorkbook), MediaTypes.Xlsx), cancellationToken);
        Execute(connection, $"CREATE TABLE parts ({string.Join(", ", PartColumns.Select(c => $"{c.Column} {c.Type}"))}, catalog VARCHAR)");
        Execute(connection, $"CREATE TABLE superseded_parts ({string.Join(", ", SupersededColumns.Select(c => $"{c.Column} {c.Type}"))})");
        string? title = null;
        foreach (var block in parsed.Blocks)
        {
            if (block is HeadingBlock heading) title = heading.Text;
            if (block is not TableBlock table) continue;
            var header = table.Rows[table.HeaderRows - 1];
            var (name, columns) = header.Contains("Old part") ? ("superseded_parts", SupersededColumns) : ("parts", PartColumns);
            foreach (var row in table.Rows.Skip(table.HeaderRows))
            {
                var values = columns.Select(c => Convert(row[IndexOf(header, c.Header)], c.Type)).ToList();
                if (name == "parts") values.Add(title?.Split(": ").Last());
                Insert(connection, name, values);
            }
        }

        return new RecordsDatabase(path);
    }

    /// <summary>
    /// A session for one person: an in-memory database that attaches the records file read-only, defines one view per
    /// table holding only the rows the person may read, then turns off file access and locks the configuration, so
    /// that no query can write, read a file or change a setting. A table the person may not read has no view at all.
    /// </summary>
    public RecordsSession Open(Person asker, bool semantic = true)
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, $"ATTACH '{Literal(Path)}' AS cmms (READ_ONLY)");
        var views = new List<string>();
        var units = AssetRegister.UnitsFor(asker);
        if (units.Count > 0)
        {
            var allowed = string.Join(", ", units.Select(u => $"'{Literal(u)}'"));
            Execute(connection, $"CREATE VIEW assets AS SELECT * FROM cmms.assets WHERE business_unit IN ({allowed})");
            Execute(connection, $"""
                CREATE VIEW work_orders AS
                SELECT w.*{(semantic ? $"""
                    ,
                    date_diff('day', w.due, coalesce(w.completed, DATE '{Today:yyyy-MM-dd}')) AS days_late,
                    coalesce(w.completed, DATE '{Today:yyyy-MM-dd}') > w.due AS overdue,
                    strftime(w.due, '%Y') || '-Q' || quarter(w.due) AS due_quarter
                    """ : "")}
                FROM cmms.work_orders w JOIN cmms.assets a ON a.asset_tag = w.asset_tag
                WHERE a.business_unit IN ({allowed})
                """);
            views.AddRange(["work_orders", "assets"]);
        }

        if (asker.Groups.Contains("employees"))
        {
            Execute(connection, "CREATE VIEW parts AS SELECT * FROM cmms.parts");
            Execute(connection, "CREATE VIEW superseded_parts AS SELECT * FROM cmms.superseded_parts");
            views.AddRange(["parts", "superseded_parts"]);
        }

        // DuckDB's security guide: no file access, no extensions loaded on demand, bounded memory and threads, then lock
        // the settings. It also says settings don't replace a sandbox for untrusted SQL (Chapter 22).
        foreach (var setting in new[] { "enable_external_access = false", "autoload_known_extensions = false", "autoinstall_known_extensions = false",
                     "memory_limit = '256MB'", "threads = 2", "lock_configuration = true" })
        {
            Execute(connection, $"SET {setting}");
        }

        return new RecordsSession(connection, asker, views, semantic);
    }

    /// <summary>Contoso's "today" in all of its data; overdue is measured against it.</summary>
    public static DateOnly Today => ContosoFacts.Today;

    internal static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(DuckDBConnection connection, string table, IReadOnlyList<object?> values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {table} VALUES ({string.Join(", ", values.Select(_ => "?"))})";
        foreach (var value in values) command.Parameters.Add(new DuckDBParameter(value ?? DBNull.Value));
        command.ExecuteNonQuery();
    }

    private static int IndexOf(IReadOnlyList<string> header, string name) =>
        header.Select((h, i) => (h, i)).First(x => x.h.Equals(name, StringComparison.OrdinalIgnoreCase)).i;

    /// <summary>A cell as Excel shows it ("1,280.00", "8%", "2026-09-14") to the column's type.</summary>
    private static object? Convert(string cell, string type)
    {
        var text = cell.Trim();
        if (text.Length == 0) return null;
        return type switch
        {
            "INTEGER" => int.Parse(text.TrimEnd('%'), NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            "DATE" => DateOnly.Parse(text, CultureInfo.InvariantCulture),
            _ when type.StartsWith("DECIMAL", StringComparison.Ordinal) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
            _ => text,
        };
    }

    private static string Literal(string value) => value.Replace("\\", "/").Replace("'", "''");
}

/// <summary>
/// One person's view of the records. <see cref="Query"/> runs SQL that a model wrote, after <see cref="SqlGuard"/>
/// accepts it; <see cref="QueryTrusted"/> runs SQL the application wrote, with parameters. Both see only this
/// person's views, return at most <see cref="MaxRows"/> rows and give up after <see cref="Timeout"/>.
/// </summary>
public sealed class RecordsSession(DuckDBConnection connection, Person asker, IReadOnlyList<string> views, bool semantic) : IDisposable
{
    public Person Asker => asker;

    public IReadOnlyList<string> Views => views;

    public bool Semantic => semantic;

    public int MaxRows { get; init; } = 20;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public SqlVerdict Check(string sql) => SqlGuard.Check(connection, sql, views);

    public QueryResult Query(string sql)
    {
        var verdict = Check(sql);
        if (!verdict.Allowed) throw new SqlRejectedException(verdict.Reason);
        return Run(sql, []);
    }

    public QueryResult QueryTrusted(string sql, params object?[] parameters) => Run(sql, parameters);

    private QueryResult Run(string sql, object?[] parameters)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.Add(new DuckDBParameter(p ?? DBNull.Value));
        using var timeout = new CancellationTokenSource(Timeout);
        using var cancel = timeout.Token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count == MaxRows)
            {
                truncated = true;
                break;
            }

            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "null" : Format(reader.GetValue(i)))]);
        }

        return new QueryResult(sql, columns, rows, truncated, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>The views as a model reads them: columns, types and what each column means.</summary>
    public string Describe(IEnumerable<string>? only = null)
    {
        var text = new StringBuilder();
        foreach (var view in (only ?? views).Where(views.Contains))
        {
            text.Append($"TABLE {view} -- {RecordsSchema.Tables[view]}\n");
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{view}' AND table_catalog = 'memory' ORDER BY ordinal_position";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var (name, type) = (reader.GetString(0), reader.GetString(1));
                var note = RecordsSchema.Columns.GetValueOrDefault($"{view}.{name}");
                text.Append($"  {name} {type}{(note is null ? "" : $" -- {note}")}\n");
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Format(object value) => value switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        double x => x.ToString("0.##", CultureInfo.InvariantCulture),
        BigInteger n => n.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public void Dispose() => connection.Dispose();
}

/// <summary>What each table and column means, written for a model that writes SQL against them.</summary>
public static class RecordsSchema
{
    public static IReadOnlyDictionary<string, string> Tables { get; } = new Dictionary<string, string>
    {
        ["work_orders"] = "one row per maintenance work order",
        ["assets"] = "the equipment the work orders are for",
        ["parts"] = "the 2027 parts catalog: suppliers, lead times, stock and prices",
        ["superseded_parts"] = "parts replaced by newer ones, and the change notice that did it",
    };

    public static IReadOnlyDictionary<string, string> Columns { get; } = new Dictionary<string, string>
    {
        ["work_orders.work_order"] = "such as WO-88412",
        ["work_orders.asset_tag"] = "equipment tag, such as P-201",
        ["work_orders.site"] = "Riverside, Northgate, Lakeside or Eastfield",
        ["work_orders.type"] = "PM (preventive maintenance) or CM (corrective maintenance)",
        ["work_orders.priority"] = "P1 (most urgent) to P3",
        ["work_orders.description"] = "technicians' shorthand, such as 'chg mech seal' or 'E217 trip'",
        ["work_orders.status"] = "open or closed",
        ["work_orders.completed"] = "null while the work order is open",
        ["work_orders.technician"] = "user name, such as tsilva",
        ["work_orders.days_late"] = "days between the due date and completion, or today if still open; 0 or less is on time",
        ["work_orders.overdue"] = "true if completed after the due date, or still open after it",
        ["work_orders.due_quarter"] = "quarter of the due date, such as 2026-Q4",
        ["assets.business_unit"] = "fluid or motion",
        ["parts.fits"] = "the model or equipment the part fits, such as CP-200",
        ["parts.stock_riverside"] = "units in stock at the Riverside plant",
        ["parts.discount_percent"] = "Contoso's discount, in percent",
        ["parts.catalog"] = "Fluid Systems or Motion Systems",
        ["superseded_parts.effective"] = "the date the replacement took effect",
    };
}
