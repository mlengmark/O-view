using System.Reflection;
using OView.Core.Models;

namespace OView.Core.Tests;

/// <summary>
/// Keeps the bare nullables out of <c>OView.Core.Models</c> now that they have gone. A
/// percentage, a reset time, a token count or an Est. figure declared as a plain
/// <c>Nullable&lt;T&gt;</c> leaves every reader to decide what its null meant, never
/// measured, measured as nothing, or simply not read yet, and those are three different
/// claims about the user's account (CLAUDE.md rule 6). The status-paired types in that folder
/// are what say which, so a reintroduced plain nullable is this test's one subject.
///
/// <para><b>What it checks is read off the models, not listed here.</b> The types it objects
/// to are the ones the status-paired types in <c>OView.Core.Models</c> wrap: each such type is
/// found by its own shape, a <c>Value</c> beside a <see cref="UsageValueStatus"/>
/// <c>Status</c>, and the type of its <c>Value</c> is what becomes forbidden as a bare
/// nullable elsewhere in the namespace. Add a fifth paired type tomorrow and this test widens
/// to cover its figures without being edited; it cannot fall behind the models the way a
/// hand-written list of member names would. <see cref="PairedTypesAreDiscovered"/> fails if
/// that discovery ever finds nothing, because a rule derived from an empty set passes
/// everything.</para>
///
/// <para><b>What this test does not reach.</b> It sees declared member types only, so it says
/// nothing about a nullable passed as a method parameter, held in a local, or declared on a
/// type outside <c>OView.Core.Models</c>, a head's own view model, say. It also cannot object
/// to a figure whose underlying type no paired type wraps: a percentage reintroduced as
/// <c>int?</c> rather than <c>double?</c> would pass, because nothing in the namespace pairs
/// an <c>int</c>. Both are deliberate: the alternative is guessing which nullables are figures
/// from their names, which is the hand-written list this test exists not to be.</para>
/// </summary>
public class CoreModelsHoldNoBareNullablesTests
{
    /// <summary>
    /// The only places a public member of <c>OView.Core.Models</c> may still be a bare
    /// nullable of a paired type's value, and why. Each entry is a value whose null says
    /// something other than "the trust of this figure is in question", which is the one
    /// question the paired types answer, so pairing it would add a status no reader could
    /// act on. A new entry is a scope question for Gary III, not a line to add.
    /// </summary>
    private static readonly (Type Type, string Member, string Reason)[] Exemptions =
    [
        // Nothing to pair: null marks an ordinary model slice, and a count marks the folded
        // remainder. It discriminates between two kinds of slice rather than reporting a
        // figure whose provenance a reader has to weigh.
        (typeof(ModelSlice), nameof(ModelSlice.FoldedCount),
            "a discriminator for the remainder segment, not a figure"),
    ];

    [Fact]
    public void NoPublicMemberInCoreModelsIsABareNullableFigure()
    {
        var violations = FindViolations();

        Assert.True(
            violations.Count == 0,
            "A public member of OView.Core.Models is a bare nullable of a value this app has a " +
            "status-paired type for:\n" +
            string.Join("\n", violations.Select(v => $"  - {v}")) +
            "\n\nDeclare it as that paired type instead, so the member carries why the figure is " +
            "absent rather than leaving each reader to infer it from the null. If its null " +
            "genuinely means something else, add it to Exemptions above with that reason in words.");
    }

    /// <summary>
    /// The rule above is only as wide as the paired types it finds, so a rename that hid them
    /// all would leave it passing everything in silence. These are the four that exist today;
    /// the assertion is on there being several, not on which, so adding one is not a failure.
    /// </summary>
    [Fact]
    public void PairedTypesAreDiscovered()
    {
        var paired = PairedTypes();

        Assert.True(
            paired.Count >= 4,
            "Found only " + paired.Count + " status-paired types in OView.Core.Models " +
            "(UsagePercent, UsageInstant, TokenCount and EstimatedUsd are expected). " +
            "NoPublicMemberInCoreModelsIsABareNullableFigure derives what it forbids from " +
            "these, so with none of them it would pass whatever the models hold.");

        Assert.Contains(typeof(decimal), PairedValueTypes());
        Assert.Contains(typeof(DateTimeOffset), PairedValueTypes());
    }

    private static List<string> FindViolations()
    {
        var paired = PairedTypes();
        var forbidden = PairedValueTypes();
        var violations = new List<string>();

        foreach (var type in ModelTypes())
        {
            // A paired type's own Value is the one nullable this rule is built around: it is
            // where the figure lives once the status is beside it.
            if (paired.Contains(type))
            {
                continue;
            }

            foreach (var (member, memberType) in ValueMembers(type))
            {
                var underlying = Nullable.GetUnderlyingType(memberType);
                if (underlying is null || !forbidden.Contains(underlying) || IsExempt(type, member))
                {
                    continue;
                }

                var replacement = paired.First(p => PairedValueTypeOf(p) == underlying);
                violations.Add(
                    $"{type.Name}.{member} is a bare {underlying.Name}?, use {replacement.Name}.");
            }
        }

        return violations;
    }

    /// <summary>
    /// Every public type in the models namespace. Taken from the assembly rather than a list,
    /// for the same reason <c>CoreModelsHoldNoDisplayTextTests</c> does: a model added without
    /// this test being touched is exactly the case it has to cover.
    /// </summary>
    private static IEnumerable<Type> ModelTypes() =>
        typeof(UsageSnapshot).Assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && t.Namespace == "OView.Core.Models");

    /// <summary>
    /// The status-paired value types, found by shape: a <c>Value</c> property beside a
    /// <c>Status</c> property of type <see cref="UsageValueStatus"/>.
    /// </summary>
    private static List<Type> PairedTypes() =>
        ModelTypes().Where(t => PairedValueTypeOf(t) is not null).ToList();

    private static HashSet<Type> PairedValueTypes() =>
        [.. PairedTypes().Select(PairedValueTypeOf).OfType<Type>()];

    /// <summary>
    /// The type a paired type's <c>Value</c> holds, unwrapped, <c>decimal</c> for
    /// <see cref="EstimatedUsd"/>, or null when the type is not a paired one at all.
    /// </summary>
    private static Type? PairedValueTypeOf(Type type)
    {
        var status = type.GetProperty("Status", BindingFlags.Public | BindingFlags.Instance);
        var value = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

        return status?.PropertyType == typeof(UsageValueStatus) && value is not null
            ? Nullable.GetUnderlyingType(value.PropertyType)
            : null;
    }

    /// <summary>
    /// Public properties and fields declared on the type, with the type each holds. Properties
    /// and fields are the whole of a model's declared shape in this codebase, and a declared
    /// type is the only thing reflection can judge a nullable by.
    /// </summary>
    private static IEnumerable<(string Name, Type Type)> ValueMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var property in type.GetProperties(flags))
        {
            yield return (property.Name, property.PropertyType);
        }

        foreach (var field in type.GetFields(flags))
        {
            yield return (field.Name, field.FieldType);
        }
    }

    private static bool IsExempt(Type type, string member) =>
        Exemptions.Any(e => e.Type == type && e.Member == member);

    // Verified by hand, 2026-10-15: restoring `decimal? EstTodayUsd` as a parameter of
    // PanelStatistics turns NoPublicMemberInCoreModelsIsABareNullableFigure red with exactly:
    //
    //   Error Message:
    //    A public member of OView.Core.Models is a bare nullable of a value this app has a
    //   status-paired type for:
    //     - PanelStatistics.EstTodayUsd is a bare Decimal?, use EstimatedUsd.
    //
    //   Declare it as that paired type instead, so the member carries why the figure is absent
    //   rather than leaving each reader to infer it from the null. If its null genuinely means
    //   something else, add it to Exemptions above with that reason in words.
    //
    // Removing it again turns the test green. This comment exists so a reader can see the test
    // fails for the right reason, and names the member, without re-running the experiment.
}
