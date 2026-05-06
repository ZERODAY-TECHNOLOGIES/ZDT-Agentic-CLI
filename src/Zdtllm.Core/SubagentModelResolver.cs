using System.Collections.Immutable;

namespace Zdtllm.Core;

/// <summary>
/// Resolves which model a subagent of a given <c>subagent_type</c> should run on. The
/// design copies what the official claude-cli does internally — read-only / explore
/// subagents run on the cheap "fast" tier (Haiku-equivalent) while the parent stays on
/// the heavier model. zdt expresses tiers as alias names (light/medium/heavy) that the
/// user wires to actual model ids in <c>litellm.models</c>.
///
/// Resolution priority (first non-empty wins):
///   1. <c>litellm.subagentModels[type]</c> — the user's explicit override. The value can
///      be either a tier alias from <c>litellm.models</c> (preferred — keeps the model id
///      in one place) or a literal model id (escape hatch when a subagent needs something
///      that's not in the alias map).
///   2. <c>smallFastModel</c> — populated from the <c>ZDT_SMALL_FAST_MODEL</c> env var (the
///      zdt rename of claude-cli's <c>ANTHROPIC_SMALL_FAST_MODEL</c>). Applies ONLY to the
///      "small/fast" subagent profiles (<c>code-reviewer</c>, <c>explore</c>) — those that
///      claude-cli internally dispatches on the haiku tier. <c>general-purpose</c> and any
///      custom type ignore this layer because they aren't conceptually "fast subagents".
///   3. Built-in default for the type — <c>code-reviewer</c> and <c>explore</c> default to
///      the <c>light</c> alias (they're meant to be cheap, parallel, and read-only). All
///      other types — including <c>general-purpose</c> — default to inheriting the parent.
///   4. Returns <c>null</c> — meaning "no tier override, inherit the parent's model". The
///      caller is responsible for falling back; the resolver explicitly does NOT return the
///      parent model so callers can distinguish "no override" from "override happens to
///      match parent" (the second case still needs explicit pinning so a /model switch on
///      the parent doesn't sneak through to a subagent the user pinned to a tier).
///
/// All lookups are alias-expanded against <c>modelAliases</c>: an override of "light" maps
/// through to whatever the user wired light to. Literal model ids that aren't aliases pass
/// through unchanged (escape hatch for "I want this exact model for this subagent").
/// </summary>
public static class SubagentModelResolver
{
    /// <summary>Tiers that read-only / cheap subagent profiles default to.</summary>
    private static readonly ImmutableDictionary<string, string> BuiltinDefaults =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            new KeyValuePair<string, string>("code-reviewer", "light"),
            new KeyValuePair<string, string>("explore", "light"),
            // general-purpose intentionally absent → fall through to parent inheritance.
        });

    /// <summary>Subagent types that the <c>ZDT_SMALL_FAST_MODEL</c> env var applies to.</summary>
    private static readonly HashSet<string> SmallFastEligibleTypes =
        new(StringComparer.Ordinal) { "code-reviewer", "explore" };

    /// <summary>
    /// Pick the model id to run a subagent of <paramref name="subagentType"/> on, or null
    /// if no tier override applies (caller inherits the parent's model in that case).
    /// </summary>
    /// <param name="subagentType">The <c>subagent_type</c> the parent requested via the Agent tool.</param>
    /// <param name="modelAliases">The <c>litellm.models</c> alias map (alias → model id).</param>
    /// <param name="subagentOverrides">The <c>litellm.subagentModels</c> map (subagent_type → alias|model id).</param>
    /// <param name="smallFastModel">Optional model id from <c>ZDT_SMALL_FAST_MODEL</c>; applies to fast subagents only.</param>
    public static string? Resolve(
        string subagentType,
        IReadOnlyDictionary<string, string> modelAliases,
        IReadOnlyDictionary<string, string> subagentOverrides,
        string? smallFastModel = null)
    {
        ArgumentNullException.ThrowIfNull(subagentType);
        ArgumentNullException.ThrowIfNull(modelAliases);
        ArgumentNullException.ThrowIfNull(subagentOverrides);

        // (1) explicit override
        if (subagentOverrides.TryGetValue(subagentType, out var overrideValue)
            && !string.IsNullOrEmpty(overrideValue))
        {
            return ExpandAliasOrLiteral(overrideValue, modelAliases);
        }

        // (2) ZDT_SMALL_FAST_MODEL — only for fast subagent profiles. The env var is also
        // alias-expanded so users can write either the literal model id or an alias name
        // (e.g. ZDT_SMALL_FAST_MODEL=light) and get consistent semantics with subagentModels.
        if (!string.IsNullOrEmpty(smallFastModel)
            && SmallFastEligibleTypes.Contains(subagentType))
        {
            return ExpandAliasOrLiteral(smallFastModel, modelAliases);
        }

        // (3) built-in default — only kicks in when the alias actually resolves to a model.
        // If the user removed e.g. the "light" entry from litellm.models, we silently fall
        // through to "no override" rather than emitting a phantom alias that AgentLoop would
        // pass to LiteLLM as-is (which would 404 at request time).
        if (BuiltinDefaults.TryGetValue(subagentType, out var defaultAlias)
            && modelAliases.TryGetValue(defaultAlias, out var defaultModel)
            && !string.IsNullOrEmpty(defaultModel))
        {
            return defaultModel;
        }

        // (4) no override — caller falls back to the parent.
        return null;
    }

    /// <summary>
    /// If <paramref name="value"/> matches an alias key in <paramref name="aliases"/>, expand
    /// to the mapped model id. Otherwise treat the value as a literal model id. Returns null
    /// only when <paramref name="value"/> itself is null/empty (so the caller can fall back).
    /// </summary>
    private static string? ExpandAliasOrLiteral(
        string? value,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return aliases.TryGetValue(value, out var resolved) && !string.IsNullOrEmpty(resolved)
            ? resolved
            : value;
    }
}
