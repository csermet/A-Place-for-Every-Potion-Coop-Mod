using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppCodeBase.Common.Utilities;
using Il2CppCodeBase.Components.EssenceSeparator;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Slots;
using Il2CppCodeBase.Components.Vats;
using PotionCoop.Net;
using UnityEngine;
using Il2CppUnits = Il2CppSystem.Collections.Generic.List<Il2Cpp.eSectionFamily>;
using Il2CppReadOnlyUnits = Il2CppSystem.Collections.Generic.IReadOnlyList<Il2Cpp.eSectionFamily>;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>
    /// Reads vessel state in network terms and applies remote state to the local lab.
    /// Never touches the local player's hand or apron: a remote peer cannot take things out of your hands.
    /// </summary>
    internal static class World
    {
        // Empty Il2Cpp Nullable<> boxes (hasValue = false) for optional parameters.
        static readonly Il2CppSystem.Nullable<float> NoDuration = new();
        static readonly Il2CppSystem.Nullable<Il2CppDG.Tweening.Ease> NoEase = new();

        /// <summary>Uids of vessels the other player currently holds (hidden locally).</summary>
        public static readonly HashSet<int> RemoteHeld = new();

        /// <summary>True while remote state is being applied (our persistence hooks must not echo it back).</summary>
        public static bool Applying { get; private set; }

        public static NetLocation HeldBySelf => Coop.IsHost ? NetLocation.HeldByHost : NetLocation.HeldByClient;
        public static NetLocation HeldByOther => Coop.IsHost ? NetLocation.HeldByClient : NetLocation.HeldByHost;

        // ---------- reading ----------

        public static byte[] ReadUnits(Il2CppUnits units)
        {
            var result = new byte[units.Count];
            for (var i = 0; i < units.Count; i++)
                result[i] = (byte)units[i];
            return result;
        }

        static Il2CppReadOnlyUnits ToIl2Cpp(byte[] units)
        {
            var list = new Il2CppUnits(units.Length);
            foreach (var unit in units)
                list.Add((eSectionFamily)unit);
            return list.Cast<Il2CppReadOnlyUnits>();
        }

        public static HashSet<int> LocalHeld(LabPotionsPopulator populator)
        {
            var set = new HashSet<int>();
            AddUids(populator.HeldPotions(), set);
            AddUids(populator.ApronPotions(), set);
            return set;
        }

        static void AddUids(ReactiveList<PotionFacade> list, HashSet<int> into)
        {
            for (var i = 0; i < list.Count; i++)
                into.Add(list[i].Uid);
        }

        /// <summary>
        /// Every unit of liquid in the lab (bottles, vats, separator loop), per family. Liquid is only ever moved,
        /// never made or destroyed, so in one lab these totals must stay the same: a change means something was lost.
        /// </summary>
        public static string LiquidReport(LabPotionsPopulator populator)
        {
            var counts = new SortedDictionary<int, int>();
            void Add(Il2CppUnits units)
            {
                for (var i = 0; i < units.Count; i++)
                {
                    var family = (int)units[i];
                    counts[family] = counts.TryGetValue(family, out var n) ? n + 1 : 1;
                }
            }
            foreach (var entry in populator._potionsByUid)
                Add(entry.Value.Contents._units);
            foreach (var vat in Vats())
                Add(vat.Contents._units);
            foreach (var separator in Separators())
            {
                var units = new Il2CppUnits();
                separator.CopyLoopUnits(units);
                Add(units);
            }
            return $"bottles={populator._potionsByUid.Count} total={counts.Values.Sum()} | " + string.Join(" ", counts.Select(c => $"{c.Key}:{c.Value}"));
        }

        static readonly HashSet<int> ReportedDoubleClaims = new();

        /// <summary>
        /// Potion uid → slot uid, from the slots' side. The world digest and the states we send must use the same
        /// rule: with the game's own TryGetSlotOf a potion claimed by two slots could read differently in each.
        /// </summary>
        public static Dictionary<int, int> SlotMap(LabPotionsPopulator populator, bool report = false)
        {
            var map = new Dictionary<int, int>();
            foreach (var entry in populator._slotsByUid)
            {
                var occupant = entry.Value.Potion;
                if (occupant == null)
                    continue;
                var uid = occupant.Uid;
                if (report && map.TryGetValue(uid, out var first) && ReportedDoubleClaims.Add(uid))
                    CoopMod.Log.Warning($"#{uid} is claimed by two slots (#{first} and #{entry.Key})");
                map[uid] = entry.Key;
            }
            return map;
        }

        /// <summary>State of one vessel as seen by this peer.</summary>
        /// <param name="slotOf">From <see cref="SlotMap"/> when reading many vessels (and to agree with the digest).</param>
        public static NetPotion Read(LabPotionsPopulator populator, PotionFacade potion, HashSet<int> localHeld, bool withPose = true,
            Dictionary<int, int> slotOf = null)
        {
            var uid = potion.Uid;
            var state = new NetPotion { Uid = uid, Units = ReadUnits(potion.Contents._units) };
            slotOf ??= SlotMap(populator);

            if (localHeld.Contains(uid))
                state.Location = HeldBySelf;
            else if (RemoteHeld.Contains(uid))
                state.Location = HeldByOther;
            else if (slotOf.TryGetValue(uid, out var slotUid))
            {
                state.Location = NetLocation.Slot;
                state.Place = slotUid;
            }
            else if (potion.IsHanging && potion.HangingHolder != null)
            {
                state.Location = NetLocation.Holder;
                state.Place = potion.HangingHolder.Index;
            }
            else
            {
                state.Location = NetLocation.Floor;
                if (withPose)
                {
                    var t = potion.transform;
                    var p = t.position;
                    var r = t.rotation;
                    state.Pos = new[] { p.x, p.y, p.z };
                    state.Rot = new[] { r.x, r.y, r.z, r.w };
                }
            }
            return state;
        }

        public static NetVat ReadVat(VatFacade vat) => new() { Uid = vat.Uid, Units = ReadUnits(vat.Contents._units) };

        // Searching the scene (240k+ objects) is slow, so vats and holders are looked up once per level.
        static VatFacade[] _vats;
        static PourOutHolderFacade[] _holders;

        public static VatFacade[] Vats()
        {
            if (_vats == null || _vats.Any(v => v == null))
                _vats = Object.FindObjectsByType<VatFacade>(FindObjectsSortMode.None).ToArray();
            return _vats;
        }

        static EssenceSeparatorFacade[] _separators;

        /// <summary>The Essence Separator(s): a shared liquid fixture like the vats (its loop holds units of essence).</summary>
        public static EssenceSeparatorFacade[] Separators()
        {
            if (_separators == null || _separators.Any(s => s == null))
                _separators = Object.FindObjectsByType<EssenceSeparatorFacade>(FindObjectsSortMode.None).ToArray();
            return _separators;
        }

        /// <summary>A separator travels as a "vat" (its own uid, its loop's units).</summary>
        public static NetVat ReadSeparator(EssenceSeparatorFacade separator)
        {
            var units = new Il2CppUnits();
            separator.CopyLoopUnits(units);
            return new NetVat { Uid = separator.Uid, Units = ReadUnits(units) };
        }

        static PourOutHolderFacade FindHolder(int index)
        {
            if (_holders == null || _holders.Any(h => h == null))
                _holders = Object.FindObjectsByType<PourOutHolderFacade>(FindObjectsSortMode.None).ToArray();
            return _holders.FirstOrDefault(h => h.Index == index);
        }

        /// <summary>Order-independent hash of what matters for sync (floor poses excluded: they are cosmetic).</summary>
        public static uint Hash(NetPotion s)
        {
            var h = HashHead(s.Uid, s.Location, s.Place);
            foreach (var u in s.Units)
                h = Mix(h, u);
            return h;
        }

        /// <summary>Same hash as <see cref="Hash(NetPotion)"/>, straight from the game objects (used by the periodic scan).</summary>
        public static uint Hash(int uid, NetLocation location, int place, Il2CppUnits units)
        {
            var h = HashHead(uid, location, place);
            var count = units.Count;
            for (var i = 0; i < count; i++)
                h = Mix(h, (int)units[i]);
            return h;
        }

        static uint HashHead(int uid, NetLocation location, int place)
        {
            var h = Mix(2166136261u, uid);
            h = Mix(h, (int)location);
            return Mix(h, location is NetLocation.Slot or NetLocation.Holder ? place : 0);
        }

        static uint Mix(uint h, int v)
        {
            unchecked { return (h ^ (uint)v) * 16777619u; }
        }

        public static uint Hash(NetVat v)
        {
            unchecked
            {
                var h = 2166136261u;
                h = (h ^ (uint)v.Uid) * 16777619u;
                foreach (var u in v.Units)
                    h = (h ^ u) * 16777619u;
                return h;
            }
        }

        // ---------- applying ----------

        /// <summary>Runs an action flagged as remote application, so our persistence hooks don't echo it back.</summary>
        public static void AsApplying(Action action)
        {
            var was = Applying;
            Applying = true;
            try { action(); }
            finally { Applying = was; }
        }

        /// <summary>Brings the given vessels to the given states. Returns how many changed.</summary>
        /// <param name="animate">Live changes glide into place; bulk resyncs snap.</param>
        public static int Apply(IReadOnlyList<NetPotion> states, IReadOnlyList<NetVat> vats, bool animate = true)
        {
            var populator = GameRefs.Populator;
            var persistence = GameRefs.Persistence;
            if (populator == null || persistence == null)
                return 0;

            var changed = 0;
            AsApplying(() =>
            {
                var localHeld = LocalHeld(populator);
                var slotOf = SlotMap(populator);
                var work = new List<(PotionFacade potion, NetPotion target, NetPotion current)>();

                foreach (var target in states)
                {
                    if (!populator.TryGetPotion(target.Uid, out var potion) || potion == null)
                        continue;
                    if (localHeld.Contains(target.Uid))
                    {
                        if (target.Location == HeldBySelf)
                            continue;
                        // Both of us hold it (grabbed at the same instant, or the client's own save had it in hand).
                        // The host has the final word: the host keeps it and repeats its state; the client lets go.
                        if (Coop.IsHost)
                        {
                            Coop.Reassert(target.Uid);
                            continue;
                        }
                        ReleaseFromLocalHand(populator, potion);
                        localHeld.Remove(target.Uid);
                    }
                    // A newer state overrides an animation still on its way.
                    FinishFlight(target.Uid);
                    var current = Read(populator, potion, localHeld, slotOf: slotOf);
                    work.Add((potion, target, current));
                }

                // Pass 1: free every slot and holder that a vessel is leaving, so placements never find a stale
                // occupant (and a holder never keeps claiming a bottle that has gone elsewhere).
                foreach (var (potion, target, current) in work)
                {
                    if (current.Location == NetLocation.Slot && !(target.Location == NetLocation.Slot && target.Place == current.Place)
                        && populator.TryGetSlotOf(potion, out var oldSlot) && oldSlot != null)
                        oldSlot.ClearPotion(true);
                    if (current.Location == NetLocation.Holder && !(target.Location == NetLocation.Holder && target.Place == current.Place))
                        Unhang(potion);
                }

                // Pass 2: liquids first (so the placement record below saves them), then placement.
                foreach (var (potion, target, current) in work)
                {
                    var moved = false;
                    if (!target.Units.SequenceEqual(current.Units))
                    {
                        potion.SetLiquidContents(ToIl2Cpp(target.Units));
                        // A pour can finish a potion: let the cork and finished look follow the new contents.
                        if (target.Location == HeldByOther)
                            SealIfFinished(potion);
                        else
                            ApplyCork(potion, animate);
                        moved = true;
                    }

                    if (!SamePlace(target, current))
                    {
                        Place(populator, persistence, potion, target, animate);
                        moved = true;
                    }
                    else
                    {
                        // The resting pose of a bottle we let fall: it landed where it should, so let it rest.
                        if (target.Location == NetLocation.Floor && !target.Falling && Falling.Remove(target.Uid))
                            potion.Freeze();
                        if (moved && target.Location == NetLocation.Slot && populator.TryGetSlotOf(potion, out var slot))
                            persistence.RecordShelved(potion, slot);
                        else if (moved && target.Location == NetLocation.Floor)
                            persistence.RecordDropped(potion);
                    }

                    if (moved)
                        changed++;
                }

                if (vats != null && vats.Count > 0)
                {
                    foreach (var vat in Vats())
                    {
                        var target = vats.FirstOrDefault(v => v.Uid == vat.Uid);
                        if (target == null || target.Units.SequenceEqual(ReadUnits(vat.Contents._units)))
                            continue;
                        vat.SetLiquidContents(ToIl2Cpp(target.Units));
                        persistence.RecordVat(vat);
                        changed++;
                    }
                    foreach (var separator in Separators())
                    {
                        var target = vats.FirstOrDefault(v => v.Uid == separator.Uid);
                        if (target == null || target.Units.SequenceEqual(ReadSeparator(separator).Units))
                            continue;
                        try
                        {
                            separator.SetLoopUnits(ToIl2Cpp(target.Units));
                            persistence.RecordSeparator(separator);
                            changed++;
                        }
                        catch (Exception e) { CoopMod.Log.Warning($"separator #{separator.Uid}: {e.Message}"); }
                    }
                }
            });
            return changed;
        }

        static bool SamePlace(NetPotion a, NetPotion b)
        {
            if (a.Location != b.Location)
                return false;
            return a.Location switch
            {
                NetLocation.Slot or NetLocation.Holder => a.Place == b.Place,
                NetLocation.Floor => a.Pos == null || b.Pos == null ||
                                     ((ToVector(a.Pos) - ToVector(b.Pos)).sqrMagnitude < 0.0004f &&
                                      (a.Rot == null || b.Rot == null || Quaternion.Angle(ToRotation(a.Rot), ToRotation(b.Rot)) < 5f)),
                _ => true,
            };
        }

        /// <summary>Takes a bottle off its holder (the separator's hooks), as picking it up there does.</summary>
        static void Unhang(PotionFacade potion)
        {
            try
            {
                var holder = potion.HangingHolder;
                if (holder != null && holder._vessel != null && holder._vessel.Pointer == potion.Pointer)
                    holder.Release();
                if (potion.IsHanging)
                    potion.EndHang();
            }
            catch (Exception e) { CoopMod.Log.Warning($"taking #{potion.Uid} off its holder: {e.Message}"); }
            if (potion.IsHanging)
                CoopMod.Log.Warning($"#{potion.Uid} still hangs on its holder");
        }

        // ---- corks: held bottles are open, resting ones corked, a finished one sealed with a glow ----

        static void ApplyCork(PotionFacade potion, bool animate)
        {
            try { potion.Corker()?.ApplyCurrentState(!animate); }
            catch (Exception e) { CoopMod.Log.Warning($"cork state for #{potion.Uid}: {e.Message}"); }
        }

        /// <summary>The other player picked it up: the cork pops, as in their hands.</summary>
        static void Uncork(PotionFacade potion)
        {
            try
            {
                var corker = potion.Corker();
                if (corker != null && corker.IsCorked.Value && !potion.IsFinished.Value)
                    corker.SetCorked(false, false);
            }
            catch (Exception e) { CoopMod.Log.Warning($"uncorking #{potion.Uid}: {e.Message}"); }
        }

        /// <summary>A carried potion that just became finished gets its cork seated, which plays the finish glow.</summary>
        static void SealIfFinished(PotionFacade potion)
        {
            try
            {
                var corker = potion.Corker();
                if (corker != null && potion.IsFinished.Value && !corker.IsCorked.Value)
                    corker.SetCorked(true, false);
            }
            catch (Exception e) { CoopMod.Log.Warning($"sealing #{potion.Uid}: {e.Message}"); }
        }

        static Vector3 ToVector(float[] p) => new(p[0], p[1], p[2]);

        static Quaternion ToRotation(float[] r) => new(r[0], r[1], r[2], r[3]);

        static void Place(LabPotionsPopulator populator, Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence persistence,
            PotionFacade potion, NetPotion target, bool animate)
        {
            var uid = target.Uid;
            var fromHand = RemoteHeld.Contains(uid);

            if (target.Location == HeldByOther)
            {
                Carry(potion);
                Diagnose(potion, "carried");
                // Host: keep the save sane if the game closes now.
                if (Coop.IsHost)
                    persistence.RecordDropped(potion);
                return;
            }

            // Leaving the other player's hands: from here on the vessel is ours to place.
            RemoteHeld.Remove(uid);
            if (CarryOrder.Remove(uid))
                BlendCarried();
            PourTilts.Remove(uid);

            switch (target.Location)
            {
                case NetLocation.Slot:
                    if (!populator.TryGetSlot(target.Place, out var slot) || slot == null)
                    {
                        CoopMod.Log.Warning($"unknown slot #{target.Place} for #{uid}");
                        RestoreColliders(potion);
                        return;
                    }
                    if (slot.Potion != null && slot.Potion.Uid != uid)
                        slot.ClearPotion(true);
                    void Shelve()
                    {
                        RestoreColliders(potion);
                        slot.PlacePotion(potion, true);
                        if (potion.transform.parent != slot.PotionMount)
                            potion.BeginFlyToSlot(slot.PotionMount, true);
                        ApplyCork(potion, animate);
                        persistence.RecordShelved(potion, slot);
                        Diagnose(potion, $"shelved in #{slot.Uid}");
                    }
                    if (fromHand)
                        Fly(potion, slot.PotionMount.position, slot.PotionMount.rotation, Shelve);
                    else
                        Shelve();
                    break;

                case NetLocation.Holder:
                    var holder = FindHolder(target.Place);
                    RestoreColliders(potion);
                    if (holder == null)
                    {
                        CoopMod.Log.Warning($"unknown holder {target.Place} for #{uid}");
                        return;
                    }
                    holder.Hang(potion, true);
                    ApplyCork(potion, animate);
                    persistence.RecordHung(potion, holder);
                    break;

                case NetLocation.Floor:
                    var pos = target.Pos != null ? ToVector(target.Pos) : potion.transform.position;
                    var rot = target.Rot != null ? ToRotation(target.Rot) : potion.transform.rotation;
                    if (target.Falling && target.Toss)
                    {
                        // Thrown (R): the game's own throw, flipping through the air to where it was aimed. The aim
                        // pose is upright, but a thrown bottle almost always ends up lying: land it on its side,
                        // the real resting pose follows as a small correction.
                        var thrownLanding = LandingBelow(potion, pos + Vector3.up * 0.3f);
                        var thrownLying = Quaternion.Euler(0f, rot.eulerAngles.y, 90f);
                        void LandThrown()
                        {
                            RestoreColliders(potion);
                            DropAt(potion, thrownLanding, thrownLying);
                            ApplyCork(potion, animate);
                            Falling.Add(uid);
                            persistence.RecordDropped(potion);
                        }
                        Throw(potion, thrownLanding, thrownLying, LandThrown);
                        break;
                    }
                    if (target.Falling)
                    {
                        // Just let go on the other side; its rest pose follows in a moment. Our physics would freeze a
                        // slow bottle mid-air, so we animate the fall ourselves onto whatever lies below.
                        var landing = LandingBelow(potion, pos);
                        var lying = Quaternion.Euler(0f, rot.eulerAngles.y, 90f);
                        void LandFallen()
                        {
                            RestoreColliders(potion);
                            DropAt(potion, landing, lying);
                            ApplyCork(potion, animate);
                            Falling.Add(uid);
                            persistence.RecordDropped(potion);
                        }
                        if (fromHand)
                            Fly(potion, pos, rot, () => Fly(potion, landing, lying, LandFallen, FlightKind.Fall));
                        else
                            Fly(potion, landing, lying, LandFallen, FlightKind.Fall);
                        break;
                    }

                    void Land()
                    {
                        RestoreColliders(potion);
                        DropAt(potion, pos, rot);
                        ApplyCork(potion, animate);
                        Falling.Remove(uid);
                        persistence.RecordDropped(potion);
                    }
                    var nearby = (potion.transform.position - pos).sqrMagnitude < 4f;
                    if (fromHand)
                        Fly(potion, pos, rot, Land);
                    else if (animate && nearby && Falling.Contains(uid))
                        // Where a dropped bottle really came to rest: it rolls/tips over into that pose.
                        Fly(potion, pos, rot, Land, FlightKind.Topple);
                    else if (animate && nearby)
                        Fly(potion, pos, rot, Land);
                    else
                        Land();
                    break;

                default:
                    // HeldBySelf for a vessel not in our hands: the other side thinks we hold it. Drop it at our feet.
                    RestoreColliders(potion);
                    var hero = GameRefs.Hero;
                    var feet = hero != null ? hero.Position + Vector3.up * 0.3f : potion.transform.position;
                    DropAt(potion, feet, potion.transform.rotation);
                    ApplyCork(potion, animate);
                    persistence.RecordDropped(potion);
                    break;
            }
        }

        // ---- vessels in the other player's hands ----
        // They stay visible and float in front of the ghost (as if carried), frozen and with colliders off,
        // so nobody can aim at them and nothing in the game thinks they fell out of the world.

        static readonly Dictionary<int, List<Collider>> CollidersOff = new();
        static readonly List<int> CarryOrder = new();
        /// <summary>Bottles we let fall with physics because they were still falling on the other side.</summary>
        static readonly HashSet<int> Falling = new();

        /// <summary>
        /// Client only: the host says a bottle in our hand or apron is somewhere else. Let go of it the way the game
        /// would (out of the hand list and the first-person hand view); the caller then applies the host's state.
        /// </summary>
        static void ReleaseFromLocalHand(LabPotionsPopulator populator, PotionFacade potion)
        {
            var held = populator.HeldPotions();
            var apron = populator.ApronPotions();
            var fromApron = !held.Contains(potion) && apron.Contains(potion);
            var hero = GameRefs.Hero;
            var picker = hero != null ? hero._picker : null;

            if (fromApron)
                apron.Remove(potion);
            else
                held.Remove(potion);
            try
            {
                var hand = picker != null ? picker._hand : null;
                if (hand != null && hero != null)
                    hand.MoveOutOfHand(potion, hero.Position + Vector3.up + hero.GetCameraForward() * 0.5f);
                if (picker != null)
                {
                    if (fromApron)
                        picker.ReseatApronRow(true);
                    else
                        picker.ReseatRow(true);
                }
                var carrier = potion._carrier;
                if (carrier != null)
                    carrier.MarkIdle();
            }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"letting go of #{potion.Uid}: {e.Message}");
            }
            CoopMod.Log.Msg($"released #{potion.Uid} from my {(fromApron ? "apron" : "hand")}: the host has it elsewhere");
        }

        static void Carry(PotionFacade potion)
        {
            var uid = potion.Uid;
            Falling.Remove(uid);
            potion.Freeze();
            var pickedUp = RemoteHeld.Add(uid);
            if (pickedUp)
            {
                // Out of a slot or holder, into the ghost's hands (not a child of the shelf any more).
                var root = GameRefs.DropRoot;
                if (root != null)
                    potion.transform.SetParent(root, true);
                Uncork(potion);
            }
            DisableColliders(potion);
            if (pickedUp)
            {
                CarryOrder.Add(uid);
                BlendCarried();
            }
        }

        /// <summary>
        /// Carried bottles: no collisions (nobody can bump or aim at them) and no shelf wobble. The cork keeps its
        /// collider: it may be flying off just now.
        /// </summary>
        static void DisableColliders(PotionFacade potion)
        {
            if (CollidersOff.ContainsKey(potion.Uid))
                return;
            var switched = new List<Collider>();
            foreach (var collider in potion.GetComponentsInChildren<Collider>(false))
                if (collider.enabled && collider.GetComponentInParent<Il2CppCodeBase.Components.Corks.CorkFacade>() == null)
                {
                    collider.enabled = false;
                    switched.Add(collider);
                }
            CollidersOff[potion.Uid] = switched;
            var tilter = potion.GetComponent<PotionShelveTilter>();
            if (tilter != null && tilter.enabled)
            {
                tilter.ResetToRest();
                tilter.enabled = false;
                TiltersOff[potion.Uid] = tilter;
            }
        }

        static void RestoreColliders(PotionFacade potion)
        {
            if (TiltersOff.Remove(potion.Uid, out var tilter) && tilter != null)
                tilter.enabled = true;
            if (!CollidersOff.Remove(potion.Uid, out var switched))
                return;
            foreach (var collider in switched)
                if (collider != null)
                    collider.enabled = true;
        }

        static readonly Dictionary<int, PotionShelveTilter> TiltersOff = new();

        /// <summary>Puts a vessel on the floor at an exact resting pose.</summary>
        static void DropAt(PotionFacade potion, Vector3 position, Quaternion rotation)
        {
            var root = GameRefs.DropRoot;
            var path = "fly";
            if (root != null)
            {
                // The optional Nullable<> parameters must be real (empty) boxes: the interop wrapper rejects null.
                try { potion.BeginFlyToDrop(root, position, rotation, true, NoDuration, NoEase); }
                catch (Exception e) { path = "fly failed (" + e.GetType().Name + ")"; }
            }

            // The game's drop only works for bottles coming out of a hand; anything else we place ourselves.
            if ((potion.transform.position - position).sqrMagnitude > 0.01f)
            {
                path += " -> teleport";
                var transform = potion.transform;
                if (root != null)
                    transform.SetParent(root, true);
                transform.SetPositionAndRotation(position, rotation);
                var carrier = potion._carrier;
                if (carrier != null)
                    carrier.MarkIdle();
                // Resting floor bottles are frozen until something disturbs them.
                potion.Freeze();
            }
            Diagnose(potion, path);
        }

        /// <summary>
        /// The first thing below a point a bottle can rest on (floor, table, a pile of bottles; not a player),
        /// plus half a bottle's thickness.
        /// </summary>
        static Vector3 LandingBelow(PotionFacade potion, Vector3 from)
        {
            var mask = ~0;
            try
            {
                var body = potion._body;
                if (body != null && body.SupportMask != 0)
                    mask = body.SupportMask;
            }
            catch (Exception) { }
            if (Physics.Raycast(from + Vector3.up * 0.05f, Vector3.down, out var hit, 8f, mask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.04f;
            return from;
        }

        public static VatFacade FindVat(int uid) => Vats().FirstOrDefault(v => v != null && v.Uid == uid);

        // ---- motion: carried bottles follow the ghost, placed bottles fly out of its hands ----

        sealed class Flight
        {
            public PotionFacade Potion;
            public int Uid;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float Start, Duration;
            public FlightKind Kind;
            public Action Done;
            // Throw: launch velocity, and the axis and number of its flips
            public Vector3 Velocity;
            public Vector3 SpinAxis;
            public int Flips;
        }

        enum FlightKind
        {
            /// <summary>Ease in and out with a small hop (hand to shelf, short corrections).</summary>
            Glide,
            /// <summary>Accelerate straight down like a dropped object.</summary>
            Fall,
            /// <summary>A rough thrown arc, when the game's own throw cannot be solved.</summary>
            Toss,
            /// <summary>The game's ballistic throw: launch velocity under gravity, flipping end over end.</summary>
            Throw,
            /// <summary>A landed bottle rolling/tipping into the pose it really came to rest in.</summary>
            Topple,
        }

        static readonly List<Flight> Flights = new();

        static Flight Fly(PotionFacade potion, Vector3 toPos, Quaternion toRot, Action done, FlightKind kind = FlightKind.Glide)
        {
            var t = potion.transform;
            var distance = Vector3.Distance(t.position, toPos);
            var duration = kind switch
            {
                // A real fall takes sqrt(2h/g).
                FlightKind.Fall => Mathf.Clamp(Mathf.Sqrt(2f * Mathf.Max(0.01f, t.position.y - toPos.y) / 9.81f), 0.12f, 0.7f),
                FlightKind.Toss => Mathf.Clamp(0.3f + distance * 0.09f, 0.35f, 0.9f),
                FlightKind.Topple => 0.4f,
                _ => Mathf.Clamp(0.18f + distance * 0.12f, 0.2f, 0.6f),
            };
            var flight = new Flight
            {
                Potion = potion,
                Uid = potion.Uid,
                FromPos = t.position,
                FromRot = t.rotation,
                ToPos = toPos,
                ToRot = toRot,
                Start = Time.realtimeSinceStartup,
                Duration = duration,
                Kind = kind,
                Done = done,
            };
            Flights.Add(flight);
            return flight;
        }

        static int _throwsLogged;

        /// <summary>A thrown bottle: the game's own launch solution from the ghost's hands to the aim point, with its flips.</summary>
        static void Throw(PotionFacade potion, Vector3 to, Quaternion toRot, Action done)
        {
            var from = potion.transform.position;
            var velocity = Vector3.zero;
            var time = 0f;
            var solved = false;
            try
            {
                if (to.y > from.y + 0.05f && potion.TrySolveUpwardThrow(from, to, out velocity, out time))
                    solved = time is > 0.05f and < 3f;
                if (!solved && potion.TrySolveBallisticDrop(from, to, out velocity))
                {
                    var speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
                    var distance = new Vector3(to.x - from.x, 0f, to.z - from.z).magnitude;
                    time = speed > 0.01f ? distance / speed : 0f;
                    solved = time is > 0.05f and < 3f;
                }
            }
            catch (Exception e)
            {
                CoopMod.Log.Warning($"throw of #{potion.Uid}: {e.Message}");
            }
            if (!solved)
            {
                Fly(potion, to, toRot, done, FlightKind.Toss);
                return;
            }
            var flips = 1;
            try { flips = Mathf.Max(1, potion.PotionsConfig.DropFlips); }
            catch (Exception) { }
            var flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            var flight = Fly(potion, to, toRot, done, FlightKind.Throw);
            flight.Duration = time;
            flight.Velocity = velocity;
            flight.SpinAxis = flat.sqrMagnitude > 0.0001f ? Vector3.Cross(Vector3.up, flat.normalized) : potion.transform.right;
            flight.Flips = flips;
            if (_throwsLogged++ < 3)
                CoopMod.Log.Msg($"[diag] throw #{potion.Uid}: {time:F2}s, {flips} flip(s), v={velocity}");
        }

        static void FinishFlight(int uid)
        {
            // Completing a flight may start the next one for the same bottle (out of the hands, then down).
            for (var guard = 0; guard < 8; guard++)
            {
                var i = Flights.FindIndex(f => f.Uid == uid);
                if (i < 0)
                    return;
                var flight = Flights[i];
                Flights.RemoveAt(i);
                Complete(flight);
            }
        }

        static void Complete(Flight flight)
        {
            if (flight.Potion == null)
                return;
            flight.Potion.transform.SetPositionAndRotation(flight.ToPos, flight.ToRot);
            AsApplying(() =>
            {
                try { flight.Done(); }
                catch (Exception e) { CoopMod.Log.Error($"placing #{flight.Uid} failed: {e.Message}"); }
            });
        }

        // Carried bottles sit exactly in the ghost's hands; when the set changes they ease into their new places.
        const float CarryBlendTime = 0.25f;
        static readonly Dictionary<int, (Vector3 pos, Quaternion rot, float start)> CarryBlends = new();

        static void BlendCarried()
        {
            var populator = GameRefs.Populator;
            if (populator == null)
                return;
            var now = Time.realtimeSinceStartup;
            foreach (var uid in CarryOrder)
                if (populator.TryGetPotion(uid, out var potion) && potion != null)
                    CarryBlends[uid] = (potion.transform.position, potion.transform.rotation, now);
        }

        // A pour of the other player: their bottle tips over the target (or is held under a vat's tap)
        // for the length of the pour.
        static readonly Dictionary<int, (Vector3 target, float start, float duration, bool underTap)> PourTilts = new();

        public static void TipOver(int uid, Vector3 target, float duration)
        {
            if (RemoteHeld.Contains(uid))
                PourTilts[uid] = (target, Time.realtimeSinceStartup, duration, false);
        }

        public static void HoldUnder(int uid, Vector3 spout, float duration)
        {
            if (RemoteHeld.Contains(uid))
                PourTilts[uid] = (spout, Time.realtimeSinceStartup, duration, true);
        }

        static (Vector3 pos, Quaternion rot) PourPose(Vector3 hold, Vector3 target, bool underTap)
        {
            var flat = new Vector3(target.x - hold.x, 0f, target.z - hold.z);
            var dir = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
            // Upright, its opening right below the tap.
            if (underTap)
                return (target - Vector3.up * 0.2f, Quaternion.LookRotation(dir));
            // Neck over the target's opening, bottle leaning past horizontal.
            var pos = target - dir * 0.08f + Vector3.up * 0.22f;
            var rot = Quaternion.AngleAxis(115f, Vector3.Cross(Vector3.up, dir)) * Quaternion.LookRotation(dir);
            return (pos, rot);
        }

        /// <summary>Every frame: move carried bottles with the ghost and advance flights.</summary>
        public static void TickMotion(Ghost ghost, float dt)
        {
            var now = Time.realtimeSinceStartup;
            if (ghost != null && ghost.Alive && CarryOrder.Count > 0)
            {
                var populator = GameRefs.Populator;
                for (var i = 0; i < CarryOrder.Count; i++)
                {
                    var uid = CarryOrder[i];
                    if (!populator.TryGetPotion(uid, out var potion) || potion == null)
                        continue;
                    var (pos, rot) = ghost.CarryPose(i, CarryOrder.Count);
                    if (PourTilts.TryGetValue(uid, out var tilt))
                    {
                        var elapsed = now - tilt.start;
                        var total = tilt.duration + 0.25f;
                        if (elapsed >= total)
                            PourTilts.Remove(uid);
                        else
                        {
                            var w = Mathf.Clamp01(Mathf.Min(elapsed / 0.2f, (total - elapsed) / 0.25f));
                            w = w * w * (3f - 2f * w);
                            var pour = PourPose(pos, tilt.target, tilt.underTap);
                            pos = Vector3.Lerp(pos, pour.pos, w);
                            rot = Quaternion.Slerp(rot, pour.rot, w);
                        }
                    }
                    if (CarryBlends.TryGetValue(uid, out var blend))
                    {
                        var b = (now - blend.start) / CarryBlendTime;
                        if (b >= 1f)
                            CarryBlends.Remove(uid);
                        else
                        {
                            b = b * b * (3f - 2f * b);
                            pos = Vector3.Lerp(blend.pos, pos, b);
                            rot = Quaternion.Slerp(blend.rot, rot, b);
                        }
                    }
                    potion.transform.SetPositionAndRotation(pos, rot);
                }
            }

            for (var i = Flights.Count - 1; i >= 0; i--)
            {
                var flight = Flights[i];
                if (flight.Potion == null)
                {
                    Flights.RemoveAt(i);
                    continue;
                }
                var p = Mathf.Clamp01((now - flight.Start) / flight.Duration);
                if (p >= 1f)
                {
                    Flights.RemoveAt(i);
                    Complete(flight);
                    continue;
                }

                Vector3 position;
                Quaternion rotation;
                if (flight.Kind == FlightKind.Throw)
                {
                    var t = p * flight.Duration;
                    position = flight.FromPos + flight.Velocity * t + 0.5f * Physics.gravity * (t * t);
                    // The solution lands a hair off the aim point: absorb that along the way.
                    var end = flight.FromPos + flight.Velocity * flight.Duration + 0.5f * Physics.gravity * (flight.Duration * flight.Duration);
                    position += (flight.ToPos - end) * p;
                    rotation = Quaternion.AngleAxis(360f * flight.Flips * p, flight.SpinAxis) * Quaternion.Slerp(flight.FromRot, flight.ToRot, p);
                }
                else
                {
                    var distance = Vector3.Distance(flight.FromPos, flight.ToPos);
                    var (eased, arc) = flight.Kind switch
                    {
                        FlightKind.Fall or FlightKind.Topple => (p * p, 0f),
                        FlightKind.Toss => (p, 4f * p * (1f - p) * Mathf.Clamp(distance * 0.3f, 0.2f, 1.2f)),
                        _ => (p * p * (3f - 2f * p), Mathf.Sin(p * Mathf.PI) * Mathf.Min(0.3f, distance * 0.25f)),
                    };
                    position = Vector3.Lerp(flight.FromPos, flight.ToPos, eased) + Vector3.up * arc;
                    rotation = Quaternion.Slerp(flight.FromRot, flight.ToRot, eased);
                }
                flight.Potion.transform.SetPositionAndRotation(position, rotation);
            }
        }

        // ---- diagnostics: what really happened to the first applied vessels ----

        static int _diagnosed;
        static readonly List<(PotionFacade potion, float due, string label)> Checks = new();

        static void Diagnose(PotionFacade potion, string label)
        {
            if (_diagnosed >= 20 && !Settings.Verbose)
                return;
            _diagnosed++;
            CoopMod.Log.Msg($"[diag] #{potion.Uid} {label}: now {Describe(potion)}");
            Checks.Add((potion, Time.realtimeSinceStartup + 1f, label));
        }

        /// <summary>Level unloaded: every vessel object is gone, forget what we knew about them.</summary>
        public static void ResetLevel()
        {
            RemoteHeld.Clear();
            CarryOrder.Clear();
            CollidersOff.Clear();
            TiltersOff.Clear();
            Falling.Clear();
            Flights.Clear();
            CarryBlends.Clear();
            PourTilts.Clear();
            Checks.Clear();
            Pours.Forget();
            _vats = null;
            _holders = null;
            _separators = null;
        }

        public static void TickDiagnostics()
        {
            for (var i = Checks.Count - 1; i >= 0; i--)
            {
                var (potion, due, label) = Checks[i];
                if (Time.realtimeSinceStartup < due)
                    continue;
                Checks.RemoveAt(i);
                if (potion != null)
                    CoopMod.Log.Msg($"[diag] #{potion.Uid} {label}: 1s later {Describe(potion)}");
            }
        }

        static string Describe(PotionFacade potion)
        {
            var t = potion.transform;
            var parent = t.parent != null ? t.parent.name : "-";
            return $"pos={t.position} carry={potion.CarryState.Value} parent={parent} carried={RemoteHeld.Contains(potion.Uid)}";
        }

        /// <summary>The other player left: whatever they held falls from their hands to the floor and lies there.</summary>
        public static void DropRemoteHeld()
        {
            var populator = GameRefs.Populator;
            var persistence = GameRefs.Persistence;
            foreach (var uid in Flights.Select(f => f.Uid).Distinct().ToList())
                FinishFlight(uid);
            if (populator != null)
            {
                AsApplying(() =>
                {
                    foreach (var uid in CarryOrder.ToList())
                    {
                        if (!populator.TryGetPotion(uid, out var potion) || potion == null)
                            continue;
                        var landing = LandingBelow(potion, potion.transform.position);
                        var lying = Quaternion.Euler(0f, potion.transform.eulerAngles.y, 90f);
                        void Landed()
                        {
                            RestoreColliders(potion);
                            DropAt(potion, landing, lying);
                            ApplyCork(potion, true);
                            persistence?.RecordDropped(potion);
                        }
                        // Runs on even without a link (Coop keeps ticking the motion while bottles are in flight).
                        Fly(potion, landing, lying, Landed, FlightKind.Fall);
                    }
                });
            }
            RemoteHeld.Clear();
            CarryOrder.Clear();
            CarryBlends.Clear();
            PourTilts.Clear();
        }
    }
}
