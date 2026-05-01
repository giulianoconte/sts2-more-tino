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

    /// Rewrites "tion" -> "tino" / "Tion" -> "Tino", but only outside of
    /// SmartFormat placeholders ({...}) and BBCode tags ([...]). Variable
    /// names and tag attributes can legitimately contain "tion" (e.g.
    /// "{exceptionType}" in main_menu_ui.MOD_ERROR.EXCEPTION; in principle
    /// also "[icon=potion]" or similar BBCode attributes), and rewriting
    /// them breaks template lookup or tag resolution.
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
        int braceDepth = 0;
        bool inBracket = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '{':
                    braceDepth++;
                    sb.Append(c);
                    continue;
                case '}':
                    if (braceDepth > 0) braceDepth--;
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
            }

            bool plainText = braceDepth == 0 && !inBracket;
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
