using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Il2Cpp;
using PotionCoop.Game;
using PotionCoop.Net;
using UnityEngine;

namespace PotionCoop.UI
{
    /// <summary>
    /// The co-op window (IMGUI): host a game, see who is hosting on the home network and join them, or type an address.
    /// Opened from the CO-OP entry in the main menu, or from the small button shown while the game is paused.
    /// </summary>
    internal static class CoopPanel
    {
        static readonly Color Gold = new(1f, 0.84f, 0.52f);
        static readonly Color Soft = new(0.86f, 0.80f, 0.70f);
        static readonly Color Dim = new(0.62f, 0.57f, 0.50f);

        public static bool Open { get; private set; }

        // hosts seen on the LAN: "ip:port" -> (address, port, name, last seen)
        static readonly Dictionary<string, (IPAddress address, int port, string name, float seen)> Found = new();
        static bool _scanning;
        static float _nextScan;
        static float _firstScanAt = -1f;

        static List<string> _myAddresses = new();
        static float _addressesAt = -100f;

        static string _manualAddress = "";
        static string _nameField;

        static GUIStyle _box, _title, _text, _small, _button, _field;
        static float _styledFor;

        static float Now => Time.realtimeSinceStartup;

        public static void Toggle()
        {
            if (Open)
                Close();
            else
                Show();
        }

        public static void Show()
        {
            Open = true;
            _nameField = Settings.RawName;
            _manualAddress = Settings.HostAddress ?? "";
            _firstScanAt = -1f;
            _nextScan = 0f;
        }

        public static void Close()
        {
            if (!Open)
                return;
            Open = false;
            Settings.Save();
        }

        /// <summary>Per frame: keep scanning the LAN while the window is open and we are not connected.</summary>
        public static void Tick()
        {
            // The window needs a free mouse cursor: only in the main menu or while paused.
            if (Open && GameRefs.State is not (eGameState.Lobby or eGameState.Paused or eGameState.None))
                Close();
            if (!Open || Coop.IsRunning || _scanning || Now < _nextScan)
                return;

            _scanning = true;
            if (_firstScanAt < 0f)
                _firstScanAt = Now;
            var port = Settings.DiscoveryPort;
            Task.Run(() =>
            {
                List<(IPAddress address, int port, string name)> hosts;
                try { hosts = DiscoveryResponder.Scan(port, TimeSpan.FromSeconds(1.2)); }
                catch (Exception) { hosts = new(); }
                MainThread.Post(() =>
                {
                    foreach (var (address, tcpPort, name) in hosts)
                        Found[$"{address}:{tcpPort}"] = (address, tcpPort, name, Now);
                    foreach (var stale in Found.Where(f => Now - f.Value.seen > 6f).Select(f => f.Key).ToList())
                        Found.Remove(stale);
                    _scanning = false;
                    _nextScan = Now + 1.5f;
                });
            });
        }

        // ---------- drawing ----------

        public static void Draw(float scale)
        {
            if (!Open)
                return;
            EnsureStyles(scale);

            var width = 560f * scale;
            var line = 30f * scale;
            var pad = 18f * scale;
            var x = Screen.width - width - 40f * scale;
            var y0 = 110f * scale;

            // Measure first (simple vertical layout), then draw the background behind it.
            var rows = new List<Action<Rect>>();
            var heights = new List<float>();
            void Row(float h, Action<Rect> draw) { heights.Add(h); rows.Add(draw); }
            BuildRows(Row, line, scale);

            var height = pad * 2 + heights.Sum();
            GUI.Box(new Rect(x, y0, width, height), "", _box);

            var y = y0 + pad;
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i](new Rect(x + pad, y, width - pad * 2, heights[i]));
                y += heights[i];
            }
        }

        static void BuildRows(Action<float, Action<Rect>> row, float line, float scale)
        {
            row(line * 1.3f, r =>
            {
                GUI.Label(new Rect(r.x, r.y, r.width - 40 * scale, r.height), "PotionCoop · Birlikte Oyna", _title);
                if (GUI.Button(new Rect(r.xMax - 34 * scale, r.y, 34 * scale, 30 * scale), "×", _button))
                    Close();
            });

            row(line * 1.2f, r =>
            {
                GUI.Label(new Rect(r.x, r.y, 90 * scale, line), "İsmin:", _text);
                var edited = GUI.TextField(new Rect(r.x + 90 * scale, r.y, 260 * scale, line), _nameField ?? "", 24, _field);
                if (edited != _nameField)
                {
                    _nameField = edited;
                    Settings.SetName(edited);
                }
            });

            row(line * 1.6f, r => GUI.Label(r, StatusText(), _small));

            if (!Coop.IsRunning)
                BuildIdleRows(row, line, scale);
            else if (Coop.IsHost)
                BuildHostRows(row, line, scale);
            else
                BuildClientRows(row, line, scale);
        }

        static void BuildIdleRows(Action<float, Action<Rect>> row, float line, float scale)
        {
            row(line * 1.5f, r =>
            {
                if (GUI.Button(new Rect(r.x, r.y, 240 * scale, line * 1.25f), "HOST OL", _button))
                    Coop.StartHost();
                GUI.Label(new Rect(r.x + 255 * scale, r.y, r.width - 255 * scale, line * 1.25f),
                    "Senin dünyan ortak dünya olur.", _small);
            });

            row(line * 1.1f, r => GUI.Label(r, _scanning && Found.Count == 0 ? "Ağdaki oyunlar: aranıyor…" : "Ağdaki oyunlar:", _text));

            var blocker = Coop.JoinBlocker;
            if (Found.Count == 0)
            {
                var waited = _firstScanAt >= 0f && Now - _firstScanAt > 4f;
                row(line * (waited ? 2.4f : 1.1f), r => GUI.Label(r, waited
                    ? "Henüz bulunamadı. Arkadaşın HOST OL'a bastı mı? Aynı ağda mısınız?\nBulunamazsa host'un ekranındaki IP'yi aşağıya yaz."
                    : "…", _small));
            }
            foreach (var host in Found.Values.OrderBy(h => h.name))
            {
                var entry = host;
                row(line * 1.25f, r =>
                {
                    GUI.Label(new Rect(r.x, r.y, r.width - 150 * scale, line), $"• {entry.name}   ({entry.address})", _text);
                    GUI.enabled = blocker == null;
                    if (GUI.Button(new Rect(r.xMax - 140 * scale, r.y, 140 * scale, line), "KATIL", _button))
                        Coop.Join(entry.address, entry.port);
                    GUI.enabled = true;
                });
            }

            row(line * 1.5f, r =>
            {
                GUI.Label(new Rect(r.x, r.y + 4 * scale, 120 * scale, line), "IP ile bağlan:", _text);
                _manualAddress = GUI.TextField(new Rect(r.x + 125 * scale, r.y + 4 * scale, 200 * scale, line), _manualAddress ?? "", 40, _field);
                GUI.enabled = blocker == null;
                if (GUI.Button(new Rect(r.xMax - 140 * scale, r.y + 4 * scale, 140 * scale, line), "BAĞLAN", _button))
                {
                    if (IPAddress.TryParse(_manualAddress.Trim(), out var address))
                    {
                        Settings.SetHostAddress(_manualAddress.Trim());
                        Coop.Join(address, Settings.Port);
                    }
                    else
                        Coop.Join(); // empty or invalid: search the LAN once more
                }
                GUI.enabled = true;
            });

            if (blocker != null)
                row(line * 1.1f, r => GUI.Label(r, "Not: " + blocker + ".", _small));
        }

        static void BuildHostRows(Action<float, Action<Rect>> row, float line, float scale)
        {
            if (Now - _addressesAt > 5f)
            {
                _myAddresses = DiscoveryResponder.LocalLanAddresses();
                _addressesAt = Now;
            }
            row(line * 1.1f, r => GUI.Label(r, "Host'sun. Arkadaşın listede seni görmezse bu IP'yi yazsın:", _text));
            var addresses = _myAddresses.Count > 0 ? _myAddresses : new List<string> { "(ağ adresi bulunamadı)" };
            foreach (var address in addresses.Take(3))
                row(line * 1.1f, r => GUI.Label(r, $"    {address}", _title));
            row(line * 1.1f, r => GUI.Label(r, Coop.IsConnected ? $"Bağlı oyuncu: {Coop.RemoteName}" : "Oyuncu bekleniyor…", _text));
            row(line * 1.6f, r =>
            {
                if (GUI.Button(new Rect(r.x, r.y + 6 * scale, 240 * scale, line * 1.2f), "HOST'U KAPAT", _button))
                    Coop.Stop();
            });
        }

        static void BuildClientRows(Action<float, Action<Rect>> row, float line, float scale)
        {
            row(line * 1.1f, r => GUI.Label(r, Coop.IsConnected ? $"{Coop.RemoteName} adlı oyuncunun dünyasındasın." : "Bağlanılıyor…", _text));
            row(line * 1.6f, r =>
            {
                if (GUI.Button(new Rect(r.x, r.y + 6 * scale, 240 * scale, line * 1.2f), "BAĞLANTIYI KES", _button))
                    Coop.Stop();
            });
        }

        static string StatusText()
        {
            var text = "Durum: " + Coop.Status;
            if (Coop.IsConnected)
            {
                if (Coop.PingMs >= 0)
                    text += $"  ·  ping {Coop.PingMs:F0} ms";
                text += Coop.InSync switch
                {
                    true => "  ·  senkron ✓",
                    false => "  ·  senkronlanıyor…",
                    _ => "",
                };
            }
            return text;
        }

        // ---------- small button while paused ----------

        /// <summary>While paused (cursor free) a small CO-OP button opens the window.</summary>
        public static void DrawPauseButton(float scale)
        {
            if (Open || GameRefs.State != eGameState.Paused)
                return;
            EnsureStyles(scale);
            var rect = new Rect(Screen.width - 170 * scale, 50 * scale, 140 * scale, 38 * scale);
            if (GUI.Button(rect, "CO-OP", _button))
                Show();
        }

        // ---------- styles ----------

        static void EnsureStyles(float scale)
        {
            if (_box != null && Mathf.Approximately(_styledFor, scale) && _box.normal.background != null)
                return;
            _styledFor = scale;

            _box = new GUIStyle(GUI.skin.box);
            _box.normal.background = Solid(new Color(0.07f, 0.055f, 0.045f, 0.93f));

            _title = Label(Gold, 20, scale, FontStyle.Bold);
            _text = Label(Soft, 16, scale, FontStyle.Normal);
            _small = Label(Dim, 14, scale, FontStyle.Normal);
            _small.wordWrap = true;

            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(16 * scale),
                fontStyle = FontStyle.Bold,
            };
            _button.normal.background = Solid(new Color(0.30f, 0.22f, 0.12f, 0.95f));
            _button.hover.background = Solid(new Color(0.45f, 0.33f, 0.16f, 1f));
            _button.active.background = Solid(new Color(0.55f, 0.40f, 0.18f, 1f));
            _button.normal.textColor = Gold;
            _button.hover.textColor = Color.white;
            _button.active.textColor = Color.white;

            _field = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(16 * scale) };
            _field.normal.background = Solid(new Color(0.15f, 0.12f, 0.10f, 1f));
            _field.focused.background = Solid(new Color(0.20f, 0.16f, 0.12f, 1f));
            _field.normal.textColor = Color.white;
            _field.focused.textColor = Color.white;
        }

        static GUIStyle Label(Color color, int size, float scale, FontStyle style)
        {
            var label = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(size * scale),
                fontStyle = style,
                alignment = TextAnchor.MiddleLeft,
            };
            label.normal.textColor = color;
            return label;
        }

        static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            // Survive scene changes (the game unloads unused assets between menu and lab).
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }
    }
}
