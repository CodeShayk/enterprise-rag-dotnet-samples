using Contoso.Knowledge.Query;
using Contoso.Knowledge.Retrieval;

namespace Ch13.Query.Cli;

/// <summary>
/// Retrieves as each question's plan says: nothing for a question the planner turned away, the planned query and
/// filter otherwise, and one ranking per part of a decomposed question, fused with RRF.
/// </summary>
public sealed class PlannedRetriever(IRetriever inner, IReadOnlyDictionary<string, QueryPlan> plans, int candidates = 20) : IRetriever
{
    private readonly ReciprocalRankFusion _fusion = new(60);

    public string Name => "planned";

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var plan = plans[request.Question];
        if (!plan.Retrieve) return RetrievalResult.Of([]);
        if (plan.Queries.Count == 1)
        {
            return await inner.RetrieveAsync(request with { Question = plan.Queries[0], Filter = plan.Filter }, cancellationToken);
        }

        var rankings = new List<IReadOnlyList<Hit>>();
        foreach (var query in plan.Queries)
        {
            rankings.Add((await inner.RetrieveAsync(request with { Question = query, Filter = plan.Filter, Top = candidates }, cancellationToken)).Hits);
        }

        return RetrievalResult.Of(_fusion.Fuse(rankings, request.Top));
    }
}
