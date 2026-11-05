using Contoso.Knowledge.Retrieval;

namespace Contoso.Knowledge.Security;

/// <summary>
/// How much a source's content is trusted, by how it's written and reviewed: controlled documents go through change
/// control; managed records come from systems of record; correspondence is whatever someone wrote; uploads are whatever
/// someone sent.
/// </summary>
public enum SourceTier
{
    Uploaded,
    Correspondence,
    Managed,
    Controlled,
}

public static class TrustTiers
{
    public static SourceTier Of(IndexedChunk chunk) => chunk.DocumentType switch
    {
        "manual" or "troubleshooting" or "sop" or "work-instruction" or "policy" or "safety-bulletin" or "change-notice" or "contract" or "training" => SourceTier.Controlled,
        "parts-catalog" or "work-orders" or "asset-record" or "service-report" => SourceTier.Managed,
        "email" => SourceTier.Correspondence,
        _ => SourceTier.Uploaded,
    };
}
