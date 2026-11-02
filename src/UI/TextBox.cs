using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PotionCoop.UI
{
    /// <summary>
    /// A text box drawn with the game's widgets (a framed button showing the text). The game has no text input of its
    /// own, so typed characters come from the Input System keyboard (onTextInput). Click to type, Enter to finish.
    /// </summary>
    internal sealed class TextBox
    {
        static TextBox _focused;
        static readonly HashSet<int> HookedKeyboards = new();

        static readonly Color TextColor = new(0.94f, 0.886f, 0.741f);   // F0E2BD, like the game's values
        static readonly Color PlaceholderColor = new(0.42f, 0.34f, 0.21f);
        static readonly Color FrameIdle = new(0.42f, 0.34f, 0.21f);     // 6A5636
        static readonly Color FrameFocused = new(0.91f, 0.70f, 0.31f);  // E8B24E

        readonly int _maxLength;
        readonly Func<char, bool> _accept;
        public string Text;
        public string Placeholder = "";

        /// <summary>Called when typing ends (Enter, Escape, clicking elsewhere, window closed).</summary>
        public Action<string> Committed;

        // The widgets showing this box; set by the window that builds them.
        public TMP_Text Label;
        public Image Frame;

        public TextBox(string text, int maxLength, Func<char, bool> accept = null)
        {
            Text = text ?? "";
            _maxLength = maxLength;
            _accept = accept;
        }

        public bool Focused => _focused == this;

        public void Focus()
        {
            if (_focused == this)
                return;
            Unfocus();
            _focused = this;
            // Typed letters must not move the menu selection or press the selected button.
            EventSystem.current?.SetSelectedGameObject(null);
        }

        public static void Unfocus()
        {
            var box = _focused;
            _focused = null;
            box?.Committed?.Invoke(box.Text);
        }

        /// <summary>Per frame: hook the active keyboard once, and handle the keys that are not characters.</summary>
        public static void Tick()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;
            if (HookedKeyboards.Add(keyboard.deviceId))
                keyboard.add_onTextInput(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(new Action<char>(OnCharacter)));

            var box = _focused;
            if (box == null)
                return;
            if (box.Label == null)
            {
                _focused = null; // its window is gone
                return;
            }
            if (keyboard.backspaceKey.wasPressedThisFrame && box.Text.Length > 0)
                box.Text = box.Text.Substring(0, box.Text.Length - 1);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)
                Unfocus();
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && !box.IsPointerOver())
                Unfocus();
        }

        static void OnCharacter(char c)
        {
            var box = _focused;
            if (box == null || char.IsControl(c) || box.Text.Length >= box._maxLength)
                return;
            if (box._accept != null && !box._accept(c))
                return;
            box.Text += c;
        }

        bool IsPointerOver()
        {
            if (Frame == null || Mouse.current == null)
                return false;
            var rect = Frame.rectTransform;
            var canvas = Frame.canvas;
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            return RectTransformUtility.RectangleContainsScreenPoint(rect, Mouse.current.position.ReadValue(), camera);
        }

        /// <summary>Shows the current text with a blinking caret.</summary>
        public void Render()
        {
            if (Label == null)
                return;
            var focused = Focused;
            if (Text.Length == 0 && !focused)
            {
                Label.text = Placeholder;
                Label.color = PlaceholderColor;
            }
            else
            {
                Label.text = focused && Time.realtimeSinceStartup % 1f < 0.55f ? Text + "|" : Text;
                Label.color = TextColor;
            }
            if (Frame != null)
                Frame.color = focused ? FrameFocused : FrameIdle;
        }
    }
}
