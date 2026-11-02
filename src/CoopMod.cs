using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        WorldSnapshot _snapshot;
        bool _loggedFirstUpdate;
        bool _loggedNoKeyboard;
        bool _legacyInputBroken;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Log.Msg("PotionCoop loaded (phase 1 probe)");
        }

        public override void OnLateInitializeMelon() => XrefDump.Run();

        public override void OnUpdate()
        {
            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Log.Msg($"[probe] OnUpdate alive {Stamp()}");
            }

            if (Pressed(Key.F7, KeyCode.F7))
                DumpHero();
            else if (Pressed(Key.F5, KeyCode.F5))
                TakeSnapshot();
            else if (Pressed(Key.F6, KeyCode.F6))
                RestoreSnapshot();
        }

        // Ctrl+key. Tries the new Input System first, then legacy Input as a fallback, and logs which one fired.
        bool Pressed(Key key, KeyCode legacy)
        {
            var kb = Keyboard.current;
            if (kb == null)
            {
                if (!_loggedNoKeyboard)
                {
                    _loggedNoKeyboard = true;
                    Log.Msg("[probe] Keyboard.current is null");
                }
            }
            else if (kb[key].wasPressedThisFrame)
            {
                var ctrl = kb.ctrlKey.isPressed;
                Log.Msg($"[probe] InputSystem saw {key} (ctrl={ctrl})");
                return ctrl;
            }

            if (_legacyInputBroken)
                return false;
            try
            {
                if (Input.GetKeyDown(legacy))
                {
                    var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    Log.Msg($"[probe] legacy Input saw {legacy} (ctrl={ctrl})");
                    return ctrl;
                }
            }
            catch (Exception e)
            {
                _legacyInputBroken = true;
                Log.Msg($"[probe] legacy Input unavailable: {e.GetType().Name}");
            }
            return false;
        }

        bool EnsurePopulator()
        {
            if (_populator == null)
                _populator = WorldState.FindPopulator();
            if (_populator == null || !_populator.IsPopulated)
            {
                Log.Msg("[probe] level not loaded");
                return false;
            }
            return true;
        }

        void TakeSnapshot()
        {
            if (!EnsurePopulator())
                return;
            var sw = Stopwatch.StartNew();
            _snapshot = WorldState.Capture(_populator);
            Log.Msg($"[probe] snapshot taken in {sw.ElapsedMilliseconds} ms: {WorldState.Summary(_snapshot)}");
        }

        void RestoreSnapshot()
        {
            if (_snapshot == null)
            {
                Log.Msg("[probe] no snapshot (press Ctrl+F5 first)");
                return;
            }
            if (!EnsurePopulator())
                return;
            var sw = Stopwatch.StartNew();
            try
            {
                WorldState.Apply(_populator, _snapshot);
            }
            catch (Exception e)
            {
                Log.Error($"[probe] apply failed: {e}");
            }
            var after = WorldState.Capture(_populator);
            var off = _snapshot.Potions.Values.Count(t =>
                after.Potions.TryGetValue(t.Uid, out var a) && (!t.SamePlace(a) || !t.SameUnits(a)));
            Log.Msg($"[probe] restore took {sw.ElapsedMilliseconds} ms; still different: {off}; now {WorldState.Summary(after)}");
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
