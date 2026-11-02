using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Il2Cpp;
using Il2CppCodeBase.Components.UI;
using Il2CppTMPro;
using PotionCoop.Game;
using PotionCoop.Net;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PotionCoop.UI
{
    /// <summary>
    /// The co-op window. It is the game's own Stats window (frame, title bar, close button, dimmer, open/close animation,
    /// Esc to close), opened through the game's window service, with its rows replaced by ours: host a game, see who is
    /// hosting on the home network and join them, or type an address. Rows are copies of the game's rows and buttons.
    /// </summary>
    internal static class CoopPanel
    {
        static readonly Color Gold = Hex(0xC9A24B);
        static readonly Color LabelColor = Hex(0xE2D3AF);
        static readonly Color ValueColor = Hex(0xF0E2BD);
        static readonly Color Muted = Hex(0x9A8567);
        static readonly Color OnPrimary = Hex(0x211910);

        static UIWindowFacade _window;
        static Transform _list;
        static RectTransform _panel;
        static float _contentHeight;
        static GameObject _rowTemplate;
        static readonly List<GameObject> Built = new();
        static string _layout;
        static TMP_FontAsset _serif, _mono, _caps;
        static TMP_Text _status;

        // hosts seen on the LAN: "ip:port" -> (address, port, name, last seen)
        static readonly Dictionary<string, (IPAddress address, int port, string name, float seen)> Found = new();
        static bool _scanning;
        static float _nextScan;
        static float _firstScanAt = -1f;
        static List<string> _myAddresses = new();
        static float _addressesAt = -100f;

        static readonly TextBox NameBox = new("", 24)
        {
            Placeholder = "your name",
            Committed = name =>
            {
                Settings.SetName(name.Trim());
                Settings.Save();
            },
        };

        static readonly TextBox AddressBox = new("", 40, c => char.IsDigit(c) || c == '.' || c == ':')
        {
            Placeholder = "192.168.1.20",
        };

        static float Now => Time.realtimeSinceStartup;

        public static bool IsOpen => _window != null;

        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Show();
        }

        public static void Show()
        {
            if (IsOpen)
                return;
            var ui = UiClone.UiService;
            if (ui == null || UiClone.ButtonTemplate == null)
            {
                CoopMod.Log.Warning("co-op window: the game's menu is not ready yet");
                return;
            }
            NameBox.Text = Settings.RawName ?? "";
            AddressBox.Text = Settings.HostAddress ?? "";
            _firstScanAt = -1f;
            _nextScan = 0f;
            try
            {
                _window = ui.OpenWindowSync(eUIWindow.Stats, false, false);
                if (_window == null)
                    throw new InvalidOperationException("the Stats window did not open");
                Dress();
                _layout = null;
                Refresh();
            }
            catch (Exception e)
            {
                CoopMod.Log.Error($"co-op window could not be built: {e}");
                if (_window != null)
                    _window.Close(true, true, null);
                _window = null;
            }
        }

        public static void Close()
        {
            if (_window == null)
                return;
            var window = _window;
            Forget();
            window.Close(false, true, null);
        }

        /// <summary>The window is gone or going: commit what was typed and drop our references.</summary>
        static void Forget()
        {
            TextBox.Unfocus();
            foreach (var go in Built)
                UiClone.Forget(go);
            Built.Clear();
            NameBox.Label = AddressBox.Label = null;
            NameBox.Frame = AddressBox.Frame = null;
            _window = null;
            _status = null;
            Settings.Save();
        }

        /// <summary>Per frame: follow the window's life, keep its rows current, scan the LAN while not connected.</summary>
        public static void Tick()
        {
            TextBox.Tick();
            if (_window == null)
            {
                if (_status != null || Built.Count > 0)
                    Forget(); // destroyed by the game (X, Esc, dimmer, game state change)
                return;
            }

            try { Refresh(); }
            catch (Exception e)
            {
                CoopMod.Log.Error($"co-op window: {e}");
                Close();
                return;
            }
            Scan();
        }

        // ---------- the game's window, re-dressed ----------

        static void Dress()
        {
            var panel = _window.transform.Find("Content/Panel");
            _panel = panel.GetComponent<RectTransform>();
            var titleBar = panel.Find("TitleBar");
            var title = titleBar.Find("TitleText").GetComponent<TMP_Text>();
            UiClone.Strip(title.gameObject, "LocalizedTextUpdater");
            title.text = "CO-OP";

            _list = panel.Find("Body/List/Viewport/Content");
            UiClone.Strip(_list.gameObject, "AllTimeTotalRowRevealer", "AllTimeTotalUpdater", "LocalizedTextUpdater", "FontRoleApplier");

            var titleIcon = titleBar.Find("Icon")?.GetComponent<Image>();
            var shelvedIcon = _list.Find("Row_PotionsShelved/Icon")?.GetComponent<Image>();
            if (titleIcon != null && shelvedIcon != null)
                titleIcon.sprite = shelvedIcon.sprite;

            // The first stats row is the pattern for ours; the others go.
            _rowTemplate = _list.GetChild(0).gameObject;
            _rowTemplate.SetActive(false);
            _rowTemplate.name = "CoopRowTemplate";
            for (var i = _list.childCount - 1; i >= 1; i--)
            {
                var child = _list.GetChild(i).gameObject;
                child.SetActive(false);
                Object.Destroy(child);
            }

            _serif = _rowTemplate.transform.Find("Label").GetComponent<TMP_Text>().font;
            _mono = _rowTemplate.transform.Find("Value").GetComponent<TMP_Text>().font;
            _caps = UiClone.ButtonTemplate.GetComponentInChildren<TMP_Text>(true)?.font ?? title.font;

            // Player names may have letters the body fonts lack (ş, ğ, ı…); Cinzel has them.
            AddFallback(_serif, _caps);
            AddFallback(_mono, _caps);
        }

        static bool _checkedGlyphs;

        static void AddFallback(TMP_FontAsset font, TMP_FontAsset fallback)
        {
            if (font == null || fallback == null || font == fallback)
                return;
            var table = font.fallbackFontAssetTable;
            if (table == null)
            {
                table = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
                font.fallbackFontAssetTable = table;
            }
            if (!table.Contains(fallback))
                table.Add(fallback);

            if (_checkedGlyphs)
                return;
            _checkedGlyphs = true;
            var missing = "şğıİüöçŞĞÜÖÇ".Where(c => !fallback.HasCharacter(c, true, true)).ToArray();
            if (missing.Length > 0)
                CoopMod.Log.Warning($"co-op window: {fallback.name} lacks {new string(missing)}");
        }

        static void Refresh()
        {
            if (Now - _addressesAt > 5f)
            {
                _myAddresses = DiscoveryResponder.LocalLanAddresses();
                _addressesAt = Now;
            }
            var layout = LayoutKey();
            if (layout != _layout)
            {
                _layout = layout;
                Rebuild();
            }
            if (_status != null)
                _status.text = StatusText();
            NameBox.Render();
            AddressBox.Render();
        }

        static bool WaitedForHosts => _firstScanAt >= 0f && Now - _firstScanAt > 4f;

        static string LayoutKey()
        {
            if (!Coop.IsRunning)
                return $"idle|{string.Join(",", Found.Keys.OrderBy(k => k))}|{Found.Count == 0 && WaitedForHosts}|{Coop.JoinBlocker}|{Coop.Status != Coop.StatusOff}";
            if (Coop.IsHost)
                return $"host|{string.Join(",", _myAddresses)}|{Coop.IsConnected}";
            return $"client|{Coop.IsConnected}";
        }

        static void Rebuild()
        {
            TextBox.Unfocus();
            foreach (var go in Built)
            {
                UiClone.Forget(go);
                if (go != null)
                {
                    go.SetActive(false);
                    Object.Destroy(go);
                }
            }
            Built.Clear();
            _status = null;
            _contentHeight = 0f;

            if (!Coop.IsRunning)
                BuildIdle();
            else if (Coop.IsHost)
                BuildHost();
            else
                BuildClient();

            // The stats window is 720 high; fit it to our rows (title bar and margins take 150).
            if (_panel != null)
                _panel.sizeDelta = new Vector2(_panel.sizeDelta.x, Mathf.Clamp(_contentHeight + 170f, 420f, 900f));
        }

        static void BuildIdle()
        {
            Note("Work in the same lab with a friend on your home network.");

            var name = Row("Your name");
            Box(name, NameBox, 340f, 4f, mono: false);

            if (Coop.Status != Coop.StatusOff)
                StatusRow();

            Header("Host a game");
            var host = Row("Your lab becomes the shared world");
            host.Label.rectTransform.sizeDelta = new Vector2(470f, 40f);
            Button(host, "HOST", Coop.StartHost, primary: true, width: 220f, right: 4f);

            Header("Games on your network");
            var blocker = Coop.JoinBlocker;
            if (Found.Count == 0)
                Note(WaitedForHosts
                    ? "None found yet. Did your friend press HOST, and are you on the same network? If not, type the IP shown on their screen below."
                    : "Searching…");
            foreach (var entry in Found.Values.OrderBy(h => h.name))
            {
                var found = entry;
                var row = Row(found.name);
                row.Label.rectTransform.sizeDelta = new Vector2(300f, 40f);
                Value(row, found.address.ToString(), mono: true, width: 220f, right: 240f);
                Button(row, "JOIN", () => Coop.Join(found.address, found.port), primary: true, width: 200f, right: 4f);
            }

            var manual = Row("Join by IP");
            manual.Label.rectTransform.sizeDelta = new Vector2(220f, 40f);
            Box(manual, AddressBox, 270f, 224f, mono: true);
            Button(manual, "CONNECT", JoinTyped, primary: false, width: 200f, right: 4f);

            if (blocker != null)
                Note("Note: " + blocker + ".");
        }

        static void BuildHost()
        {
            Note("You are hosting: your save is the shared world. Your friend finds you under Co-op › Games on your network.");
            StatusRow();
            var addresses = _myAddresses.Count > 0 ? _myAddresses : new List<string> { "not found" };
            foreach (var address in addresses.Take(3))
                Value(Row("Your IP"), address, mono: true, width: 300f, right: 4f);
            if (!Coop.IsConnected)
                Note("If they don't see you in the list, they can type this address under “Join by IP”.");
            var stop = Row("Co-op session");
            Button(stop, "STOP HOSTING", Coop.Stop, primary: false, width: 240f, right: 4f);
        }

        static void BuildClient()
        {
            Note(Coop.IsConnected
                ? $"You are in {Coop.RemoteName}'s world. Your XP and skills stay your own."
                : "Connecting to the host…");
            StatusRow();
            var stop = Row("Co-op session");
            Button(stop, "DISCONNECT", Coop.Stop, primary: false, width: 260f, right: 4f);
        }

        static void JoinTyped()
        {
            var typed = AddressBox.Text.Trim();
            if (IPAddress.TryParse(typed, out var address))
            {
                Settings.SetHostAddress(typed);
                Coop.Join(address, Settings.Port);
            }
            else
                Coop.Join(); // empty or invalid: search the LAN once more
        }

        static string StatusText()
        {
            var text = Coop.Status;
            if (Coop.IsConnected)
            {
                if (Coop.PingMs >= 0)
                    text += $"  ·  {Coop.PingMs:F0} ms";
                text += Coop.InSync switch
                {
                    true => "  ·  in sync ✓",
                    false => "  ·  syncing…",
                    _ => "",
                };
            }
            return text.Length > 0 ? char.ToUpper(text[0]) + text.Substring(1) : text;
        }

        // ---------- rows made from the stats row ----------

        sealed class RowParts
        {
            public GameObject Root;
            public TMP_Text Label;
            public TMP_Text Value;
        }

        static RowParts Row(string label, float height = 74f, bool divider = true)
        {
            var go = Object.Instantiate(_rowTemplate, _list, false);
            go.name = "CoopRow";
            var element = go.GetComponent<LayoutElement>();
            if (element != null)
            {
                element.minHeight = height;
                element.preferredHeight = height;
            }
            go.transform.Find("Icon")?.gameObject.SetActive(false);
            go.transform.Find("Divider")?.gameObject.SetActive(divider);
            var parts = new RowParts
            {
                Root = go,
                Label = go.transform.Find("Label").GetComponent<TMP_Text>(),
                Value = go.transform.Find("Value").GetComponent<TMP_Text>(),
            };
            parts.Label.rectTransform.anchoredPosition = new Vector2(6f, 1f);
            parts.Label.richText = false;
            parts.Label.text = label;
            parts.Value.gameObject.SetActive(false);
            go.SetActive(true);
            Built.Add(go);
            _contentHeight += height;
            return parts;
        }

        static void Value(RowParts row, string text, bool mono, float width, float right)
        {
            var value = row.Value;
            value.gameObject.SetActive(true);
            value.richText = false;
            value.font = mono ? _mono : _serif;
            value.fontSize = mono ? 24f : 24f;
            value.color = ValueColor;
            value.rectTransform.sizeDelta = new Vector2(width, 40f);
            value.rectTransform.anchoredPosition = new Vector2(-right, 1f);
            value.text = text;
        }

        /// <summary>A gold small-caps group title, like "BASIC SETTINGS" in the game's settings.</summary>
        static void Header(string text)
        {
            var row = Row(text.ToUpperInvariant(), 58f, divider: false);
            var label = row.Label;
            label.font = _caps;
            label.fontSize = 20f;
            label.color = Gold;
            label.characterSpacing = 5f;
            label.alignment = TextAlignmentOptions.BottomLeft;
            Stretch(label.rectTransform, 6f, 6f, bottom: 8f);
        }

        /// <summary>A muted italic line, like the game's window subtitles.</summary>
        static void Note(string text)
        {
            var row = Row(text, 62f, divider: false);
            var label = row.Label;
            label.fontStyle = FontStyles.Italic;
            label.fontSize = 22f;
            label.color = Muted;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(label.rectTransform, 6f, 6f);
        }

        static void StatusRow()
        {
            var row = Row("Status");
            row.Label.rectTransform.sizeDelta = new Vector2(150f, 40f);
            var value = row.Value;
            value.gameObject.SetActive(true);
            value.richText = false;
            value.font = _serif;
            value.fontSize = 23f;
            value.color = ValueColor;
            value.alignment = TextAlignmentOptions.MidlineLeft;
            value.textWrappingMode = TextWrappingModes.Normal;
            var rect = value.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(160f, 4f);
            rect.offsetMax = new Vector2(-4f, -4f);
            _status = value;
        }

        // ---------- buttons and boxes made from the game's framed button ----------

        static GameObject Button(RowParts row, string text, Action click, bool primary, float width, float right)
        {
            var button = UiClone.CloneInactive(UiClone.ButtonTemplate, row.Root.transform);
            button.name = "CoopButton";
            Place(button, width, 54f, right);
            button.transform.Find("Icon")?.gameObject.SetActive(false);

            var label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                Stretch(label.rectTransform, 14f, 14f);
                label.enableAutoSizing = true;
                label.fontSizeMin = 14f;
                label.fontSizeMax = 22f;
                label.characterSpacing = 3f;
                label.text = text;
            }
            var bg = button.transform.Find("Bg")?.GetComponent<Image>();
            var primarySprite = primary ? UiClone.FindSprite("btn-primary") : null;
            if (primarySprite != null && bg != null && bg.gameObject.activeSelf)
            {
                bg.sprite = primarySprite;
                if (label != null)
                    label.color = OnPrimary;
            }

            UiClone.OnClick(button, click);
            button.SetActive(true);
            Built.Add(button);
            return button;
        }

        static void Box(RowParts row, TextBox box, float width, float right, bool mono)
        {
            var field = UiClone.CloneInactive(UiClone.ButtonTemplate, row.Root.transform);
            field.name = "CoopTextBox";
            // Its colours are set per frame (focus), the press colour/scale would fight that.
            UiClone.Strip(field, "AdvancedButtonColor", "AdvancedButtonScale", "FontRoleApplier");
            Place(field, width, 50f, right);
            field.transform.Find("Icon")?.gameObject.SetActive(false);
            // A dark well instead of the button face (it stays the click target).
            var bg = field.transform.Find("Bg")?.GetComponent<Image>();
            if (bg != null)
                bg.color = new Color(0f, 0f, 0f, 0.35f);

            var label = field.GetComponentInChildren<TMP_Text>(true);
            Stretch(label.rectTransform, 16f, 16f);
            label.enableAutoSizing = false;
            // Names in the game's small-caps face (it has every Turkish letter), addresses in the mono face.
            label.font = mono ? _mono : _caps;
            label.fontSize = mono ? 23f : 24f;
            label.characterSpacing = 0f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.richText = false;

            box.Label = label;
            box.Frame = field.transform.Find("Border")?.GetComponent<Image>();
            box.Render();
            UiClone.OnClick(field, box.Focus);
            field.SetActive(true);
            Built.Add(field);
        }

        static void Place(GameObject widget, float width, float height, float right)
        {
            var rect = widget.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(-right, 0f);
        }

        static void Stretch(RectTransform rect, float left, float right, float bottom = 0f, float top = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        // ---------- LAN scan ----------

        static void Scan()
        {
            if (Coop.IsRunning || _scanning || Now < _nextScan)
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
    }
}
