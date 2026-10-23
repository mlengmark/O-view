using System.Reflection;
using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// Keeps display copy out of <c>OView.Core.Models</c> once it has moved to Presentation
/// (docs/architecture.md, "Once display text has moved out of Core, a reflection test keeps it
/// out"), by reflecting over every public type in the namespace and failing on a public
/// string-typed member that reads like something written for a user rather than data a
/// provider resolves.
///
/// <para><b>Two independent checks, because a member can fail either way.</b> A member whose
/// name ends in one of <see cref="DisplayTextSuffixes"/> is named like copy regardless of what
/// it holds today, <c>PanelText.TokensUsedTodayLabel</c>, <c>TokenText.CoverageNote</c> and
/// <c>OffPlanText.CreditModelsCaption</c> all carried one of these endings until the display
/// text moved to Presentation. Separately, a public <c>const string</c> whose
/// literal value itself reads as a sentence is copy no matter what its name is.</para>
///
/// <para><b>What this test does not reach.</b> Core's own provider reports (the "excludes
/// claude-x (no published rate)" style caveats CLAUDE.md rule 6 describes) still compose prose
/// from data at the point they are built; that composition is not a public member returning a
/// fixed string, so reflection cannot see it and this test says nothing about it. <see
/// cref="Exemptions"/> is not a place to park new copy to get past this test, it is a closed
/// list of two, each with the reason it is data rather than wording.</para>
/// </summary>
public class CoreModelsHoldNoDisplayTextTests
{
    /// <summary>
    /// Endings that mark a member as copy written for a user, not data read by one. These are
    /// exactly the endings display-text members in this codebase have carried.
    /// </summary>
    private static readonly string[] DisplayTextSuffixes =
    [
        "Note", "Label", "Caption", "Hint", "Title", "Detail", "Caveat",
        "Heading", "Message", "Summary", "Text", "Instruction",
    ];

    /// <summary>
    /// The only two places a public member of <c>OView.Core.Models</c> may hold something a
    /// user reads, and why. A <c>null</c> member exempts every public member of the type; a
    /// named member exempts only that one. There are at most two entries here, a third means
    /// Core still holds display text this phase was meant to move, which is a question for Gary
    /// III, not a line to add.
    /// </summary>
    private static readonly (Type Type, string? Member)[] Exemptions =
    [
        // The model's own published name (ModelCatalog's data, carried alongside the id it
        // names rather than invented at render time), not prose composed about the model.
        // Neither check reaches it today ("DisplayName" matches no suffix above, and it is a
        // record property rather than a const), so this entry is pre-emptive: it keeps the
        // property's status settled if it is ever renamed to something that would match, such
        // as DisplayLabel.
        (typeof(ModelSlice), "DisplayName"),

        // An affordance phrase each head supplies once at startup ("Right-click the tray icon
        // -> Copy diagnostics"). Core holds no branch on it and never composes it itself; it is
        // set once by the head and read back whole. Core's own provider reports go on to
        // compose this phrase into prose the way they compose everything else, exempting it
        // here does not exempt that composition, which this test cannot see in the first place.
        (typeof(DiagnosticsHint), null),
    ];

    [Fact]
    public void NoPublicMemberInCoreModelsReadsLikeDisplayCopy()
    {
        var violations = FindViolations();

        Assert.True(
            violations.Count == 0,
            "A public member of OView.Core.Models reads like display copy a user would see, " +
            "not data a provider resolves:\n" +
            string.Join("\n", violations.Select(v => $"  - {v}")) +
            "\n\nMove the wording to Presentation. If this is genuinely one of the two allowed " +
            "exceptions, add it to Exemptions above with its reason in words.");
    }

    private static List<string> FindViolations()
    {
        var violations = new List<string>();

        foreach (var type in typeof(ModelSlice).Assembly.GetTypes())
        {
            if ((!type.IsPublic && !type.IsNestedPublic) || type.Namespace != "OView.Core.Models" || IsExemptType(type))
            {
                continue;
            }

            foreach (var member in StringMembers(type))
            {
                if (IsExemptMember(type, member.Name))
                {
                    continue;
                }

                var suffix = DisplayTextSuffixes.FirstOrDefault(s => member.Name.EndsWith(s, StringComparison.Ordinal));
                if (suffix is not null)
                {
                    violations.Add($"{type.Name}.{member.Name} is named like display copy (ends with \"{suffix}\").");
                    continue;
                }

                if (member is FieldInfo { IsLiteral: true } field &&
                    field.GetRawConstantValue() is string value &&
                    ReadsAsSentence(value))
                {
                    violations.Add($"{type.Name}.{member.Name} reads as a sentence: \"{value}\"");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Public properties and fields declared directly on the type whose value is a string,
    /// the only shape either check means anything for. Methods are never the shape a display
    /// member has taken in this codebase, so checking only properties and fields keeps a
    /// genuine hit obvious rather than guessing at method bodies.
    /// </summary>
    private static IEnumerable<MemberInfo> StringMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static |
                                    BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            if (property.PropertyType == typeof(string))
            {
                yield return property;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (field.FieldType == typeof(string))
            {
                yield return field;
            }
        }
    }

    /// <summary>
    /// An identifier in this codebase, a model id, a file name, a phrase meant to be composed
    /// rather than read whole, never contains a space; a sentence always does. That single
    /// distinction is all this check needs, and it is deliberately not trying to parse English.
    /// </summary>
    private static bool ReadsAsSentence(string value) =>
        value.Length > 0 && char.IsUpper(value.TrimStart()[0]) && value.Contains(' ');

    private static bool IsExemptType(Type type) =>
        Exemptions.Any(e => e.Type == type && e.Member is null);

    private static bool IsExemptMember(Type type, string memberName) =>
        Exemptions.Any(e => e.Type == type && e.Member == memberName);

    // Verified by hand, 2026-10-14: commenting out the DiagnosticsHint line in Exemptions above
    // (its Instruction property ends in "Instruction", and its Default constant is the
    // sentence "Produce an O-view diagnostics report") turns
    // NoPublicMemberInCoreModelsReadsLikeDisplayCopy red with exactly:
    //
    //   Error Message:
    //    A public member of OView.Core.Models reads like display copy a user would see, not
    //   data a provider resolves:
    //     - DiagnosticsHint.Instruction is named like display copy (ends with "Instruction").
    //     - DiagnosticsHint.Default reads as a sentence: "Produce an O-view diagnostics report"
    //
    //   Move the wording to Presentation. If this is genuinely one of the two allowed
    //   exceptions, add it to Exemptions above with its reason in words.
    //
    // Restoring the line above turns it green again. This comment exists so a reader can see
    // the test fails for the right reason without re-running the experiment.
}
