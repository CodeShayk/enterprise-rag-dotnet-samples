using Contoso.Corpus;
using Contoso.Knowledge.Agents;

namespace Ch17.Tests;

/// <summary>The records database built once from the generated work-order export and parts workbook.</summary>
public sealed class RecordsFixture : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ch17-tests-{Guid.NewGuid():N}.duckdb");

    public RecordsDatabase Records { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Records = await RecordsDatabase.BuildAsync(_path,
        Path.Combine(SampleCode.Root, "corpus", "sources", "database", "work-orders.csv"),
        Path.Combine(SampleCode.Root, "corpus", "sources", "fileshare", "procurement", "Parts Catalog 2027.xlsx"));

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A session still holding the file; the temp folder will be cleaned eventually.
        }

        return ValueTask.CompletedTask;
    }
}

public static class SampleCode
{
    public static string Root { get; } = Find(AppContext.BaseDirectory);

    private static string Find(string directory) =>
        File.Exists(Path.Combine(directory, "Contoso-Knowledge.slnx")) ? directory : Find(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!);
}

public sealed class RecordsTests(RecordsFixture fixture) : IClassFixture<RecordsFixture>
{
    [Fact]
    public void Views_hold_only_the_rows_the_asker_may_read()
    {
        using var nadia = fixture.Records.Open(People.Nadia);
        using var kwame = fixture.Records.Open(People.Kwame);
        using var grace = fixture.Records.Open(People.Grace);

        Assert.Equal("0", nadia.QueryTrusted("SELECT count(*) FROM work_orders WHERE asset_tag = 'GBX-07'").Rows[0][0]);
        Assert.NotEqual("0", kwame.QueryTrusted("SELECT count(*) FROM work_orders WHERE asset_tag = 'GBX-07'").Rows[0][0]);
        Assert.DoesNotContain("work_orders", grace.Views);
        Assert.Contains("parts", grace.Views);
    }

    [Fact]
    public void Overdue_comes_from_the_view_definition()
    {
        using var nadia = fixture.Records.Open(People.Nadia);

        var p201 = nadia.Query("SELECT count(*) FILTER (WHERE overdue), count(*) FROM work_orders WHERE asset_tag = 'P-201' AND due_quarter = '2026-Q4'");
        var open = nadia.Query("SELECT work_order FROM work_orders WHERE status = 'open' AND overdue");

        Assert.Equal(["0", "3"], p201.Rows[0]);
        Assert.Equal("WO-88286", Assert.Single(open.Rows)[0]);
    }

    [Theory]
    [InlineData("DELETE FROM work_orders", "SELECT")]
    [InlineData("SELECT 1; DROP TABLE assets", "SELECT")]
    [InlineData("SELECT 1; SELECT 2", "exactly one")]
    [InlineData("SELECT * FROM cmms.work_orders", "without a schema")]
    [InlineData("SELECT * FROM information_schema.tables", "without a schema")]
    [InlineData("SELECT * FROM read_csv('work-orders.csv')", "Table functions")]
    [InlineData("SELECT * FROM salaries", "No such table")]
    public void The_guard_refuses_anything_but_one_select_over_the_views(string sql, string reason)
    {
        using var nadia = fixture.Records.Open(People.Nadia);

        var verdict = nadia.Check(sql);

        Assert.False(verdict.Allowed);
        Assert.Contains(reason, verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guard_allows_ctes_over_the_views()
    {
        using var nadia = fixture.Records.Open(People.Nadia);

        var verdict = nadia.Check("WITH late AS (SELECT * FROM work_orders WHERE overdue) SELECT count(*) FROM late");

        Assert.True(verdict.Allowed);
        Assert.Equal(["work_orders"], verdict.Views);
    }

    [Fact]
    public void A_session_cannot_write_reach_files_or_change_settings()
    {
        using var kwame = fixture.Records.Open(People.Kwame);

        Assert.ThrowsAny<Exception>(() => kwame.QueryTrusted("INSERT INTO cmms.assets VALUES ('X-1', 'x', 'x', 'fluid')"));
        Assert.ThrowsAny<Exception>(() => kwame.QueryTrusted("COPY (SELECT 1) TO 'out.csv'"));
        Assert.ThrowsAny<Exception>(() => kwame.QueryTrusted("SET enable_external_access = true"));
    }

    [Fact]
    public void The_workbook_loads_with_its_schema_written_down()
    {
        using var grace = fixture.Records.Open(People.Grace);

        var seal = grace.Query("SELECT supplier, lead_time_weeks, stock_riverside, net_price, catalog FROM parts WHERE part_number = 'PN-4471-C'");

        Assert.Equal(["Fabrikam Seals", "6", "7", "1334", "Fluid Systems"], seal.Rows[0]);
    }
}
