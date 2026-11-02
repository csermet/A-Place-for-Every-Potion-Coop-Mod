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

            Vector3? targetPoint = null;
            World.AsApplying(() =>
            {
                if (msg.To.K == VesselRef.Potion && populator.TryGetPotion(msg.To.U, out var target) && target != null)
                {
                    target.ReceivePour(source, family, units, false);
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
                else if (msg.To.K == VesselRef.Vat && World.FindVat(msg.To.U) is { } vat)
                {
                    vat.ReceivePour(source, family, units, false);
                    vat.RevealPour(false);
                    targetPoint = vat.LiquidSurfacePoint;
                    After(duration, () => vat.OnReceiveFinished(source));
                }

                if (sourcePotion != null)
                {
                    sourcePotion.DrainOut(units, false);
                    sourcePotion.RevealDrain(duration, false);
                    if (targetPoint.HasValue)
                        World.TipOver(sourcePotion.Uid, targetPoint.Value, duration);
                    // The drain's reveal may not run on a bottle the game doesn't see pouring: settle the level after.
                    var drained = sourcePotion;
                    After(duration + 0.15f, () => World.FixVisual(drained));
                }
                else if (tap != null)
                {
                    tap.SetOpen(true, false);
                    var driver = tapVat.Driver();
                    driver.HoldOutgoing(units);
                    driver.ReleaseOutgoing(units, false, duration);
                    After(duration, () => tap.SetOpen(false, false));
                }
            });
        }

        static void After(float seconds, Action action) => Later.Add((Time.realtimeSinceStartup + seconds, action));

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
