using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using PotionCoop.Game;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PotionCoop.UI
{
    /// <summary>
    /// Adds a "CO-OP" entry to the game's main menu, styled like the entry next to it (same font, size and colour).
    /// It is our own uGUI button rather than a copy of the game's, so no game scripts run on it.
    /// </summary>
    internal static class MenuButton
    {
        const string MenuItemsPath = "UISetup(Clone)/WindowsContainerCanvas/MainMenu(Clone)/Content/MenuItems";
        const string TemplateName = "OptionsButton";

        static GameObject _button;
        static float _nextTry;
        static bool _loggedMissing;

        public static void Tick()
        {
            if (GameRefs.State != eGameState.Lobby || _button != null || Time.realtimeSinceStartup < _nextTry)
                return;
            _nextTry = Time.realtimeSinceStartup + 0.5f;

            var items = GameObject.Find(MenuItemsPath);
            var template = items != null ? items.transform.Find(TemplateName) : null;
            if (template == null)
            {
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    CoopMod.Log.Msg("main menu entries not found yet (CO-OP button waits)");
                }
                return;
            }

            try
            {
                Create(template.gameObject);
            }
            catch (Exception e)
            {
                CoopMod.Log.Error($"could not add the CO-OP menu entry: {e}");
                _nextTry = float.MaxValue; // don't retry every half second; the hotkeys still work
            }
        }

        static void Create(GameObject template)
        {
            var source = template.GetComponentInChildren<TMP_Text>(true);
            var templateRect = template.GetComponent<RectTransform>();

            _button = new GameObject("PotionCoopButton");
            var rect = _button.AddComponent<RectTransform>();
            rect.SetParent(template.transform.parent, false);
            rect.SetSiblingIndex(template.transform.GetSiblingIndex());
            rect.anchorMin = templateRect.anchorMin;
            rect.anchorMax = templateRect.anchorMax;
            rect.pivot = templateRect.pivot;
            rect.sizeDelta = templateRect.sizeDelta;

            // Keep the same slot size inside the menu's layout group.
            var layout = _button.AddComponent<LayoutElement>();
            layout.preferredWidth = templateRect.rect.width;
            layout.preferredHeight = templateRect.rect.height;

            // Invisible hit area so the whole row is clickable.
            var hit = _button.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            var labelObject = new GameObject("Label");
            var labelRect = labelObject.AddComponent<RectTransform>();
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            if (source != null)
            {
                // Match the label's own placement inside the template row.
                var sourceRect = source.rectTransform;
                labelRect.anchorMin = sourceRect.anchorMin;
                labelRect.anchorMax = sourceRect.anchorMax;
                labelRect.pivot = sourceRect.pivot;
                labelRect.anchoredPosition = sourceRect.anchoredPosition;
                labelRect.sizeDelta = sourceRect.sizeDelta;
            }

            var label = labelObject.AddComponent<TextMeshProUGUI>();
            if (source != null)
            {
                label.font = source.font;
                label.fontSharedMaterial = source.fontSharedMaterial;
                label.fontSize = source.fontSize;
                label.fontStyle = source.fontStyle;
                label.alignment = source.alignment;
                label.characterSpacing = source.characterSpacing;
                label.color = source.color;
            }
            label.enableAutoSizing = false;
            label.raycastTarget = false;
            label.text = "CO-OP";

            var button = _button.AddComponent<Button>();
            button.targetGraphic = label;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.86f, 0.55f);
            colors.selectedColor = Color.white;
            colors.pressedColor = new Color(0.85f, 0.7f, 0.4f);
            button.colors = colors;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(CoopPanel.Toggle)));

            CoopMod.Log.Msg("CO-OP entry added to the main menu");
        }
    }
}
