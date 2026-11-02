using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppCodeBase.Components.EssenceSeparator;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Potions.Liquid;
using Il2CppCodeBase.Components.Vats;
using Il2CppCodeBase.Components.Vessels;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>
    /// Development aid, only with --coop-test &lt;name&gt; (use a copy of a save, see --coop-saves): scripted actions in
    /// the lab with a timeline of what the game's data does, to check assumptions the sync relies on.
    /// pourtiming: bottle → bottle, tap → bottle and bottle → vat pours; contents logged over time.
    /// </summary>
    internal static class DevTests
    {
        static readonly string Test = Arg("--coop-test");
        static float _startAt = -1f;
        static readonly List<(float at, Action action)> Steps = new();

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static float Now => Time.realtimeSinceStartup;

        static readonly bool AutoHost = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-autohost") >= 0;
        static readonly bool AutoJoin = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-autojoin") >= 0;
        static bool _autoDone;

        public static void Tick()
        {
            // --coop-autohost / --coop-autojoin: two windows on one PC find each other without clicks.
            if ((AutoHost || AutoJoin) && GameRefs.State == eGameState.Lobby && UI.UiClone.ButtonTemplate != null)
            {
                if (!_autoDone)
                {
                    _autoDone = true;
                    if (AutoHost)
                        Coop.StartHost();
                    else
                        Coop.Join(System.Net.IPAddress.Loopback);
                }
                else if (AutoJoin && Coop.Stage == Coop.GuestStage.Choosing)
                    Coop.ChooseProfile(copyHostLevel: true);
            }

            if (Test == null || !GameRefs.InLevel || GameRefs.Populator == null)
                return;
            // Tests in co-op wait for the link (and the guest for its full world).
            if ((AutoHost || AutoJoin) && !Coop.IsConnected)
                return;
            if (_startAt < 0f)
            {
                _startAt = Now + 8f;
                return;
            }
            if (_startAt < float.MaxValue && Now >= _startAt)
            {
                _startAt = float.MaxValue;
                try { Start(Test); }
                catch (Exception e) { CoopMod.Log.Error($"[test] {Test}: {e}"); }
            }
            for (var i = Steps.Count - 1; i >= 0; i--)
            {
                if (Now < Steps[i].at)
                    continue;
                var action = Steps[i].action;
                Steps.RemoveAt(i);
                try { action(); }
                catch (Exception e) { CoopMod.Log.Error($"[test] step: {e}"); }
            }
            try { ShowTick(); }
            catch (Exception e)
            {
                CoopMod.Log.Error($"[test] avatar: {e}");
                _showPhase = null;
            }
        }

        static void At(float delay, Action action) => Steps.Add((Now + delay, action));

        static void Start(string test)
        {
            switch (test)
            {
                case "pourtiming":
                    PourTiming();
                    break;
                case "separator":
                    Separator();
                    break;
                case "stress":
                    Stress();
                    break;
                case "sepcoop":
                    SeparatorCoop();
                    break;
                case "avatar":
                    AvatarShow();
                    break;
                default:
                    CoopMod.Log.Warning($"[test] unknown test {test}");
                    break;
            }
        }

        static string Units(LiquidContents contents) =>
            contents == null ? "?" : "[" + string.Join(",", World.ReadUnits(contents._units)) + "]";

        static bool Resting(LabPotionsPopulator populator, PotionFacade potion) =>
            populator.TryGetSlotOf(potion, out var slot) && slot != null && !potion.IsSealed && !potion.IsFinished.Value;

        static void PourTiming()
        {
            var populator = GameRefs.Populator;
            var potions = new List<PotionFacade>();
            foreach (var entry in populator._potionsByUid)
                if (entry.Value != null && Resting(populator, entry.Value))
                    potions.Add(entry.Value);

            // 1) bottle → bottle
            var source = potions.FirstOrDefault(p => p.Contents.Count >= 2 && !p.Contents.IsUniform);
            source ??= potions.FirstOrDefault(p => p.Contents.Count >= 1);
            var family = source.Contents.TopFamily;
            var target = potions.FirstOrDefault(p => p != source && p.Contents.FreeSpace >= 1 && !p.Contents.IsEmpty && p.Contents.TopFamily == family)
                         ?? potions.First(p => p != source && p.Contents.IsEmpty);
            var units = Math.Min(source.Contents.TopRunLength, target.Contents.FreeSpace);
            Timeline("bottle→bottle", () => source.PourOutInto(target.Cast<IPourVessel>(), family, units, false),
                ("source", () => Units(source.Contents)), ("target", () => Units(target.Contents)));

            // 2) tap → bottle, 4 s later
            At(4f, () =>
            {
                var vat = World.Vats().FirstOrDefault(v => v.Contents.Count > 0 && v._taps != null && v._taps.Length > 0);
                if (vat == null)
                {
                    CoopMod.Log.Msg("[test] tap→bottle: no vat with liquid");
                    return;
                }
                var tap = vat._taps[0];
                var drawn = (eSectionFamily)vat.Contents._units[vat.Contents._units.Count - 1];
                var empty = potions.First(p => p.Contents.IsEmpty && p != target);
                Timeline($"tap→bottle (vat #{vat.Uid}, family {drawn})", () => vat.DrawOutInto(tap, empty, drawn, 1, false),
                    ("vat", () => Units(vat.Contents)), ("target", () => Units(empty.Contents)));
            });

            // 3) bottle → vat, 8 s later
            At(8f, () =>
            {
                var vat = World.Vats().FirstOrDefault(v => v.Contents.FreeSpace >= 1);
                var from = potions.FirstOrDefault(p => p != source && p != target && p.Contents.Count >= 1);
                if (vat == null || from == null)
                {
                    CoopMod.Log.Msg("[test] bottle→vat: nothing suitable");
                    return;
                }
                var poured = from.Contents.TopFamily;
                Timeline($"bottle→vat (vat #{vat.Uid})", () => from.PourOutInto(vat.Cast<IPourVessel>(), poured, 1, false),
                    ("source", () => Units(from.Contents)), ("vat", () => Units(vat.Contents)));
            });
        }

        /// <summary>What the separator holds, part by part (to know what a liquid count must include).</summary>
        public static string SeparatorParts(EssenceSeparatorFacade separator)
        {
            var loop = new Il2CppSystem.Collections.Generic.List<eSectionFamily>();
            separator.CopyLoopUnits(loop);
            var c = separator._circulator;
            var cells = 0;
            if (c._cells != null)
                for (var i = 0; i < c._cells.Length; i++)
                    if (c._cells[i] != eSectionFamily.None)
                        cells++;
            var waiting = 0;
            if (c._waiting != null)
                for (var i = 0; i < c._waiting.Count; i++)
                    waiting += c._waiting[i]?.Count ?? 0;
            var spilled = c._spilled?.Count ?? 0;
            return $"loop={loop.Count} cells={cells} waiting={waiting} spilled={spilled} pipe={separator.UnitsInPipe} count={separator.UnitCount} cap={separator.Capacity}";
        }

        static void Separator()
        {
            var populator = GameRefs.Populator;
            var separator = World.Separators().FirstOrDefault();
            if (separator == null)
            {
                CoopMod.Log.Msg("[test] no separator");
                return;
            }
            var holders = separator._holders;
            CoopMod.Log.Msg($"[test] separator #{separator.Uid} unlocked={separator.IsUnlocked} holders={holders?.Length} {SeparatorParts(separator)}");
            for (var i = 0; holders != null && i < holders.Length; i++)
            {
                var h = holders[i];
                CoopMod.Log.Msg($"[test]   holder {h.Index}: usable={separator.IsUsable(h)} occupied={h.IsOccupied} vessel={(h._vessel != null ? h._vessel.Uid + " " + Units(h._vessel.Contents) : "-")}");
            }

            var potions = new List<PotionFacade>();
            foreach (var entry in populator._potionsByUid)
                if (entry.Value != null && Resting(populator, entry.Value))
                    potions.Add(entry.Value);
            var free = new List<PourOutHolderFacade>();
            for (var i = 0; holders != null && i < holders.Length; i++)
                if (separator.IsUsable(holders[i]) && !holders[i].IsOccupied)
                    free.Add(holders[i]);
            if (free.Count < 2)
            {
                CoopMod.Log.Msg($"[test] only {free.Count} free usable holder(s)");
                if (free.Count == 0)
                    return;
            }

            var mixed = potions.First(p => p.Contents.Count >= 2 && !p.Contents.IsUniform);
            var empty = potions.First(p => p.Contents.IsEmpty);
            var persistence = GameRefs.Persistence;
            void HangOn(PotionFacade potion, PourOutHolderFacade holder)
            {
                if (populator.TryGetSlotOf(potion, out var slot) && slot != null)
                    slot.ClearPotion(true);
                holder.Hang(potion, false);
                persistence.RecordHung(potion, holder);
                CoopMod.Log.Msg($"[test] hung #{potion.Uid} {Units(potion.Contents)} on holder {holder.Index}");
            }

            string Read() =>
                $"mixed={Units(mixed.Contents)} empty={Units(empty.Contents)} {SeparatorParts(separator)} " +
                $"h{free[0].Index}:pour={free[0].IsPouringOut},drip={free[0].IsDripping}" +
                (free.Count > 1 ? $" h{free[1].Index}:pour={free[1].IsPouringOut},drip={free[1].IsDripping}" : "");

            CoopMod.Log.Msg($"[test] before {Read()}  {World.LiquidReport(populator)}");
            HangOn(mixed, free[0]);
            if (free.Count > 1)
                HangOn(empty, free[1]);
            for (var t = 1; t <= 40; t++)
            {
                var at = t * 0.5f;
                At(at, () => CoopMod.Log.Msg($"[test] +{at:0.0}s {Read()}" + (at % 5 == 0 ? $"  {World.LiquidReport(populator)}" : "")));
            }
            At(2f, () =>
            {
                var poured = separator.PourOutOf(free[0], false);
                CoopMod.Log.Msg($"[test] PourOutOf(holder {free[0].Index}) = {poured}");
            });
        }

        // ---- avatar: the other player's character shown in front of the camera, move after move ----

        static Ghost _show;
        static Vector3 _showAnchor;
        static float _showYaw;
        static string _showPhase;
        static float _showAngle;
        static readonly List<PotionFacade> ShowBottles = new();

        static void AvatarShow()
        {
            var camera = Camera.main;
            var hero = GameRefs.Hero;
            var forward = camera.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            _showAnchor = camera.transform.position + forward * 3.2f;
            _showAnchor.y = hero.Position.y;
            _showYaw = Quaternion.LookRotation(-forward).eulerAngles.y;
            _show = new Ghost(new Color(0.35f, 0.55f, 0.95f), _showAnchor) { Name = "Preview" };
            var populator = GameRefs.Populator;
            foreach (var entry in populator._potionsByUid)
                if (entry.Value != null && Resting(populator, entry.Value) && entry.Value.Contents.Count >= 2 && ShowBottles.Count < 2)
                    ShowBottles.Add(entry.Value);

            var phases = new[] { "idle", "walk", "run", "carry", "carrywalk", "pour", "throw", "lookdown", "lookup", "back" };
            for (var i = 0; i < phases.Length; i++)
            {
                var phase = phases[i];
                At(i * 6f, () =>
                {
                    _showPhase = phase;
                    CoopMod.Log.Msg($"[test] avatar phase {phase}");
                    if (phase == "pour")
                        _show.Act("pour");
                    if (phase == "throw")
                        _show.Act("throw");
                });
            }
            At(phases.Length * 6f, () =>
            {
                CoopMod.Log.Msg("[test] avatar done");
                _showPhase = null;
            });
        }

        static void ShowTick()
        {
            if (_show == null || _showPhase == null)
                return;
            var dt = Time.unscaledDeltaTime;
            var pos = _showAnchor;
            var yaw = _showYaw;
            var pitch = 0f;
            var held = 0;
            switch (_showPhase)
            {
                case "walk":
                case "run":
                case "carrywalk":
                    var speed = _showPhase == "run" ? 4.2f : 1.6f;
                    _showAngle += dt * speed / 1.2f;
                    pos = _showAnchor + new Vector3(Mathf.Cos(_showAngle), 0f, Mathf.Sin(_showAngle)) * 1.2f;
                    var tangent = new Vector3(-Mathf.Sin(_showAngle), 0f, Mathf.Cos(_showAngle));
                    yaw = Quaternion.LookRotation(tangent).eulerAngles.y;
                    held = _showPhase == "carrywalk" ? 2 : 0;
                    break;
                case "back":
                    _showAngle += dt;
                    pos = _showAnchor + Quaternion.Euler(0f, _showYaw, 0f) * Vector3.back * Mathf.PingPong(_showAngle * 1.2f, 1.5f);
                    break;
                case "carry":
                case "pour":
                    held = _showPhase == "pour" ? 1 : 2;
                    break;
                case "lookdown":
                    pitch = 30f;
                    break;
                case "lookup":
                    pitch = -30f;
                    break;
            }
            _show.Held = held;
            _show.SetTarget(pos, yaw, pitch);
            _show.Tick(dt);
            for (var i = 0; i < ShowBottles.Count; i++)
            {
                var (p, r) = i < held ? _show.CarryPose(i, held) : (ShowBottles[i].transform.position, ShowBottles[i].transform.rotation);
                if (i < held)
                    ShowBottles[i].transform.SetPositionAndRotation(p, r);
            }
        }

        static readonly System.Random Random = new();
        const float StressSeconds = 150f;

        /// <summary>
        /// Random pours (bottle → bottle, bottle → vat, tap → bottle) about once a second for a while, then quiet.
        /// Run on both windows at once: the liquid totals must stay those of the lab, on both sides.
        /// </summary>
        static void Stress()
        {
            var until = Now + StressSeconds;
            var done = 0;
            void Next()
            {
                if (Now >= until)
                {
                    CoopMod.Log.Msg($"[test] stress: {done} pours done, now quiet");
                    for (var t = 5; t <= 60; t += 5)
                    {
                        var at = t;
                        At(at, () => CoopMod.Log.Msg($"[test] quiet +{at}s sync={Coop.InSync} {World.LiquidReport(GameRefs.Populator)}"));
                    }
                    return;
                }
                try
                {
                    if (RandomPour())
                        done++;
                }
                catch (Exception e) { CoopMod.Log.Warning($"[test] stress pour: {e.Message}"); }
                if (done % 20 == 0)
                    CoopMod.Log.Msg($"[test] stress: {done} pours, sync={Coop.InSync} {World.LiquidReport(GameRefs.Populator)}");
                At(0.6f + (float)Random.NextDouble() * 0.9f, Next);
            }
            CoopMod.Log.Msg($"[test] stress start {World.LiquidReport(GameRefs.Populator)}");
            Next();
        }

        static bool RandomPour()
        {
            var populator = GameRefs.Populator;
            var potions = new List<PotionFacade>();
            foreach (var entry in populator._potionsByUid)
                if (entry.Value != null && Resting(populator, entry.Value))
                    potions.Add(entry.Value);
            var kind = Random.Next(100);
            if (kind < 15)
            {
                var vat = World.Vats().Where(v => v.Contents.Count > 0 && v._taps != null && v._taps.Length > 0).OrderBy(_ => Random.Next()).FirstOrDefault();
                if (vat == null)
                    return false;
                var drawn = (eSectionFamily)vat.Contents._units[vat.Contents._units.Count - 1];
                var into = potions.Where(p => p.Contents.FreeSpace >= 1 && (p.Contents.IsEmpty || p.Contents.TopFamily == drawn))
                    .OrderBy(_ => Random.Next()).FirstOrDefault();
                if (into == null)
                    return false;
                var before = World.ReadUnits(vat.Contents._units);
                vat.DrawOutInto(vat._taps[Random.Next(vat._taps.Length)], into, drawn, 1, false);
                var after = World.ReadUnits(vat.Contents._units);
                var lost = before.ToList();
                foreach (var u in after)
                    lost.Remove(u);
                if (lost.Count != 1 || lost[0] != (byte)drawn)
                    CoopMod.Log.Warning($"[test] tap: asked for {(int)drawn}, the vat lost [{string.Join(",", lost)}] (was [{string.Join(",", before)}])");
                return true;
            }
            var source = potions.Where(p => p.Contents.Count > 0).OrderBy(_ => Random.Next()).First();
            var family = source.Contents.TopFamily;
            if (kind < 30)
            {
                var vat = World.Vats().Where(v => v.Contents.FreeSpace >= 1).OrderBy(_ => Random.Next()).FirstOrDefault();
                if (vat == null)
                    return false;
                source.PourOutInto(vat.Cast<IPourVessel>(), family, Math.Min(source.Contents.TopRunLength, vat.Contents.FreeSpace), false);
                return true;
            }
            var target = potions.Where(p => p != source && p.Contents.FreeSpace >= 1 && (p.Contents.IsEmpty || p.Contents.TopFamily == family))
                .OrderBy(_ => Random.Next()).FirstOrDefault();
            if (target == null)
                return false;
            source.PourOutInto(target.Cast<IPourVessel>(), family, Math.Min(source.Contents.TopRunLength, target.Contents.FreeSpace), false);
            return true;
        }

        /// <summary>Guest side: bottles onto separator holders, then pours out of one (done by the host's separator).</summary>
        static void SeparatorCoop()
        {
            var populator = GameRefs.Populator;
            var separator = World.Separators().FirstOrDefault();
            if (separator == null || !separator.IsUnlocked)
            {
                CoopMod.Log.Msg("[test] sepcoop: no unlocked separator");
                return;
            }
            var free = new List<PourOutHolderFacade>();
            for (var i = 0; i < separator._holders.Length; i++)
                if (separator.IsUsable(separator._holders[i]) && !separator._holders[i].IsOccupied)
                    free.Add(separator._holders[i]);
            var potions = new List<PotionFacade>();
            foreach (var entry in populator._potionsByUid)
                if (entry.Value != null && Resting(populator, entry.Value))
                    potions.Add(entry.Value);
            var mixed = potions.First(p => p.Contents.Count >= 3 && !p.Contents.IsUniform);
            var empty = potions.First(p => p.Contents.IsEmpty);
            var persistence = GameRefs.Persistence;
            void HangOn(PotionFacade potion, PourOutHolderFacade holder)
            {
                if (populator.TryGetSlotOf(potion, out var slot) && slot != null)
                    slot.ClearPotion(true);
                holder.Hang(potion, false);
                persistence.RecordHung(potion, holder);
            }
            CoopMod.Log.Msg($"[test] sepcoop start mixed=#{mixed.Uid}{Units(mixed.Contents)} empty=#{empty.Uid} {SeparatorParts(separator)} {World.LiquidReport(populator)}");
            HangOn(mixed, free[0]);
            HangOn(empty, free[1]);
            for (var n = 0; n < 3; n++)
                At(3f + n * 3f, () =>
                {
                    var r = separator.PourOutOf(free[0], false);
                    CoopMod.Log.Msg($"[test] sepcoop: asked to pour out of holder {free[0].Index} -> {r}");
                });
            for (var t = 1; t <= 20; t++)
            {
                var at = t;
                At(at, () => CoopMod.Log.Msg($"[test] sepcoop +{at}s mixed={Units(mixed.Contents)} empty={Units(empty.Contents)} {SeparatorParts(separator)}" +
                                             (at % 5 == 0 ? $" {World.LiquidReport(populator)}" : "")));
            }
        }

        /// <summary>Logs the probes before, right after, and at intervals after the action.</summary>
        static void Timeline(string name, Action action, params (string label, Func<string> read)[] probes)
        {
            string Read() => string.Join("  ", probes.Select(p => $"{p.label}={p.read()}"));
            var t0 = Now;
            CoopMod.Log.Msg($"[test] {name}: before  {Read()}  {World.LiquidReport(GameRefs.Populator)}");
            action();
            CoopMod.Log.Msg($"[test] {name}: +0.000  {Read()}");
            foreach (var delay in new[] { 0.05f, 0.3f, 0.8f, 1.5f, 3f })
                At(delay, () => CoopMod.Log.Msg($"[test] {name}: +{Now - t0:0.000}  {Read()}" +
                                                (delay >= 3f ? $"  {World.LiquidReport(GameRefs.Populator)}" : "")));
        }
    }
}
