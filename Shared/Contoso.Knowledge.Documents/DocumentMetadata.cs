namespace Contoso.Knowledge.Documents;

/// <summary>
/// What the pipeline knows about a document besides its content. The required fields are the data
/// contract with content owners (Chapter 6): a document without them is quarantined, not guessed at.
/// </summary>
public sealed record DocumentMetadata
{
    public required string Title { get; init; }

    /// <summary>One of the tenants (<c>fluid</c>, <c>motion</c>, <c>service</c>) or the shared <c>corporate</c> library.</summary>
    public required string Tenant { get; init; }

    /// <summary>A value from <see cref="DocumentTypes"/>.</summary>
    public required string DocumentType { get; init; }

    /// <summary>A value from <see cref="SensitivityLabels"/>.</summary>
    public required string Sensitivity { get; init; }

    /// <summary>The groups whose members may read the document: a reference to the source's ACL, synced (Chapter 22).</summary>
    public required IReadOnlyList<string> AllowedGroups { get; init; }

    /// <summary>The person or team that answers for the content.</summary>
    public required string Owner { get; init; }

    public string Language { get; init; } = "en";

    public string? Revision { get; init; }

    /// <summary>The first day this version applies. Retrieval "as of" a date uses it (Chapter 6).</summary>
    public DateOnly? EffectiveFrom { get; init; }

    /// <summary>The last day this version applies; open-ended while it's current.</summary>
    public DateOnly? EffectiveTo { get; init; }

    /// <summary>Documents or versions this one replaces, such as <c>PN-4471-B</c> or <c>SOP-FS-014 rev 2</c>.</summary>
    public IReadOnlyList<string> Supersedes { get; init; } = [];

    public IReadOnlyList<string> EquipmentTags { get; init; } = [];

    public IReadOnlyList<string> PartNumbers { get; init; } = [];

    /// <summary>Source-specific fields that aren't part of the contract yet; promote a field once a chapter depends on it.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>The controlled vocabulary for <see cref="DocumentMetadata.DocumentType"/>.</summary>
public static class DocumentTypes
{
    public const string Manual = "manual";
    public const string Troubleshooting = "troubleshooting";
    public const string Sop = "sop";
    public const string WorkInstruction = "work-instruction";
    public const string SafetyBulletin = "safety-bulletin";
    public const string ChangeNotice = "change-notice";
    public const string AssetRecord = "asset-record";
    public const string ServiceReport = "service-report";
    public const string PartsCatalog = "parts-catalog";
    public const string WorkOrders = "work-orders";
    public const string Policy = "policy";
    public const string Contract = "contract";
    public const string Training = "training";
    public const string Email = "email";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Manual, Troubleshooting, Sop, WorkInstruction, SafetyBulletin, ChangeNotice, AssetRecord,
        ServiceReport, PartsCatalog, WorkOrders, Policy, Contract, Training, Email,
    };
}

/// <summary>The controlled vocabulary for <see cref="DocumentMetadata.Sensitivity"/>, least to most sensitive.</summary>
public static class SensitivityLabels
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Confidential = "confidential";
    public const string Restricted = "restricted";

    public static IReadOnlyList<string> Ordered { get; } = [Public, Internal, Confidential, Restricted];
}
