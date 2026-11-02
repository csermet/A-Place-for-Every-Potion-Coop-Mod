using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Slots;
using Il2CppCodeBase.Components.Vats;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic.List<Il2Cpp.eSectionFamily>;
using Il2CppReadOnlyList = Il2CppSystem.Collections.Generic.IReadOnlyList<Il2Cpp.eSectionFamily>;
using Object = UnityEngine.Object;

namespace PotionCoop
{
    /// <summary>Where one vessel is and what is inside it.</summary>
    internal sealed class PotionState
    {
        public int Uid;
        public ePotionLocation Location;
        public int SlotUid;
        public Vector3 Position;
        public Quaternion Rotation;
        public byte[] Units;

        public bool SamePlace(PotionState other) =>
            Location == other.Location && SlotUid == other.SlotUid &&
            (Location != ePotionLocation.Floor || (Position - other.Position).sqrMagnitude < 0.0004f);

        public bool SameUnits(PotionState other) => Units.SequenceEqual(other.Units);
    }

    internal sealed class VatState
    {
        public int Uid;
        public byte[] Units;
    }

    internal sealed class WorldSnapshot
    {
        public readonly Dictionary<int, PotionState> Potions = new();
        public readonly Dictionary<int, VatState> Vats = new();
    }

    /// <summary>Reads and writes the shared world (vessels on floor/shelves, liquids, vats).</summary>
    internal static class WorldState
    {
        public static LabPotionsPopulator FindPopulator() => Object.FindFirstObjectByType<LabPotionsPopulator>();

        public static byte[] ReadUnits(Il2CppList units)
        {
            var result = new byte[units.Count];
            for (var i = 0; i < units.Count; i++)
                result[i] = (byte)units[i];
            return result;
        }

        public static Il2CppReadOnlyList ToIl2Cpp(byte[] units)
        {
            var list = new Il2CppList(units.Length);
            foreach (var unit in units)
                list.Add((eSectionFamily)unit);
            return list.Cast<Il2CppReadOnlyList>();
        }

        static HashSet<int> Uids(Il2CppCodeBase.Common.Utilities.ReactiveList<PotionFacade> list)
        {
            var set = new HashSet<int>();
            for (var i = 0; i < list.Count; i++)
                set.Add(list[i].Uid);
            return set;
        }

        public static Dictionary<int, SlotFacade> SlotsByPotion(LabPotionsPopulator populator)
        {
            var map = new Dictionary<int, SlotFacade>();
            foreach (var entry in populator._slotsByUid)
            {
                var slot = entry.Value;
                var potion = slot.Potion;
                if (potion != null)
                    map[potion.Uid] = slot;
            }
            return map;
        }

        public static WorldSnapshot Capture(LabPotionsPopulator populator)
        {
            var snapshot = new WorldSnapshot();
            var slotOf = SlotsByPotion(populator);
            var held = Uids(populator.HeldPotions());
            var apron = Uids(populator.ApronPotions());

            foreach (var entry in populator._potionsByUid)
            {
                var potion = entry.Value;
                var uid = potion.Uid;
                var state = new PotionState
                {
                    Uid = uid,
                    Position = potion.transform.position,
                    Rotation = potion.transform.rotation,
                    Units = ReadUnits(potion.Contents._units),
                };

                if (held.Contains(uid))
                    state.Location = ePotionLocation.Hand;
                else if (apron.Contains(uid))
                    state.Location = ePotionLocation.Apron;
                else if (slotOf.TryGetValue(uid, out var slot))
                {
                    state.Location = ePotionLocation.Slot;
                    state.SlotUid = slot.Uid;
                }
                else if (potion.IsHanging)
                    state.Location = ePotionLocation.Holder;
                else
                    state.Location = ePotionLocation.Floor;

                snapshot.Potions[uid] = state;
            }

            Il2CppArrayBase<VatFacade> vats = Object.FindObjectsByType<VatFacade>(FindObjectsSortMode.None);
            foreach (var vat in vats)
                snapshot.Vats[vat.Uid] = new VatState { Uid = vat.Uid, Units = ReadUnits(vat.Contents._units) };

            return snapshot;
        }

        /// <summary>
        /// Moves every floor/shelf vessel to the target state. Vessels in the local hand, apron or on a holder
        /// are left alone (remote changes never touch the local player's hands).
        /// </summary>
        public static int Apply(LabPotionsPopulator populator, WorldSnapshot target)
        {
            var current = Capture(populator);
            var slotOf = SlotsByPotion(populator);
            var persistence = populator._potionsPersistence;
            var moved = 0;

            bool Movable(PotionState s) => s.Location == ePotionLocation.Floor || s.Location == ePotionLocation.Slot;

            var toMove = target.Potions.Values
                .Where(t => current.Potions.TryGetValue(t.Uid, out var c) && Movable(t) && Movable(c) && !t.SamePlace(c))
                .ToList();

            // Pass 1: empty every slot a moving vessel leaves, so pass 2 never finds a stale occupant.
            foreach (var t in toMove)
                if (slotOf.TryGetValue(t.Uid, out var oldSlot))
                    oldSlot.ClearPotion(true);

            // Pass 2: place.
            foreach (var t in toMove)
            {
                if (!populator.TryGetPotion(t.Uid, out var potion))
                    continue;

                if (t.Location == ePotionLocation.Slot)
                {
                    if (!populator.TryGetSlot(t.SlotUid, out var slot))
                        continue;
                    if (slot.Potion != null && slot.Potion.Uid != t.Uid)
                        slot.ClearPotion(true);

                    slot.PlacePotion(potion, true);
                    if (potion.transform.parent != slot.PotionMount)
                        potion.BeginFlyToSlot(slot.PotionMount, true);
                    persistence.RecordShelved(potion, slot);
                }
                else
                {
                    potion.BeginFlyToDrop(populator._floorPotionsRoot, t.Position, t.Rotation, true);
                    persistence.RecordDropped(potion);
                }
                moved++;
            }

            // Liquids.
            var refilled = 0;
            foreach (var t in target.Potions.Values)
            {
                if (!current.Potions.TryGetValue(t.Uid, out var c) || t.SameUnits(c) || !Movable(c))
                    continue;
                if (populator.TryGetPotion(t.Uid, out var potion))
                {
                    potion.SetLiquidContents(ToIl2Cpp(t.Units));
                    refilled++;
                }
            }

            Il2CppArrayBase<VatFacade> vats = Object.FindObjectsByType<VatFacade>(FindObjectsSortMode.None);
            foreach (var vat in vats)
            {
                if (!target.Vats.TryGetValue(vat.Uid, out var t) || t.Units.SequenceEqual(ReadUnits(vat.Contents._units)))
                    continue;
                vat.SetLiquidContents(ToIl2Cpp(t.Units));
                persistence.RecordVat(vat);
                refilled++;
            }

            CoopMod.Log.Msg($"[probe] apply: moved={moved} refilled={refilled}");
            return moved + refilled;
        }

        public static string Summary(WorldSnapshot s) =>
            string.Join(", ", s.Potions.Values.GroupBy(p => p.Location).Select(g => $"{g.Key}={g.Count()}")) +
            $", vats={s.Vats.Count}";
    }
}
