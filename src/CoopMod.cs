using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppCodeBase.Components.Hero;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Slots;
using Il2CppCodeBase.Components.Vats;
using Il2CppCodeBase.Services.GameStateMachineService;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using Il2CppCodeBase.Services.SaveLoadService;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(PotionCoop.CoopMod), "PotionCoop", "0.0.1", "caner")]
[assembly: MelonGame("CubeQuad", "A Place for Every Potion")]

namespace PotionCoop
{
    public class CoopMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static readonly Stopwatch Clock = Stopwatch.StartNew();

        HeroFacade _hero;
        LabPotionsPopulator _populator;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Log.Msg("PotionCoop loaded (phase 0 probe)");
        }

        public override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.ctrlKey.isPressed)
                return;

            if (kb.f7Key.wasPressedThisFrame)
                DumpHero();
        }

        void DumpHero()
        {
            if (_hero == null)
                _hero = Object.FindFirstObjectByType<HeroFacade>();
            if (_populator == null)
                _populator = Object.FindFirstObjectByType<LabPotionsPopulator>();

            if (_hero == null)
            {
                Log.Msg("[probe] HeroFacade not found (not in a level?)");
                return;
            }

            var look = _hero.Look;
            Log.Msg($"[probe] hero pos={_hero.Position} yaw={look._yaw:F1} pitch={look._pitch:F1} " +
                    $"potions={(_populator != null ? _populator.PotionCount : -1)}");
        }

        internal static string Stamp() => $"t={Clock.Elapsed.TotalSeconds:F2}s";

        internal static string Describe(PotionFacade potion)
        {
            if (potion == null)
                return "null";
            return $"#{potion.Uid} carry={potion.CarryState.Value} pos={potion.transform.position}";
        }
    }

    [HarmonyPatch(typeof(GameStateMachine), nameof(GameStateMachine.Enter), new[] { typeof(eGameState) })]
    static class GameStateEnterProbe
    {
        static void Postfix(eGameState __0) => CoopMod.Log.Msg($"[probe] state -> {__0} {CoopMod.Stamp()}");
    }

    // Tells us when (boot vs. level load) the game reads each save key.
    [HarmonyPatch(typeof(SaveLoadService), nameof(SaveLoadService.KeyToFilePath))]
    static class KeyToFilePathProbe
    {
        static readonly HashSet<string> Seen = new();

        static void Postfix(string __0, string __result)
        {
            if (Seen.Add(__0))
                CoopMod.Log.Msg($"[probe] KeyToFilePath '{__0}' -> {__result} {CoopMod.Stamp()}");
        }
    }

    [HarmonyPatch(typeof(PotionsPersistence))]
    static class PersistenceProbe
    {
        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordDropped))]
        static void Dropped(PotionFacade __0) => CoopMod.Log.Msg($"[probe] RecordDropped {CoopMod.Describe(__0)}");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordPlaced))]
        static void Placed(PotionFacade __0) => CoopMod.Log.Msg($"[probe] RecordPlaced {CoopMod.Describe(__0)}");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordShelved))]
        static void Shelved(PotionFacade __0, SlotFacade __1) =>
            CoopMod.Log.Msg($"[probe] RecordShelved {CoopMod.Describe(__0)} slot=#{(__1 != null ? __1.Uid : 0)}");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordHandRow))]
        static void HandRow() => CoopMod.Log.Msg("[probe] RecordHandRow");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordApronRow))]
        static void ApronRow() => CoopMod.Log.Msg("[probe] RecordApronRow");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordVat))]
        static void Vat(VatFacade __0) => CoopMod.Log.Msg("[probe] RecordVat");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordHung))]
        static void Hung(PotionFacade __0) => CoopMod.Log.Msg($"[probe] RecordHung {CoopMod.Describe(__0)}");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordSeparator))]
        static void Separator() => CoopMod.Log.Msg("[probe] RecordSeparator");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.TryScoreShelve))]
        static void Score(PotionFacade __0, bool __result) =>
            CoopMod.Log.Msg($"[probe] TryScoreShelve {CoopMod.Describe(__0)} -> {__result}");

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.Flush))]
        static void Flush() => CoopMod.Log.Msg($"[probe] Flush {CoopMod.Stamp()}");
    }
}
