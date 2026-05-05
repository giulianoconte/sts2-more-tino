using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace MoreTino;

/// Postfix on LocManager.SetLanguage. When the active language is English, walk
/// every loaded LocTable and rewrite "tion" -> "tino" / "Tion" -> "Tino" in
/// every value, mutating the underlying _translations dict in place.
///
/// Mirrors BaseLib.Patches.Localization.ModelLocPatch's reflection trick: the
/// _translations field is private, so we grab it via AccessTools once and reuse.
///
/// Known PoC limitation: BaseLib injects mod-added card/potion/etc. loc into
/// these same dicts later (postfix on ModelDb.Init). Mod content added after
/// SetLanguage runs will keep its original "tion" spellings. Add a second hook
/// on ModelDb.Init postfix if we need that coverage.
[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
internal static class LocManagerSetLanguagePatch
{
    private static readonly FieldInfo TablesField =
        AccessTools.Field(typeof(LocManager), "_tables");

    private static readonly FieldInfo TranslationsField =
        AccessTools.Field(typeof(LocTable), "_translations");

    [HarmonyPostfix]
    private static void Postfix(LocManager __instance)
    {
        if (__instance.Language != "eng")
        {
            return;
        }

        var tables = (Dictionary<string, LocTable>?)TablesField.GetValue(__instance);
        if (tables == null)
        {
            MainFile.Logger.Warn("MoreTino: LocManager._tables was null; nothing to rewrite.");
            return;
        }

        int totalRewritten = 0;
        int totalEntries = 0;
        foreach (var (tableName, locTable) in tables)
        {
            var dict = (Dictionary<string, string>?)TranslationsField.GetValue(locTable);
            if (dict == null) continue;

            int beforeCount = totalRewritten;
            // Snapshot keys so we can mutate values during iteration.
            foreach (var key in dict.Keys.ToList())
            {
                var original = dict[key];
                var transformed = Rewrite(original);
                if (!ReferenceEquals(original, transformed))
                {
                    dict[key] = transformed;
                    totalRewritten++;
                }
                totalEntries++;
            }

            int delta = totalRewritten - beforeCount;
            if (delta > 0)
            {
                MainFile.Logger.Info($"MoreTino: rewrote {delta} entries in table '{tableName}'.");
            }
        }

        MainFile.Logger.Info(
            $"MoreTino: rewrote {totalRewritten}/{totalEntries} loc entries across {tables.Count} tables (eng).");
    }

    /// Rewrites "tion" -> "tino" / "Tion" -> "Tino" only in spans that render
    /// as user-visible English text. Two kinds of context are skipped:
    ///
    /// 1. BBCode tag interiors ("[...]") — tag names and attributes like
    ///    "[icon=potion]" must be preserved or the tag stops resolving.
    ///
    /// 2. SmartFormat placeholder selector/formatter slots. A placeholder
    ///    has the shape "{selector[:formatter[:literal_options]]}". The
    ///    selector (e.g. "exceptionType" in main_menu_ui.MOD_ERROR.EXCEPTION)
    ///    and formatter name (e.g. "plural", "cond", "choose") must be
    ///    preserved verbatim — rewriting them breaks lookups. But the
    ///    literal_options slot (after the second ':') contains user-facing
    ///    English: "{Potions:plural:potion|potions}" needs to render as
    ///    "potinos" when count != 1, otherwise plural-bearing strings like
    ///    Phial Holster's relic description leak unrewritten "potions".
    ///
    /// To distinguish the slots we track a per-brace-depth count of colons
    /// seen at that depth. Rewriting is enabled outside braces, or at depths
    /// where we're past the second colon. Nested braces inside literal
    /// options (e.g. "{Persist:cond:>1?[blue]{Persist}[/blue] |}") get their
    /// own depth frame, so the inner selector is still preserved.
    ///
    /// Returns the same string instance (reference-equal) when no
    /// substitution occurred, so the caller can cheaply skip the dict write.
    ///
    /// Also preserves "additional" (and its case/suffix variants) -- without
    /// this guard "additional" -> "additinoal" reads worse than the joke is
    /// worth, and the word appears in a lot of card text.
    ///
    /// Limitation: SmartFormat's "{{" / "}}" literal-brace escape is
    /// treated as opening/closing two placeholders, so any "tion" inside
    /// "{{...}}" is left alone. Vanilla loc strings don't appear to use
    /// this construct; revisit if it shows up.
    internal static string Rewrite(string value)
    {
        if (value.IndexOf("tion", System.StringComparison.Ordinal) < 0
            && value.IndexOf("Tion", System.StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        var colonsAtDepth = new List<int>();
        bool inBracket = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '{':
                    colonsAtDepth.Add(0);
                    sb.Append(c);
                    continue;
                case '}':
                    if (colonsAtDepth.Count > 0) colonsAtDepth.RemoveAt(colonsAtDepth.Count - 1);
                    sb.Append(c);
                    continue;
                case '[':
                    inBracket = true;
                    sb.Append(c);
                    continue;
                case ']':
                    inBracket = false;
                    sb.Append(c);
                    continue;
                case ':':
                    if (colonsAtDepth.Count > 0) colonsAtDepth[^1]++;
                    sb.Append(c);
                    continue;
            }

            bool inLiteralSlot = colonsAtDepth.Count == 0 || colonsAtDepth[^1] >= 2;
            bool plainText = inLiteralSlot && !inBracket;
            if (plainText
                && (c == 't' || c == 'T')
                && i + 3 < value.Length
                && value[i + 1] == 'i'
                && value[i + 2] == 'o'
                && value[i + 3] == 'n'
                && !IsInsideAdditional(value, i))
            {
                sb.Append(c == 'T' ? "Tino" : "tino");
                i += 3; // for-loop increment handles the +4th
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// True when the "tion" starting at tionStart is the middle of "additional"
    /// (or any case-variant). Catches "additional", "Additional", and the
    /// suffix forms like "additionally"/"additionals" -- anything where "tion"
    /// is preceded by "addi" and followed by "al". Case-insensitive on the
    /// surrounding letters so it also handles a hypothetical all-caps
    /// "ADDITIONAL" if/when ALL_CAPS rewrite support is added.
    private static bool IsInsideAdditional(string value, int tionStart)
    {
        if (tionStart < 4 || tionStart + 6 > value.Length) return false;
        return char.ToLowerInvariant(value[tionStart - 4]) == 'a'
            && char.ToLowerInvariant(value[tionStart - 3]) == 'd'
            && char.ToLowerInvariant(value[tionStart - 2]) == 'd'
            && char.ToLowerInvariant(value[tionStart - 1]) == 'i'
            && char.ToLowerInvariant(value[tionStart + 4]) == 'a'
            && char.ToLowerInvariant(value[tionStart + 5]) == 'l';
    }
}
