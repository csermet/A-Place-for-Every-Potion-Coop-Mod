using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PotionCoop.UI
{
    /// <summary>
    /// A minimal text box for the IMGUI window. The game's build stripped IMGUI's own text field, so typed characters
    /// come from the Input System keyboard (onTextInput) and the box only draws them with a caret.
    /// </summary>
    internal sealed class TextBox
    {
        static TextBox _focused;
        static readonly HashSet<int> HookedKeyboards = new();

        readonly int _maxLength;
        readonly Func<char, bool> _accept;
        public string Text;

        public TextBox(string text, int maxLength, Func<char, bool> accept = null)
        {
            Text = text ?? "";
            _maxLength = maxLength;
            _accept = accept;
        }

        public bool Focused => _focused == this;

        public static void Unfocus() => _focused = null;

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
            if (keyboard.backspaceKey.wasPressedThisFrame && box.Text.Length > 0)
                box.Text = box.Text.Substring(0, box.Text.Length - 1);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)
                _focused = null;
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

        public void Draw(Rect rect, GUIStyle background, GUIStyle text, float scale)
        {
            // The whole box is a button: clicking it gives it the keyboard.
            if (GUI.Button(rect, "", background))
                _focused = this;
            var caret = Focused && Time.realtimeSinceStartup % 1f < 0.55f ? "|" : "";
            var shown = Text.Length == 0 && !Focused ? " " : Text + caret;
            GUI.Label(new Rect(rect.x + 8 * scale, rect.y, rect.width - 16 * scale, rect.height), shown, text);
        }
    }
}
