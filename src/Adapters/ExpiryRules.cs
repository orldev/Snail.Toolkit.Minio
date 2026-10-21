using Minio.DataModel.ILM;
using Snail.Toolkit.Minio.Domain;

namespace Snail.Toolkit.Minio.Adapters;

/// <summary>
/// The rules by which a bucket expires its objects, judged before they are sent and read back in this
/// library's terms.
/// </summary>
internal static class ExpiryRules
{
    /// <summary>The code a server answers with when a bucket expires nothing.</summary>
    private const string NoLifecycleConfiguration = "NoSuchLifecycleConfiguration";

    /// <summary>
    /// Turns one rule into what the SDK sends.
    /// </summary>
    /// <param name="rule">The rule to send.</param>
    /// <returns>The same rule in the SDK's shape.</returns>
    internal static LifecycleRule Rule(ExpiryRule rule) => new(
        null,
        rule.Id,
        new Expiration { Days = rule.Days },
        null,
        new RuleFilter(null, rule.Prefix, null),
        null,
        null,
        rule.IsEnabled ? LifecycleRule.LifecycleRuleStatusEnabled : LifecycleRule.LifecycleRuleStatusDisabled);

    /// <summary>
    /// Reads back the rules this library models, and no others.
    /// </summary>
    /// <param name="configuration">Everything the bucket carries.</param>
    /// <returns>The rules that expire objects after a number of days.</returns>
    /// <remarks>
    /// A bucket may also carry transitions, versioning rules and upload cleanups, which this library does
    /// not model. Reporting them as expiries would say an object dies on a day it does not.
    /// </remarks>
    internal static IReadOnlyList<ExpiryRule> Expiries(LifecycleConfiguration configuration)
        => configuration.Rules is not { Count: > 0 } rules
            ? []
            : [.. rules
                .Where(rule => rule.Expiration?.Days is > 0)
                .Select(rule => new ExpiryRule
                {
                    Id = rule.ID ?? string.Empty,
                    Days = (int)rule.Expiration!.Days!.Value,
                    Prefix = rule.Filter?.Prefix ?? string.Empty,
                    IsEnabled = string.Equals(
                        rule.Status,
                        LifecycleRule.LifecycleRuleStatusEnabled,
                        StringComparison.OrdinalIgnoreCase)
                })];

    /// <summary>
    /// Decides whether a failure is the server saying the bucket expires nothing.
    /// </summary>
    /// <param name="error">The failure to judge.</param>
    /// <returns><see langword="true"/> when there is no configuration rather than no answer.</returns>
    /// <remarks>
    /// A bucket with no rules is a state, not a fault, and the protocol reports it as one: the request is
    /// refused with <c>NoSuchLifecycleConfiguration</c>. Handing that back as a failure would make every
    /// caller translate the same code before it could act on the answer. Judged by the code rather than by
    /// the message, which the server is free to reword.
    /// </remarks>
    internal static bool IsUnset(StorageError? error)
        => string.Equals(error?.Code, NoLifecycleConfiguration, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the first thing wrong with a set of rules.
    /// </summary>
    /// <param name="rules">The rules to judge.</param>
    /// <returns>What is wrong, or <see langword="null"/> when they can be sent.</returns>
    /// <remarks>
    /// Checked here rather than at the server because the server's refusal names neither the rule nor the
    /// field, and a caller reading it cannot tell which of its rules was the bad one.
    /// </remarks>
    internal static StorageError? Fault(IReadOnlyList<ExpiryRule> rules)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                return new StorageError(StorageErrorKind.InvalidArgument, "A rule has to be named.");

            if (!seen.Add(rule.Id))
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The rule '{rule.Id}' is named twice, and the second would replace the first.");

            if (rule.Days <= 0)
                return new StorageError(
                    StorageErrorKind.InvalidArgument,
                    $"The rule '{rule.Id}' expires after {rule.Days} days, which is not a life.");
        }

        return null;
    }
}
