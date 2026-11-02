using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Vats;
using Il2CppCodeBase.Components.Vessels;
using PotionCoop.Net;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>
    /// Plays the other player's pours as they happen: the liquid leaves the source and rises in the target with the
    /// game's own level animations, and the ghost tips its bottle over the target. The contents themselves are
    /// confirmed by the regular state messages that follow, so a failure here only costs the animation.
    /// </summary>
    internal static class Pours
    {
        static bool _off;
        static readonly List<(float due, Action action)> Later = new();

        public static VesselRef Ref(IPourVessel vessel)
        {
            if (vessel == null)
                return null;
            var potion = vessel.TryCast<PotionFacade>();
            if (potion != null)
                return new VesselRef { K = VesselRef.Potion, U = potion.Uid };
            var vat = vessel.TryCast<VatFacade>();
            if (vat != null)
                return new VesselRef { K = VesselRef.Vat, U = vat.Uid };
            return null;
        }

        public static int TapIndex(VatFacade vat, VatTapFacade tap)
        {
            var taps = vat._taps;
            for (var i = 0; taps != null && i < taps.Length; i++)
                if (taps[i] != null && taps[i].Pointer == tap.Pointer)
                    return i;
            return -1;
        }

        /// <summary>How long a pour of this size takes on screen.</summary>
        static float Duration(int units) => Mathf.Clamp(0.45f + 0.12f * units, 0.5f, 1.4f);

        public static void Play(PourMsg msg)
        {
            if (_off || msg?.From == null || msg.To == null)
                return;
            try { PlayImpl(msg); }
            catch (Exception e)
            {
                _off = true;
                CoopMod.Log.Warning($"pour animations off for this session: {e}");
            }
        }

        static void PlayImpl(PourMsg msg)
        {
            var populator = GameRefs.Populator;
            if (populator == null)
                return;
            var family = (eSectionFamily)msg.F;
            var units = msg.N;
            var duration = Duration(units);

            PotionFacade sourcePotion = null;
            VatFacade tapVat = null;
            VatTapFacade tap = null;
            IPourStreamSource source = null;
            if (msg.From.K == VesselRef.Potion && populator.TryGetPotion(msg.From.U, out sourcePotion) && sourcePotion != null)
                source = sourcePotion.Cast<IPourStreamSource>();
            else if (msg.From.K == VesselRef.Tap)
            {
                tapVat = World.FindVat(msg.From.U);
                var taps = tapVat != null ? tapVat._taps : null;
                if (taps != null && msg.From.I >= 0 && msg.From.I < taps.Length)
                {
                    tap = taps[msg.From.I];
                    source = tap != null ? tap.Cast<IPourStreamSource>() : null;
                }
            }
            if (source == null)
                return;

            PotionFacade targetPotion = null;
            VatFacade targetVat = null;
            if (msg.To.K == VesselRef.Potion)
                populator.TryGetPotion(msg.To.U, out targetPotion);
            else if (msg.To.K == VesselRef.Vat)
                targetVat = World.FindVat(msg.To.U);
            if (targetPotion == null && targetVat == null)
                return;

            // What can really move here. Our copies of the two vessels can differ from the sender's for a moment (the
            // other player poured into or out of one of them at the same instant): liquid must never appear or vanish,
            // so only what this side can both take and hold moves. The sender's states settle the rest.
            var room = targetPotion != null ? targetPotion.Contents.FreeSpace : targetVat.Contents.FreeSpace;
            var available = sourcePotion != null
                ? (!sourcePotion.Contents.IsEmpty && sourcePotion.Contents.TopFamily == family ? sourcePotion.Contents.TopRunLength : 0)
                : RunAtEnd(tapVat, family);
            var moved = Math.Min(units, Math.Min(room, available));
            if (moved < units)
                CoopMod.Log.Msg($"pour of {units} {family}: only {moved} can move here (the vessels just changed on this side too)");

            Coop.Ghost?.Act("pour");
            Vector3? targetPoint = null;
            World.AsApplying(() =>
            {
                if (targetPotion != null)
                {
                    var target = targetPotion;
                    if (moved > 0)
                        target.ReceivePour(source, family, moved, false);
                    target.RevealPourOver(duration, false);
                    targetPoint = target.transform.position + Vector3.up * 0.12f;
                    After(duration, () =>
                    {
                        target.OnReceiveFinished(source);
                        World.FixVisual(target);
                    });
                    // Drawing from a tap: the bottle goes under the spout for the draw.
                    if (tap != null && tap.Spout != null)
                        World.HoldUnder(target.Uid, tap.Spout.position, duration);
                }
                else
                {
                    var vat = targetVat;
                    if (moved > 0)
                        vat.ReceivePour(source, family, moved, false);
                    vat.RevealPour(false);
                    targetPoint = vat.LiquidSurfacePoint;
                    After(duration, () => vat.OnReceiveFinished(source));
                }

                if (sourcePotion != null)
                {
                    if (moved > 0)
                        sourcePotion.DrainOut(moved, false);
                    sourcePotion.RevealDrain(duration, false);
                    if (targetPoint.HasValue)
                        World.TipOver(sourcePotion.Uid, targetPoint.Value, duration);
                    // The drain's reveal may not run on a bottle the game doesn't see pouring: settle the level after.
                    var drained = sourcePotion;
                    After(duration + 0.15f, () => World.FixVisual(drained));
                }
                else if (tap != null)
                {
                    // The game takes the drawn units out of the vat as the draw starts (the level follows later); so
                    // does the replay, or the units would be in the vat and in the bottle at once until the sender's
                    // states arrive (and stay so if those cross one of ours).
                    TakeFromVat(tapVat, family, moved);
                    tap.SetOpen(true, false);
                    var driver = tapVat.Driver();
                    driver.HoldOutgoing(moved);
                    driver.ReleaseOutgoing(moved, false, duration);
                    After(duration, () => tap.SetOpen(false, false));
                }
            });
        }

        /// <summary>How many units of this family a tap can draw: the run at the end of the vat's list.</summary>
        static int RunAtEnd(VatFacade vat, eSectionFamily family)
        {
            var list = vat.Contents._units;
            var run = 0;
            for (var i = list.Count - 1; i >= 0 && list[i] == family; i--)
                run++;
            return run;
        }

        /// <summary>A tap draws the last units of the vat's list (measured: a draw takes the list's end).</summary>
        static void TakeFromVat(VatFacade vat, eSectionFamily family, int units)
        {
            var contents = vat.Contents;
            for (var n = 0; n < units && RunAtEnd(vat, family) > 0; n++)
                contents.RemoveAt(contents._units.Count - 1);
        }

        static void After(float seconds, Action action) => Later.Add((Time.realtimeSinceStartup + seconds, action));

        /// <summary>A replayed pour is still running.</summary>
        public static bool Busy => Later.Count > 0;

        /// <summary>Per frame in the lab: the ends of pours that are due.</summary>
        public static void Tick()
        {
            if (Later.Count == 0)
                return;
            var now = Time.realtimeSinceStartup;
            for (var i = Later.Count - 1; i >= 0; i--)
            {
                if (now < Later[i].due)
                    continue;
                var action = Later[i].action;
                Later.RemoveAt(i);
                World.AsApplying(() =>
                {
                    try { action(); }
                    catch (Exception e) { CoopMod.Log.Warning($"finishing a pour: {e.Message}"); }
                });
            }
        }

        /// <summary>Level unloaded: pending pour ends refer to objects that are gone.</summary>
        public static void Forget() => Later.Clear();
    }
}
