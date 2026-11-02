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

        /// <summary>Set while applying remote state so our persistence hooks don't echo it back.</summary>
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

        /// <summary>State of one vessel as seen by this peer.</summary>
        public static NetPotion Read(LabPotionsPopulator populator, PotionFacade potion, HashSet<int> localHeld, bool withPose = true)
        {
            var uid = potion.Uid;
            var state = new NetPotion { Uid = uid, Units = ReadUnits(potion.Contents._units) };

            if (localHeld.Contains(uid))
                state.Location = HeldBySelf;
            else if (RemoteHeld.Contains(uid))
                state.Location = HeldByOther;
            else if (populator.TryGetSlotOf(potion, out var slot) && slot != null)
            {
                state.Location = NetLocation.Slot;
                state.Place = slot.Uid;
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

        /// <summary>Brings the given vessels to the given states. Returns how many changed.</summary>
        public static int Apply(IReadOnlyList<NetPotion> states, IReadOnlyList<NetVat> vats)
        {
            var populator = GameRefs.Populator;
            var persistence = GameRefs.Persistence;
            if (populator == null || persistence == null)
                return 0;

            Applying = true;
            var changed = 0;
            try
            {
                var localHeld = LocalHeld(populator);
                var work = new List<(PotionFacade potion, NetPotion target, NetPotion current)>();

                foreach (var target in states)
                {
                    if (!populator.TryGetPotion(target.Uid, out var potion) || potion == null)
                        continue;
                    if (localHeld.Contains(target.Uid))
                    {
                        // Both grabbed it at once. Our hands are ours; the digest check settles it later.
                        if (target.Location != HeldBySelf)
                            CoopMod.Log.Msg($"conflict: remote state for #{target.Uid} ({target.Location}) but it is in my hand");
                        continue;
                    }
                    var current = Read(populator, potion, localHeld);
                    work.Add((potion, target, current));
                }

                // Pass 1: free every slot that a vessel is leaving, so placements never find a stale occupant.
                foreach (var (potion, target, current) in work)
                {
                    if (current.Location == NetLocation.Slot && !(target.Location == NetLocation.Slot && target.Place == current.Place)
                        && populator.TryGetSlotOf(potion, out var oldSlot) && oldSlot != null)
                        oldSlot.ClearPotion(true);
                }

                // Pass 2: liquids first (so the placement record below saves them), then placement.
                foreach (var (potion, target, current) in work)
                {
                    var moved = false;
                    if (!target.Units.SequenceEqual(current.Units))
                    {
                        potion.SetLiquidContents(ToIl2Cpp(target.Units));
                        moved = true;
                    }

                    if (!SamePlace(target, current))
                    {
                        Place(populator, persistence, potion, target);
                        moved = true;
                    }
                    else if (moved && target.Location == NetLocation.Slot && populator.TryGetSlotOf(potion, out var slot))
                        persistence.RecordShelved(potion, slot);
                    else if (moved && target.Location == NetLocation.Floor)
                        persistence.RecordDropped(potion);

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
                }
            }
            finally
            {
                Applying = false;
            }
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
                                     (ToVector(a.Pos) - ToVector(b.Pos)).sqrMagnitude < 0.0004f,
                _ => true,
            };
        }

        static Vector3 ToVector(float[] p) => new(p[0], p[1], p[2]);

        static void Place(LabPotionsPopulator populator, Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence persistence,
            PotionFacade potion, NetPotion target)
        {
            var uid = target.Uid;

            if (target.Location == HeldByOther)
            {
                Park(potion);
                RemoteHeld.Add(uid);
                Diagnose(potion, "hidden");
                // Host: keep the save sane if the game closes now (the stray rescuer returns lost bottles on load).
                if (Coop.IsHost)
                    persistence.RecordDropped(potion);
                return;
            }

            Unpark(potion);

            switch (target.Location)
            {
                case NetLocation.Slot:
                    if (!populator.TryGetSlot(target.Place, out var slot) || slot == null)
                    {
                        CoopMod.Log.Warning($"unknown slot #{target.Place} for #{uid}");
                        return;
                    }
                    if (slot.Potion != null && slot.Potion.Uid != uid)
                        slot.ClearPotion(true);
                    slot.PlacePotion(potion, true);
                    if (potion.transform.parent != slot.PotionMount)
                        potion.BeginFlyToSlot(slot.PotionMount, true);
                    persistence.RecordShelved(potion, slot);
                    Diagnose(potion, $"shelved in #{slot.Uid}");
                    break;

                case NetLocation.Holder:
                    var holder = FindHolder(target.Place);
                    if (holder == null)
                    {
                        CoopMod.Log.Warning($"unknown holder {target.Place} for #{uid}");
                        return;
                    }
                    holder.Hang(potion, true);
                    persistence.RecordHung(potion, holder);
                    break;

                case NetLocation.Floor:
                    var pos = target.Pos != null ? ToVector(target.Pos) : potion.transform.position;
                    var rot = target.Rot != null ? new Quaternion(target.Rot[0], target.Rot[1], target.Rot[2], target.Rot[3]) : potion.transform.rotation;
                    DropAt(potion, pos, rot);
                    persistence.RecordDropped(potion);
                    break;

                default:
                    // HeldBySelf for a vessel not in our hands: the other side thinks we hold it. Drop it at our feet.
                    var hero = GameRefs.Hero;
                    var feet = hero != null ? hero.Position + Vector3.up * 0.3f : potion.transform.position;
                    DropAt(potion, feet, potion.transform.rotation);
                    persistence.RecordDropped(potion);
                    break;
            }
        }

        // ---- hiding remote-held vessels ----
        // A vessel in the other player's hands stays where it was, frozen, with its renderers and colliders off:
        // invisible, impossible to aim at, and nothing in the game thinks it fell out of the world.

        static readonly Dictionary<int, List<Component>> Hidden = new();

        static void Park(PotionFacade potion)
        {
            var uid = potion.Uid;
            if (Hidden.ContainsKey(uid))
                return;
            potion.Freeze();
            var switched = new List<Component>();
            foreach (var renderer in potion.GetComponentsInChildren<Renderer>(false))
                if (renderer.enabled)
                {
                    renderer.enabled = false;
                    switched.Add(renderer);
                }
            foreach (var collider in potion.GetComponentsInChildren<Collider>(false))
                if (collider.enabled)
                {
                    collider.enabled = false;
                    switched.Add(collider);
                }
            Hidden[uid] = switched;
        }

        static void Unpark(PotionFacade potion)
        {
            var uid = potion.Uid;
            RemoteHeld.Remove(uid);
            if (!Hidden.Remove(uid, out var switched))
                return;
            foreach (var component in switched)
            {
                if (component == null)
                    continue;
                var renderer = component.TryCast<Renderer>();
                if (renderer != null)
                    renderer.enabled = true;
                var collider = component.TryCast<Collider>();
                if (collider != null)
                    collider.enabled = true;
            }
        }

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
            Hidden.Clear();
            Checks.Clear();
            _vats = null;
            _holders = null;
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
            return $"pos={t.position} carry={potion.CarryState.Value} parent={parent} hidden={Hidden.ContainsKey(potion.Uid)}";
        }

        /// <summary>The other player left: put whatever they held down where their ghost stood.</summary>
        public static void DropRemoteHeld(Vector3 at)
        {
            var populator = GameRefs.Populator;
            if (populator == null || RemoteHeld.Count == 0)
                return;
            var states = RemoteHeld.Select((uid, i) =>
            {
                populator.TryGetPotion(uid, out var potion);
                var spot = at + new Vector3((i % 3) * 0.15f, 0.3f, (i / 3) * 0.15f);
                return new NetPotion
                {
                    Uid = uid,
                    Location = NetLocation.Floor,
                    Pos = new[] { spot.x, spot.y, spot.z },
                    Rot = new[] { 0f, 0f, 0f, 1f },
                    Units = potion != null ? ReadUnits(potion.Contents._units) : new byte[0],
                };
            }).ToList();
            Apply(states, null);
            RemoteHeld.Clear();
        }
    }
}
