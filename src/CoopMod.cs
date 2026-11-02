using System;
using MelonLoader;
using PotionCoop.Game;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(PotionCoop.CoopMod), "PotionCoop", PotionCoop.Net.Protocol.ModVersion, "caner")]
[assembly: MelonGame("CubeQuad", "A Place for Every Potion")]

namespace PotionCoop
{
    /// <summary>
    /// Entry point. Hotkeys (hold Ctrl):
    ///   F8 host on/off · F9 join (from the main menu) · F10 resync · F11 leave · F12 hide/show panel
    /// </summary>
    public class CoopMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        bool _showPanel = true;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            Settings.Init();
            Log.Msg($"PotionCoop {Net.Protocol.ModVersion} ready — Ctrl+F8 host, Ctrl+F9 join");
        }

        public override void OnUpdate()
        {
            HandleHotkeys();
            try { Coop.Update(); }
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

            if (kb.f8Key.wasPressedThisFrame)
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
                _showPanel = !_showPanel;
        }

        public override void OnGUI()
        {
            if (_showPanel)
                DrawPanel();
            DrawGhostLabel();
        }

        static void DrawPanel()
        {
            var role = !Coop.IsRunning ? "" : Coop.IsHost ? "HOST · " : "KATILIMCI · ";
            var text = $"PotionCoop  {role}{Coop.Status}";
            if (Coop.IsConnected)
            {
                text += Coop.PingMs >= 0 ? $"  ·  ping {Coop.PingMs:F0} ms" : "";
                text += Coop.InSync switch
                {
                    true => "  ·  senkron ✓",
                    false => "  ·  senkronlanıyor…",
                    _ => "",
                };
            }
            else if (!Coop.IsRunning)
                text += "   (Ctrl+F8 host · Ctrl+F9 katıl · Ctrl+F12 gizle)";

            var width = Mathf.Min(Screen.width - 20, 760);
            GUI.Box(new Rect((Screen.width - width) / 2f, 6, width, 26), text);
        }

        static void DrawGhostLabel()
        {
            var ghost = Coop.Ghost;
            var camera = Camera.main;
            if (ghost == null || !ghost.Alive || camera == null)
                return;
            var screen = camera.WorldToScreenPoint(ghost.LabelAnchor);
            if (screen.z <= 0f)
                return;
            var label = ghost.Name;
            if (ghost.Held > 0)
                label += $"  ({ghost.Held} şişe)";
            if (ghost.InMenu)
                label += "  · menüde";
            GUI.Label(new Rect(screen.x - 110, Screen.height - screen.y - 12, 220, 24), label);
        }
    }
}
