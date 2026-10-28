using PcHealth.Core.Metrics;

namespace PcHealth.Core.Advice.Items;

/// <summary>
/// O4: security settings are switched off (REMEDIES-PLAN O4, STEP-B-SPEC 5). One card that lists
/// every setting found off. Windows' defaults grade Good on each, so a freshly reimaged laptop gets
/// none. Real-time protection is information only when another antivirus is in charge, and never
/// counts then.
///
/// Nothing in a report says whether a laptop is managed by a company: domain and policy reads are
/// Step C's. So the line about IT is always there, and worded as a possibility.
/// </summary>
public sealed class SecurityOffItem : IAdviceItem
{
    public const string ItemId = "O4";

    private const string Firewall = "security.firewall.enabled", RealTime = "security.defender.rtp", Antivirus = "security.av.registered",
        Tamper = "security.defender.tamper", Uac = "security.uac.enabled", SmartScreen = "security.smartscreen";

    /// <summary>Each setting, in the order its lines are printed, with its sentence: <c>advice.o4.setting.</c> and its id.</summary>
    private static readonly (string MetricId, string Key)[] Settings =
    [
        (Firewall, "advice.o4.setting.security.firewall.enabled"),
        (Antivirus, "advice.o4.setting.security.av.registered"),
        (RealTime, "advice.o4.setting.security.defender.rtp"),
        (Tamper, "advice.o4.setting.security.defender.tamper"),
        (Uac, "advice.o4.setting.security.uac.enabled"),
        (SmartScreen, "advice.o4.setting.security.smartscreen"),
    ];

    public string Id => ItemId;

    public string Area => "security";

    public string Os => "windows";

    public int Rank => 40;

    public IReadOnlyList<string> Needs { get; } = ["security.posture"];

    public IReadOnlyList<Recommendation> Evaluate(AdviceFacts facts, IReadOnlyList<Recommendation> earlier)
    {
        // SmartScreen is Normal when its state is one this app does not recognise: that is not "off".
        var off = Settings
            .SelectMany(s => facts.Problems(s.MetricId)
                .Where(v => s.MetricId != SmartScreen || v.Grade == Grade.Bad)
                .Select(v => (Setting: s, Verdict: v)))
            .ToList();
        if (off.Count == 0) return [];

        var card = new AdviceCard(ItemId, ItemId, Area, AdviceUrgency.Advice, Rank).Title("advice.o4.title");
        foreach (var ((metricId, key), verdict) in off)
        {
            card.Because(verdict);
            // The firewall's Subject is the profile, "Public profile", which the module names itself.
            if (metricId != Firewall) card.Line(AdviceLineRole.Finding, key);
            else if (verdict.Metric.Subject is { } profile) card.Line(AdviceLineRole.Finding, key, ("profile", AdviceArg.Words(profile)));
            else card.Line(AdviceLineRole.Finding, "advice.o4.setting.security.firewall.enabled.unmeasured");
        }

        bool Is(string metricId) => off.Exists(o => o.Setting.MetricId == metricId);
        if (Is(RealTime) || Is(Antivirus)) card.Line(AdviceLineRole.Why, "advice.o4.why");
        if (Is(Firewall) || Is(Antivirus) || Is(RealTime) || Is(Tamper)) card.Line(AdviceLineRole.Do, "advice.o4.do.windows-security");
        if (Is(Uac)) card.Line(AdviceLineRole.Do, "advice.o4.do.uac");
        if (Is(SmartScreen)) card.Line(AdviceLineRole.Do, "advice.o4.do.smartscreen");
        card.Line(AdviceLineRole.Finding, "advice.o4.managed");
        return [card.Build()];
    }
}
