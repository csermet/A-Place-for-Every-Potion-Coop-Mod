using System;
using MelonLoader;
using PotionCoop.Game;
using PotionCoop.UI;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(PotionCoop.CoopMod), "PotionCoop", PotionCoop.Net.Protocol.ModVersion, "caner")]
[assembly: MelonGame("CubeQuad", "A Place for Every Potion")]

namespace PotionCoop
{
    /// <summary>
    /// Entry point. The CO-OP entry in the main menu (or the CO-OP button while paused) opens the co-op window.
    /// Spare hotkeys (hold Ctrl): F7 status to log · F8 host on/off · F9 join · F10 resync · F11 leave · F12 hide status bar
    /// </summary>
    public class CoopMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        bool _showStatusBar = true;
        static float UiScale = 1f;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Settings.Init();
            // Development aid: --coop-saves <folder> plays the world and progress of another save folder (e.g. a copy
            // of a backup) instead of the solo saves.
            var args = Environment.GetCommandLineArgs();
            var saves = Array.IndexOf(args, "--coop-saves");
            if (saves >= 0 && saves + 1 < args.Length)
                SavePaths.Activate(args[saves + 1]);
            Log.Msg($"PotionCoop {Net.Protocol.ModVersion} ready: CO-OP in the main menu (spare keys Ctrl+F8 host, Ctrl+F9 join)");
        }

        public override void OnUpdate()
        {
            HandleHotkeys();
            try
            {
                Coop.Update();
                MenuButton.Tick();
                CoopPanel.Tick();
                UiDump.Tick();
                DevTests.Tick();
            }
            catch (Exception e) { Log.Error($"update: {e}"); }
        }

        public override void OnLateUpdate()
        {
            try { Coop.LateUpdate(); }
            catch (Exception e) { Log.Error($"late update: {e}"); }
        }

        public override void OnDeinitializeMelon() => Coop.Stop();

        void HandleHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.ctrlKey.isPressed)
                return;

            if (kb.f7Key.wasPressedThisFrame)
                Coop.DumpStatus();
            else if (kb.f8Key.wasPressedThisFrame)
            {
                if (Coop.IsRunning && Coop.IsHost)
                    Coop.Stop();
                else
                    Coop.StartHost();
            }
            else if (kb.f9Key.wasPressedThisFrame)
                Coop.Join();
            else if (kb.f10Key.wasPressedThisFrame)
                Coop.ForceResync();
            else if (kb.f11Key.wasPressedThisFrame)
                Coop.Stop();
            else if (kb.f12Key.wasPressedThisFrame)
                _showStatusBar = !_showStatusBar;
        }

        public override void OnGUI()
        {
            // IMGUI defaults are tiny on 1440p/4K screens.
            UiScale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.skin.box.fontSize = Mathf.RoundToInt(14 * UiScale);
            GUI.skin.label.fontSize = Mathf.RoundToInt(16 * UiScale);

            Guarded("status bar", () =>
            {
                // In the menus the co-op window shows the same; the bar is for the lab.
                if (_showStatusBar && Coop.IsRunning && !CoopPanel.IsOpen)
                    DrawStatusBar();
            });
            Guarded("name label", DrawGhostLabel);
        }

        static readonly System.Collections.Generic.HashSet<string> ReportedGuiErrors = new();

        /// <summary>OnGUI runs several times a frame: report each failing piece once instead of flooding the log.</summary>
        static void Guarded(string what, Action draw)
        {
            try { draw(); }
            catch (Exception e)
            {
                if (ReportedGuiErrors.Add(what + e.GetType().Name))
                    Log.Error($"drawing the {what} failed: {e}");
            }
        }

        static void DrawStatusBar()
        {
            var role = Coop.IsHost ? "HOST · " : "GUEST · ";
            var text = $"PotionCoop  {role}{Coop.Status}";
            if (Coop.IsConnected)
            {
                text += Coop.PingMs >= 0 ? $"  ·  ping {Coop.PingMs:F0} ms" : "";
                text += Coop.InSync switch
                {
                    true => "  ·  in sync ✓",
                    false => "  ·  syncing…",
                    _ => "",
                };
            }
            var width = Mathf.Min(Screen.width - 20, 760 * UiScale);
            GUI.Box(new Rect((Screen.width - width) / 2f, 6 * UiScale, width, 28 * UiScale), text);
        }

        static GUIStyle _labelStyle;
        static GUIStyle _shadowStyle;

        /// <summary>The other player's name above their head: shadowed, sized by distance, faded behind walls.</summary>
        static void DrawGhostLabel()
        {
            var ghost = Coop.Ghost;
            var camera = Camera.main;
            if (ghost == null || !ghost.Alive || camera == null)
                return;
            var anchor = ghost.LabelAnchor;
            var screen = camera.WorldToScreenPoint(anchor);
            if (screen.z <= 0f)
                return;

            var distance = Vector3.Distance(camera.transform.position, anchor);
            var size = Mathf.RoundToInt(Mathf.Lerp(22f, 13f, Mathf.InverseLerp(2f, 25f, distance)) * UiScale);
            // Something between us and them (a shelf, a wall): keep the name visible but faded.
            var hidden = Physics.Linecast(camera.transform.position, anchor, out var hit, ~0, QueryTriggerInteraction.Ignore)
                         && hit.distance < distance - 0.3f;

            _labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _shadowStyle ??= new GUIStyle(_labelStyle);
            _labelStyle.fontSize = size;
            _shadowStyle.fontSize = size;
            _labelStyle.normal.textColor = new Color(1f, 0.92f, 0.75f, hidden ? 0.45f : 1f);
            _shadowStyle.normal.textColor = new Color(0f, 0f, 0f, hidden ? 0.3f : 0.75f);

            var label = ghost.Name;
            if (ghost.InMenu)
                label += "  (in menu)";
            var w = 320 * UiScale;
            var h = 34 * UiScale;
            var rect = new Rect(screen.x - w / 2f, Screen.height - screen.y - h / 2f, w, h);
            var offset = Mathf.Max(1f, 1.5f * UiScale);
            GUI.Label(new Rect(rect.x + offset, rect.y + offset, w, h), label, _shadowStyle);
            GUI.Label(rect, label, _labelStyle);
        }
    }
}
