using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Momos.Worker.Execution;

namespace Momos.Worker.Analysis.Synthesis;

/// <summary>What a cited piece of evidence is. A finding is deliberately absent: only an inspection that reproduced something can supply one.</summary>
public enum ProposedEvidenceKind
{
    Code,
    Commit,
    PullRequest,
    Issue,
    Claim,
}

public sealed record ProposedEvidence(
    [property: Description("Code, Commit, PullRequest, Issue or Claim.")] ProposedEvidenceKind Kind,
    [property: Description("Code: repository-relative file path.")] string? Path = null,
    [property: Description("Code: text that appears verbatim in the file (or in Lines). Required for Code.")] string? Symbol = null,
    [property: Description("Code: a line number or range such as 12-20.")] string? Lines = null,
    [property: Description("Commit: the commit sha.")] string? Sha = null,
    [property: Description("PullRequest/Issue: its https URL, as it appears in the repository or a commit message.")] string? Url = null,
    [property: Description("Claim: the key of the claim this one builds on.")] string? ClaimKey = null);

public sealed record ProposedFlowStep(
    [property: Description("The component this step runs in, if one applies.")] string? ComponentId,
    [property: Description("The claim that shows this step happens.")] string ClaimKey);

/// <summary>
/// The only way an analysis agent changes the model: each tool checks its proposal at once — the
/// model rules, then the evidence against the analyzed commit — and answers "Recorded as …" or
/// "Rejected: …" so the agent can correct itself. Accepted proposals wait in the pass's stage.
/// </summary>
public sealed class SynthesisTools(DraftStage stage, EvidenceVerifier verifier, int maxChapters)
{
    public static JsonSerializerOptions ToolJson { get; } = CreateToolJson();

    [Description("Plan one chapter of the manual. Give a short title and a one-sentence purpose. " +
        "guide names the guide chapter it follows (see the list in your instructions), or is empty for a chapter the project needs that the guide lacks.")]
    public string ProposeChapter(string title, string purpose, string? guide = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > DraftRules.MaxTitleLength)
        {
            return Reject(RejectionReason.InvalidElement, $"A title is 1 to {DraftRules.MaxTitleLength} characters.");
        }

        if (purpose.Trim().Length > DraftRules.MaxPurposeLength)
        {
            return Reject(RejectionReason.InvalidElement, $"A purpose is at most {DraftRules.MaxPurposeLength} characters.");
        }

        if (stage.SectionCount >= maxChapters)
        {
            return Reject(RejectionReason.ChapterLimit, $"The manual already has {maxChapters} chapters; merge this into one of them.");
        }

        return Recorded(stage.AddSection(title.Trim(), purpose.Trim(), string.IsNullOrWhiteSpace(guide) ? null : guide.Trim()).Path);
    }

    [Description("Propose one claim. topic is a short, stable name for what it is about; reuse the topic a previous model used when you restate its claim. " +
        "Fact = read directly from code (cite Code). History = what the commit history shows (cite Commit, PullRequest or Issue). " +
        "Assessment = your interpretation; cite the claims it interprets and use Low confidence.")]
    public async Task<string> ProposeClaim(
        string topic, ClaimTier tier, string statement, ClaimConfidence confidence, ProposedEvidence[] evidence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return Reject(RejectionReason.InvalidElement, "A claim needs a topic.");
        }

        var key = ModelIds.SynthesizedClaim(topic);
        if (stage.FindClaim(key) is not null)
        {
            return Reject(RejectionReason.DuplicateTopic, $"Topic '{topic}' is already used by {key}; cite it instead or choose another topic.");
        }

        var claim = new ClaimPayload(key, tier, statement.Trim(), [.. evidence.Select(ToPayload)], confidence, ClaimOrigin.Synthesized);
        if (DraftRules.CheckClaim(claim, stage.FindClaim) is { Accepted: false } rule)
        {
            return Reject(rule);
        }

        var verified = new List<EvidencePayload>(claim.Evidence.Count);
        foreach (var e in claim.Evidence)
        {
            var (verdict, normalized) = await verifier.VerifyAsync(e, cancellationToken);
            if (!verdict.Accepted)
            {
                return Reject(verdict);
            }

            verified.Add(normalized);
        }

        var capped = DraftRules.CapConfidence(claim with { Evidence = verified }, stage.FindClaim);
        stage.AddClaim(capped);
        return capped.Confidence == claim.Confidence
            ? Recorded(key)
            : $"Recorded as {key}. Confidence lowered to {capped.Confidence}: it cites a less certain claim.";
    }

    [Description("Record a goal or constraint the project states for itself. source is Document when the repository says it, Inferred when you conclude it.")]
    public string ProposeIntent(string topic, string statement, IntentSource source, string[] claimKeys)
    {
        if (source == IntentSource.Developer || !Enum.IsDefined(source))
        {
            return Reject(RejectionReason.Unsupported, "Developer intents come from the developer, not from an analysis; use Document or Inferred.");
        }

        return AddElement(ModelIds.IntentPrefix, topic, statement, claimKeys, OutlineBlockKind.Intent,
            id => stage.AddIntent(new IntentPayload(id, statement.Trim(), source, claimKeys)));
    }

    [Description("Say what an existing component is responsible for, backed by claims. componentId is one of the component ids you were given.")]
    public string DescribeComponent(string componentId, string responsibility, string[] claimKeys)
    {
        if (!stage.HasComponent(componentId))
        {
            return Reject(RejectionReason.UnresolvedReference, $"Component '{componentId}' is not in the model.");
        }

        if (string.IsNullOrWhiteSpace(responsibility))
        {
            return Reject(RejectionReason.InvalidElement, "The responsibility is empty.");
        }

        if (DraftRules.CheckClaimRefs(claimKeys, stage.FindClaim) is { Accepted: false } refs)
        {
            return Reject(refs);
        }

        stage.DescribeComponent(componentId, responsibility.Trim(), claimKeys);
        return Recorded(componentId);
    }

    [Description("Record a runtime path as ordered steps. Each step cites the claim that shows it and, where it applies, the component it runs in.")]
    public string ProposeFlow(string topic, string name, ProposedFlowStep[] steps, string[] claimKeys)
    {
        if (steps.Length == 0)
        {
            return Reject(RejectionReason.InvalidElement, "A flow needs at least one step.");
        }

        if (steps.FirstOrDefault(s => s.ComponentId is { } c && !stage.HasComponent(c)) is { } badComponent)
        {
            return Reject(RejectionReason.UnresolvedReference, $"Component '{badComponent.ComponentId}' is not in the model.");
        }

        if (steps.FirstOrDefault(s => stage.FindClaim(s.ClaimKey) is null) is { } badClaim)
        {
            return Reject(RejectionReason.UnresolvedReference, $"Claim '{badClaim.ClaimKey}' is not in the model.");
        }

        return AddElement(ModelIds.FlowPrefix, topic, name, claimKeys, OutlineBlockKind.Flow,
            id => stage.AddFlow(new FlowPayload(id, name.Trim(), [.. steps.Select(s => new FlowStepPayload(s.ComponentId, s.ClaimKey))], claimKeys)));
    }

    [Description("Record something that must not break — a contract, a data ownership rule, a security boundary, a concurrency rule. kind is a short label for which.")]
    public string ProposeInvariant(string topic, string statement, string kind, string[] appliesTo, string[] claimKeys)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return Reject(RejectionReason.InvalidElement, "An invariant needs a kind.");
        }

        if (appliesTo.FirstOrDefault(c => !stage.HasComponent(c)) is { } bad)
        {
            return Reject(RejectionReason.UnresolvedReference, $"Component '{bad}' is not in the model.");
        }

        return AddElement(ModelIds.InvariantPrefix, topic, statement, claimKeys, OutlineBlockKind.Invariant,
            id => stage.AddInvariant(new InvariantPayload(id, statement.Trim(), kind.Trim(), appliesTo, claimKeys)));
    }

    [Description("Record a recurring design structure and the components it appears in.")]
    public string ProposePattern(string topic, string name, string[] appliesTo, string[] claimKeys)
    {
        if (appliesTo.FirstOrDefault(c => !stage.HasComponent(c)) is { } bad)
        {
            return Reject(RejectionReason.UnresolvedReference, $"Component '{bad}' is not in the model.");
        }

        return AddElement(ModelIds.PatternPrefix, topic, name, claimKeys, OutlineBlockKind.Pattern,
            id => stage.AddPattern(new PatternPayload(id, name.Trim(), appliesTo, claimKeys)));
    }

    [Description("Record a structural decision. rationale is 'unrecorded' unless a History claim you cite shows the reason; never infer a reason.")]
    public string ProposeDecision(string topic, string summary, string[] alternatives, string rationale, string[] claimKeys)
    {
        if (DraftRules.CheckDecision(rationale.Trim(), claimKeys, stage.FindClaim) is { Accepted: false } rule)
        {
            return Reject(rule);
        }

        return AddElement(ModelIds.DecisionPrefix, topic, summary, claimKeys, OutlineBlockKind.Decision,
            id => stage.AddDecision(new DecisionPayload(id, summary.Trim(), alternatives, rationale.Trim(), claimKeys)));
    }

    [Description("Place a model element in this chapter, in reading order. kind is Claim, Component, Pattern, Decision, Intent, Flow, Invariant or Map; " +
        "reference is its id or key (for Map: '*' for the whole structure, or a component id for it and its neighbours).")]
    public string AddBlock(OutlineBlockKind kind, string reference)
    {
        if (stage.Section is null)
        {
            return Reject(RejectionReason.Unsupported, "Blocks are placed while writing a chapter, not while planning.");
        }

        var block = new OutlineBlockPayload(kind, reference);
        if (DraftRules.CheckBlock(block, stage.HasElement) is { Accepted: false } rule)
        {
            return Reject(rule);
        }

        stage.AddBlock(block);
        return Recorded(reference);
    }

    [Description("Set this chapter's owner summary: 3 to 5 claims, in plain language, that tell a non-programmer what the chapter says.")]
    public string SetOwnerSummary(string[] claimKeys)
    {
        if (stage.Section is null)
        {
            return Reject(RejectionReason.Unsupported, "An owner summary belongs to a chapter.");
        }

        if (DraftRules.CheckClaimRefs(claimKeys, stage.FindClaim) is { Accepted: false } refs)
        {
            return Reject(refs);
        }

        stage.SetOwnerSummary(claimKeys);
        return Recorded(stage.Section.Path);
    }

    public IReadOnlyList<AIFunction> ForOverview() => [Tool(ProposeChapter), Tool(ProposeClaim), Tool(ProposeIntent)];

    public IReadOnlyList<AIFunction> ForChapter() =>
    [
        Tool(ProposeClaim), Tool(ProposeIntent), Tool(DescribeComponent), Tool(ProposeFlow), Tool(ProposeInvariant),
        Tool(ProposePattern), Tool(ProposeDecision), Tool(AddBlock), Tool(SetOwnerSummary),
    ];

    private string AddElement(string prefix, string topic, string text, string[] claimKeys, OutlineBlockKind kind, Action<string> add)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(text))
        {
            return Reject(RejectionReason.InvalidElement, "A topic and a name or statement are required.");
        }

        var id = ModelIds.Element(prefix, topic);
        if (stage.HasElement(kind, id))
        {
            return Reject(RejectionReason.DuplicateTopic, $"Topic '{topic}' is already used by {id}.");
        }

        if (DraftRules.CheckClaimRefs(claimKeys, stage.FindClaim) is { Accepted: false } refs)
        {
            return Reject(refs);
        }

        add(id);
        return Recorded(id);
    }

    private static AIFunction Tool(Delegate method) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { SerializerOptions = ToolJson });

    private static EvidencePayload ToPayload(ProposedEvidence e) =>
        new(Enum.Parse<EvidenceKind>(e.Kind.ToString()), e.Path, e.Symbol, e.Lines, e.Sha, e.Url, ClaimKey: e.ClaimKey);

    private static string Recorded(string id) => $"Recorded as {id}.";

    private string Reject(Verdict verdict) => Reject(verdict.Reason!, verdict.Message);

    private string Reject(string reason, string message)
    {
        stage.Draft.Reject(reason);
        return $"Rejected: {message}";
    }

    private static JsonSerializerOptions CreateToolJson()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}
