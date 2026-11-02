using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppCodeBase.Components.EssenceSeparator;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Potions.Liquid;
using Il2CppCodeBase.Services.ConfigsService;
using UnityEngine;
using Il2CppUnits = Il2CppSystem.Collections.Generic.List<Il2Cpp.eSectionFamily>;

namespace PotionCoop.Game
{
    /// <summary>
    /// Liquid is only ever moved in this game, never made or destroyed: a lab always holds exactly the units its bottles
    /// were born with (its manifest). The host (or a player alone) counts now and then. When the count stays off for a
    /// while (a sync race, or an older version of this mod), the difference goes back into, or comes out of, resting
    /// bottles of that family, and the log says what was done.
    /// </summary>
    internal static class Liquid
    {
        const int Families = 14; // eSectionFamily: None + 13
        const float CheckInterval = 12f;
        const int ChecksToAgree = 3;
        /// <summary>A bigger difference means we are not counting the same thing as the game: never touch it.</summary>
        const int MaxRepair = 40;
        const int PotionsPerFrame = 700;

        static IntPtr _lab;
        static int[] _birth;
        static bool _givenUp;
        static float _nextCheck;
        static int[] _lastDiff;
        static int _agreeing;
        static bool _reportedBalanced;

        // a count in progress, a few hundred bottles per frame
        static PotionFacade[] _counting;
        static int _countIndex = -1;
        static int[] _acc;

        public static string Name(int family) => ((eSectionFamily)family).ToString();

        /// <summary>Units per family the lab's bottles were born with.</summary>
        static int[] Birth(LabPotionsPopulator populator)
        {
            var manifest = populator.Manifest();
            if (manifest == null)
                return null;
            var count = manifest.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<PotionManifestEntry>>().Count;
            var totals = new int[Families];
            for (var i = 0; i < count; i++)
            {
                var units = manifest[i]?.BirthUnits;
                if (units == null)
                    continue;
                for (var u = 0; u < units.Length; u++)
                {
                    var family = (int)units[u];
                    if (family > 0 && family < Families)
                        totals[family]++;
                }
            }
            return totals;
        }

        static void Add(int[] totals, Il2CppUnits units)
        {
            if (units == null)
                return;
            for (var i = 0; i < units.Count; i++)
            {
                var family = (int)units[i];
                if (family > 0 && family < Families)
                    totals[family]++;
            }
        }

        /// <summary>The fixtures: vats and the separator's loop (its units on their way in or out, too).</summary>
        static void AddFixtures(int[] totals)
        {
            foreach (var vat in World.Vats())
                Add(totals, vat.Contents._units);
            foreach (var separator in World.Separators())
            {
                var loop = new Il2CppUnits();
                separator.CopyLoopUnits(loop);
                Add(totals, loop);
            }
        }

        /// <summary>
        /// The separator is moving liquid between its loop and the bottles hanging on it: not a moment to count.
        /// (Units simply riding the loop are counted with it, however long they stay.)
        /// </summary>
        static bool SeparatorBusy()
        {
            foreach (var separator in World.Separators())
            {
                var waiting = separator._circulator?._waiting;
                for (var i = 0; waiting != null && i < waiting.Count; i++)
                    if (waiting[i] != null && waiting[i].Count > 0)
                        return true;
                var holders = separator._holders;
                for (var i = 0; holders != null && i < holders.Length; i++)
                    if (holders[i] != null && (holders[i].IsPouringOut || holders[i].IsDripping))
                        return true;
            }
            return false;
        }

        /// <summary>Per frame in the lab, on the host or alone (a guest's world follows the host's).</summary>
        public static void Tick()
        {
            var populator = GameRefs.Populator;
            if (!GameRefs.InLevel || populator == null || (Coop.IsConnected && !Coop.IsHost))
            {
                _countIndex = -1;
                return;
            }
            if (populator.Pointer != _lab)
                NewLab(populator);
            if (_givenUp)
                return;

            try
            {
                if (_countIndex < 0)
                {
                    if (Time.realtimeSinceStartup < _nextCheck)
                        return;
                    _nextCheck = Time.realtimeSinceStartup + CheckInterval;
                    var potions = new List<PotionFacade>(populator.PotionCount);
                    foreach (var entry in populator._potionsByUid)
                        potions.Add(entry.Value);
                    _counting = potions.ToArray();
                    _acc = new int[Families];
                    _countIndex = 0;
                }

                var end = Math.Min(_countIndex + PotionsPerFrame, _counting.Length);
                for (; _countIndex < end; _countIndex++)
                    if (_counting[_countIndex] != null)
                        Add(_acc, _counting[_countIndex].Contents._units);
                if (_countIndex < _counting.Length)
                    return;

                // The fixtures are counted in the last frame together with the last bottles, and nothing must be
                // in motion between them: otherwise this round does not count.
                _countIndex = -1;
                if (SeparatorBusy() || Pours.Busy)
                {
                    _agreeing = 0;
                    return;
                }
                AddFixtures(_acc);
                Judge(populator, _acc);
            }
            catch (Exception e)
            {
                _givenUp = true;
                CoopMod.Log.Warning($"[liquid] check off for this lab: {e.Message}");
            }
        }

        static void NewLab(LabPotionsPopulator populator)
        {
            _lab = populator.Pointer;
            _givenUp = false;
            _lastDiff = null;
            _agreeing = 0;
            _reportedBalanced = false;
            _countIndex = -1;
            _nextCheck = Time.realtimeSinceStartup + 5f;
            _birth = Birth(populator);
            var manifestCount = populator.Manifest()?.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<PotionManifestEntry>>().Count ?? -1;
            if (_birth == null || manifestCount != populator.PotionCount)
            {
                _givenUp = true;
                CoopMod.Log.Warning($"[liquid] not checking this lab: {manifestCount} bottles in its manifest, {populator.PotionCount} in the lab");
            }
        }

        static void Judge(LabPotionsPopulator populator, int[] now)
        {
            var diff = new int[Families];
            var off = 0;
            for (var f = 1; f < Families; f++)
            {
                diff[f] = now[f] - _birth[f];
                off += Math.Abs(diff[f]);
            }

            if (off == 0)
            {
                _lastDiff = null;
                _agreeing = 0;
                if (!_reportedBalanced)
                {
                    _reportedBalanced = true;
                    CoopMod.Log.Msg($"[liquid] all {now.Sum()} units of this lab are accounted for");
                }
                return;
            }
            _reportedBalanced = false;
            if (off > MaxRepair)
            {
                _givenUp = true;
                CoopMod.Log.Warning($"[liquid] this lab is {off} units away from its manifest ({Describe(diff)}): too far to be ours, leaving it");
                return;
            }

            _agreeing = _lastDiff != null && _lastDiff.SequenceEqual(diff) ? _agreeing + 1 : 1;
            _lastDiff = diff;
            if (_agreeing < ChecksToAgree)
                return;
            _agreeing = 0;
            _lastDiff = null;
            Repair(populator, diff);
        }

        static string Describe(int[] diff) =>
            string.Join(", ", Enumerable.Range(1, Families - 1).Where(f => diff[f] != 0).Select(f => $"{Name(f)} {diff[f]:+0;-0}"));

        /// <summary>Bottles we may change: resting on a shelf, not in anyone's hand, not finished.</summary>
        static List<(PotionFacade potion, Il2CppCodeBase.Components.Slots.SlotFacade slot)> Candidates(LabPotionsPopulator populator)
        {
            var held = World.LocalHeld(populator);
            var list = new List<(PotionFacade, Il2CppCodeBase.Components.Slots.SlotFacade)>();
            foreach (var entry in populator._slotsByUid)
            {
                var slot = entry.Value;
                var potion = slot != null ? slot.Potion : null;
                if (potion == null || held.Contains(potion.Uid) || World.RemoteHeld.Contains(potion.Uid))
                    continue;
                if (potion.IsSealed || potion.IsFinished.Value || potion.IsHanging || potion.CarryState.Value != ePotionCarryState.Idle)
                    continue;
                list.Add((potion, slot));
            }
            return list;
        }

        static void Repair(LabPotionsPopulator populator, int[] diff)
        {
            var candidates = Candidates(populator);
            var persistence = GameRefs.Persistence;
            var done = new List<string>();
            for (var f = 1; f < Families; f++)
            {
                if (diff[f] < 0)
                    PutBack(f, -diff[f], candidates, persistence, done);
                else if (diff[f] > 0)
                    TakeBack(f, diff[f], candidates, persistence, done);
            }
            CoopMod.Log.Msg($"[liquid] the lab was off by {Describe(diff)}; put right: {string.Join("; ", done)}");
        }

        /// <summary>
        /// Missing units: on top of bottles already ending in this family, then into empty ones, never filling a
        /// bottle up (a pour finishes a bottle, we don't); failing that on top of any bottle, or into a vat.
        /// </summary>
        static void PutBack(int f, int missing, List<(PotionFacade potion, Il2CppCodeBase.Components.Slots.SlotFacade slot)> candidates,
            Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence persistence, List<string> done)
        {
            var family = (eSectionFamily)f;
            int Rank(LiquidContents c) =>
                !c.IsEmpty && c.TopFamily == family ? (c.IsUniform ? 0 : 1) : c.IsEmpty ? 2 : 3;
            foreach (var (potion, slot) in candidates.OrderBy(c => Rank(c.potion.Contents)).ToList())
            {
                if (missing == 0)
                    return;
                var room = potion.Contents.FreeSpace - 1;
                if (room <= 0)
                    continue;
                var put = Math.Min(room, missing);
                var units = World.ReadUnits(potion.Contents._units).ToList();
                units.AddRange(Enumerable.Repeat((byte)f, put));
                Set(potion, slot, units, persistence);
                missing -= put;
                done.Add($"{Name(f)} +{put} into #{potion.Uid}");
            }
            foreach (var vat in World.Vats())
            {
                if (missing == 0)
                    return;
                var put = Math.Min(vat.Contents.FreeSpace, missing);
                if (put <= 0)
                    continue;
                var units = World.ReadUnits(vat.Contents._units).ToList();
                units.AddRange(Enumerable.Repeat((byte)f, put));
                vat.SetLiquidContents(World.ToIl2Cpp(units.ToArray()));
                persistence.RecordVat(vat);
                missing -= put;
                done.Add($"{Name(f)} +{put} into vat #{vat.Uid}");
            }
            if (missing > 0)
                done.Add($"{Name(f)}: {missing} found no room");
        }

        /// <summary>Doubled units: off the top of bottles ending in this family, then out of any bottle or vat holding it.</summary>
        static void TakeBack(int f, int extra, List<(PotionFacade potion, Il2CppCodeBase.Components.Slots.SlotFacade slot)> candidates,
            Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence persistence, List<string> done)
        {
            var family = (eSectionFamily)f;
            foreach (var (potion, slot) in candidates.OrderBy(c => !c.potion.Contents.IsEmpty && c.potion.Contents.TopFamily == family ? 0 : 1).ToList())
            {
                if (extra == 0)
                    return;
                var units = World.ReadUnits(potion.Contents._units).ToList();
                var taken = TakeTopmost(units, (byte)f, extra);
                if (taken == 0)
                    continue;
                Set(potion, slot, units, persistence);
                extra -= taken;
                done.Add($"{Name(f)} -{taken} from #{potion.Uid}");
            }
            foreach (var vat in World.Vats())
            {
                if (extra == 0)
                    return;
                var units = World.ReadUnits(vat.Contents._units).ToList();
                var taken = TakeTopmost(units, (byte)f, extra);
                if (taken == 0)
                    continue;
                vat.SetLiquidContents(World.ToIl2Cpp(units.ToArray()));
                persistence.RecordVat(vat);
                extra -= taken;
                done.Add($"{Name(f)} -{taken} from vat #{vat.Uid}");
            }
            if (extra > 0)
                done.Add($"{Name(f)}: {extra} could not be taken back");
        }

        static int TakeTopmost(List<byte> units, byte family, int count)
        {
            var taken = 0;
            for (var i = units.Count - 1; i >= 0 && taken < count; i--)
                if (units[i] == family)
                {
                    units.RemoveAt(i);
                    taken++;
                }
            return taken;
        }

        static void Set(PotionFacade potion, Il2CppCodeBase.Components.Slots.SlotFacade slot, List<byte> units,
            Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence persistence)
        {
            potion.SetLiquidContents(World.ToIl2Cpp(units.ToArray()));
            World.FixVisual(potion);
            // Saved, and (hosting) sent to the guest like any change of ours.
            persistence.RecordShelved(potion, slot);
        }
    }
}
