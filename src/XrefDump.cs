using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppCodeBase.Components.Hero;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Slots;
using Il2CppCodeBase.Components.Vats;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.XrefScans;
using MelonLoader.Utils;

namespace PotionCoop
{
    /// <summary>
    /// Dev tool: writes which game methods the key gameplay methods call, in call order,
    /// so we can mirror the game's own sequence when applying remote changes.
    /// </summary>
    internal static class XrefDump
    {
        static readonly (Type type, string method)[] Targets =
        {
            (typeof(HeroPotionPicker), nameof(HeroPotionPicker.ShelveInto)),
            (typeof(HeroPotionPicker), nameof(HeroPotionPicker.Grab)),
            (typeof(HeroPotionPicker), nameof(HeroPotionPicker.ReleaseFromCarry)),
            (typeof(HeroPotionPicker), nameof(HeroPotionPicker.TryDrop)),
            (typeof(HeroPotionPicker), nameof(HeroPotionPicker.TryPourIntoShelvedPotion)),
            (typeof(HeroFacade), nameof(HeroFacade.ShelveInto)),
            (typeof(HeroFacade), nameof(HeroFacade.Grab)),
            (typeof(SlotFacade), nameof(SlotFacade.PlacePotion)),
            (typeof(SlotFacade), nameof(SlotFacade.ClearPotion)),
            (typeof(PotionFacade), nameof(PotionFacade.BeginFlyToSlot)),
            (typeof(PotionFacade), nameof(PotionFacade.BeginFlyToDrop)),
            (typeof(PotionFacade), nameof(PotionFacade.SetLiquidContents)),
            (typeof(PotionCarrier), nameof(PotionCarrier.BeginFlyToSlot)),
            (typeof(PotionCarrier), nameof(PotionCarrier.BeginFlyToDrop)),
            (typeof(PotionCarrier), nameof(PotionCarrier.MarkShelved)),
            (typeof(LabPotionsPopulator), nameof(LabPotionsPopulator.TrySpawnIntoSlot)),
            (typeof(LabPotionsPopulator), nameof(LabPotionsPopulator.TrySpawn)),
            (typeof(PotionsPersistence), nameof(PotionsPersistence.RecordShelved)),
            (typeof(PotionsPersistence), nameof(PotionsPersistence.RecordDropped)),
            (typeof(PotionsPersistence), nameof(PotionsPersistence.RecordHandRow)),
            (typeof(PotionsPersistence), nameof(PotionsPersistence.WatchForSettle)),
            (typeof(VatFacade), nameof(VatFacade.SetLiquidContents)),
        };

        public static void Run()
        {
            var sb = new StringBuilder();
            foreach (var (type, name) in Targets)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                             .Where(m => m.Name == name))
                {
                    sb.AppendLine($"=== {type.Name}.{name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
                    try
                    {
                        foreach (var xref in XrefScanner.XrefScan(method))
                        {
                            if (xref.Type == XrefType.Method)
                            {
                                var callee = xref.TryResolve();
                                if (callee != null)
                                    sb.AppendLine($"    call {callee.DeclaringType?.Name}.{callee.Name}");
                            }
                            else if (xref.Type == XrefType.Global)
                            {
                                var str = xref.ReadAsObject()?.ToString();
                                if (!string.IsNullOrEmpty(str))
                                    sb.AppendLine($"    str  \"{str.Replace("\n", "\\n")}\"");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine($"    !! scan failed: {e.GetType().Name}: {e.Message}");
                    }
                }
            }

            var path = Path.Combine(MelonEnvironment.UserDataDirectory, "PotionCoop_xrefs.txt");
            File.WriteAllText(path, sb.ToString());
            CoopMod.Log.Msg($"[probe] xref dump written to {path}");
        }
    }
}
