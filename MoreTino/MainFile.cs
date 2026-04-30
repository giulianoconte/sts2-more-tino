using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace MoreTino;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "MoreTino";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);

        // Apply patches individually so a missing target in one class doesn't
        // abort the rest. Mirrors the ColorlessCharacter pattern.
        foreach (var type in typeof(MainFile).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.CreateClassProcessor(type).Patch(); }
            catch (Exception e) { Logger.Warn($"MoreTino: Patch {type.Name} skipped — {e.Message}"); }
        }

        Logger.Info("MoreTino mod loaded successfully.");
    }
}
