using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>A chapter as the analysis builds it; becomes an <see cref="OutlineSectionPayload"/> at assembly.</summary>
public sealed class DraftSection(string id, string path, string title, string purpose, string? guide)
{
    public string Id { get; } = id;

    public string Path { get; } = path;

    public string Title { get; } = title;

    public string Purpose { get; } = purpose;

    public string? Guide { get; } = guide;

    public List<string> OwnerSummaryClaims { get; } = [];

    public List<OutlineBlockPayload> Blocks { get; } = [];
}

public sealed record ComponentUpdate(string Responsibility, IReadOnlyList<string> Claims);

/// <summary>
/// What an analysis has accepted so far, on top of the deterministic skeleton. A pass writes to a
/// <see cref="DraftStage"/>, and nothing reaches the draft until the stage is committed — the caller
/// decides whether a pass that did not finish keeps what it staged.
/// </summary>
public sealed class SynthesisDraft
{
    private readonly Dictionary<string, ClaimPayload> _claims = new(StringComparer.Ordinal);
    private readonly HashSet<string> _components;
    private readonly Dictionary<string, int> _rejections = new(StringComparer.Ordinal);

    public SynthesisDraft(ProjectModelPayload skeleton)
    {
        Skeleton = skeleton;
        foreach (var claim in skeleton.Claims)
        {
            _claims[claim.Key] = claim;
        }

        _components = skeleton.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
    }

    public ProjectModelPayload Skeleton { get; }

    public List<DraftSection> Sections { get; } = [];

    /// <summary>The claims the passes added — the skeleton's are in <see cref="Skeleton"/>.</summary>
    public List<ClaimPayload> Claims { get; } = [];

    public Dictionary<string, ComponentUpdate> ComponentUpdates { get; } = new(StringComparer.Ordinal);

    public List<FlowPayload> Flows { get; } = [];

    public List<InvariantPayload> Invariants { get; } = [];

    public List<PatternPayload> Patterns { get; } = [];

    public List<DecisionPayload> Decisions { get; } = [];

    public List<IntentPayload> Intents { get; } = [];

    public IReadOnlyDictionary<string, int> Rejections => _rejections;

    /// <summary>Chapter paths handed out so far, including those of a pass that did not commit —
    /// a later chapter then gets a numbered path rather than a clash.</summary>
    internal HashSet<string> Paths { get; } = new(StringComparer.Ordinal);

    public ClaimPayload? FindClaim(string key) => _claims.GetValueOrDefault(key);

    public bool HasComponent(string id) => _components.Contains(id);

    public bool HasElement(OutlineBlockKind kind, string reference) => kind switch
    {
        OutlineBlockKind.Claim => _claims.ContainsKey(reference),
        OutlineBlockKind.Component => _components.Contains(reference),
        OutlineBlockKind.Flow => Flows.Any(f => f.Id == reference),
        OutlineBlockKind.Invariant => Invariants.Any(i => i.Id == reference),
        OutlineBlockKind.Pattern => Patterns.Any(p => p.Id == reference),
        OutlineBlockKind.Decision => Decisions.Any(d => d.Id == reference),
        OutlineBlockKind.Intent => Intents.Any(i => i.Id == reference),
        _ => false,
    };

    public void Reject(string reason) => _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;

    public DraftStage Stage(DraftSection? section) => new(this, section);

    internal void Accept(ClaimPayload claim)
    {
        _claims[claim.Key] = claim;
        Claims.Add(claim);
    }
}

/// <summary>One pass's pending additions; reads see the committed draft plus what this pass added.</summary>
public sealed class DraftStage(SynthesisDraft draft, DraftSection? section)
{
    private readonly List<ClaimPayload> _claims = [];
    private readonly Dictionary<string, ComponentUpdate> _components = new(StringComparer.Ordinal);
    private readonly List<FlowPayload> _flows = [];
    private readonly List<InvariantPayload> _invariants = [];
    private readonly List<PatternPayload> _patterns = [];
    private readonly List<DecisionPayload> _decisions = [];
    private readonly List<IntentPayload> _intents = [];
    private readonly List<DraftSection> _sections = [];
    private readonly List<OutlineBlockPayload> _blocks = [];
    private List<string>? _ownerSummary;

    public DraftSection? Section { get; } = section;

    public SynthesisDraft Draft { get; } = draft;

    public int StagedClaimCount => _claims.Count;

    /// <summary>Whether <see cref="Commit"/> would add anything to the draft.</summary>
    public bool HasContent =>
        _claims.Count > 0 || _components.Count > 0 || _flows.Count > 0 || _invariants.Count > 0 || _patterns.Count > 0
        || _decisions.Count > 0 || _intents.Count > 0 || _sections.Count > 0 || _blocks.Count > 0 || _ownerSummary is not null;

    public int SectionCount => Draft.Sections.Count + _sections.Count;

    public ClaimPayload? FindClaim(string key) => Draft.FindClaim(key) ?? _claims.FirstOrDefault(c => c.Key == key);

    public bool HasComponent(string id) => Draft.HasComponent(id);

    public bool HasElement(OutlineBlockKind kind, string reference) => Draft.HasElement(kind, reference) || kind switch
    {
        OutlineBlockKind.Claim => _claims.Any(c => c.Key == reference),
        OutlineBlockKind.Flow => _flows.Any(f => f.Id == reference),
        OutlineBlockKind.Invariant => _invariants.Any(i => i.Id == reference),
        OutlineBlockKind.Pattern => _patterns.Any(p => p.Id == reference),
        OutlineBlockKind.Decision => _decisions.Any(d => d.Id == reference),
        OutlineBlockKind.Intent => _intents.Any(i => i.Id == reference),
        _ => false,
    };

    public void AddClaim(ClaimPayload claim) => _claims.Add(claim);

    public void DescribeComponent(string id, string responsibility, IReadOnlyList<string> claims) => _components[id] = new(responsibility, claims);

    public void AddFlow(FlowPayload flow) => _flows.Add(flow);

    public void AddInvariant(InvariantPayload invariant) => _invariants.Add(invariant);

    public void AddPattern(PatternPayload pattern) => _patterns.Add(pattern);

    public void AddDecision(DecisionPayload decision) => _decisions.Add(decision);

    public void AddIntent(IntentPayload intent) => _intents.Add(intent);

    public DraftSection AddSection(string title, string purpose, string? guide)
    {
        var path = SectionPaths.FromTitle(title, Draft.Paths);
        var added = new DraftSection(ModelIds.Section(path), path, title, purpose, guide);
        _sections.Add(added);
        return added;
    }

    public void AddBlock(OutlineBlockPayload block) => _blocks.Add(block);

    public void SetOwnerSummary(IReadOnlyList<string> claims) => _ownerSummary = [.. claims];

    public void Commit()
    {
        foreach (var claim in _claims)
        {
            Draft.Accept(claim);
        }

        foreach (var (id, update) in _components)
        {
            var existing = Draft.ComponentUpdates.GetValueOrDefault(id)?.Claims ?? [];
            Draft.ComponentUpdates[id] = update with { Claims = [.. existing, .. update.Claims.Except(existing)] };
        }

        Draft.Flows.AddRange(_flows);
        Draft.Invariants.AddRange(_invariants);
        Draft.Patterns.AddRange(_patterns);
        Draft.Decisions.AddRange(_decisions);
        Draft.Intents.AddRange(_intents);
        Draft.Sections.AddRange(_sections);
        if (Section is not null)
        {
            Section.Blocks.AddRange(_blocks);
            if (_ownerSummary is not null)
            {
                Section.OwnerSummaryClaims.Clear();
                Section.OwnerSummaryClaims.AddRange(_ownerSummary);
            }
        }
    }
}
