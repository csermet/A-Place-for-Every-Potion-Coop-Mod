using System;
using Il2Cpp;
using Il2CppCodeBase.Components.UI;
using Il2CppTMPro;
using PotionCoop.Game;
using UnityEngine;
using UnityEngine.UI;

namespace PotionCoop.UI
{
    /// <summary>
    /// Adds a "Co-op" entry next to the Settings entry of the game's menus (main menu, pause menu). It is a copy of
    /// that entry, so it has the game's own highlight band, text style and sounds; pressing it opens the co-op window
    /// (see <see cref="MenuButtonClickPatch"/>).
    /// </summary>
    internal static class MenuButton
    {
        public const string ObjectName = "PotionCoopButton";

        static float _nextScan;
        static bool _reportedFailure;

        public static void Tick()
        {
            if (GameRefs.State is not (eGameState.Lobby or eGameState.Paused) || Time.realtimeSinceStartup < _nextScan)
                return;
            _nextScan = Time.realtimeSinceStartup + 0.2f;

            var canvas = GameObject.Find(UiClone.CanvasPath);
            if (canvas == null)
                return;
            foreach (var opener in canvas.GetComponentsInChildren<AdvancedButtonWindowOpener>(false))
            {
                if (opener._windowID != eUIWindow.Settings)
                    continue;
                UiClone.Remember(opener);
                var parent = opener.transform.parent;
                // Only lists laid out by the game; a free-placed button would cover another one.
                if (parent == null || parent.Find(ObjectName) != null || parent.GetComponent<LayoutGroup>() == null)
                    continue;
                AddEntry(opener.gameObject);
            }
            if (GameRefs.State == eGameState.Lobby)
                UiClone.EnsureButtonTemplate();
        }

        static void AddEntry(GameObject settingsEntry)
        {
            try
            {
                var entry = UiClone.CloneInactive(settingsEntry, settingsEntry.transform.parent);
                entry.name = ObjectName;
                entry.transform.SetSiblingIndex(settingsEntry.transform.GetSiblingIndex());
                foreach (var text in entry.GetComponentsInChildren<TMP_Text>(true))
                    text.text = "Co-op";
                entry.SetActive(true);
                MakeRoom(settingsEntry);
                CoopMod.Log.Msg($"Co-op entry added to {Describe(settingsEntry.transform)}");
            }
            catch (Exception e)
            {
                if (_reportedFailure)
                    return;
                _reportedFailure = true;
                CoopMod.Log.Error($"could not add the Co-op menu entry: {e}");
            }
        }

        /// <summary>
        /// In a framed window (the pause menu) the list sits in a panel of fixed height: grow the list and the panel
        /// by one entry, or the last entry (Quit to Menu) ends up below the frame. The main menu has no panel.
        /// </summary>
        static void MakeRoom(GameObject settingsEntry)
        {
            var list = settingsEntry.transform.parent;
            Transform panel = null;
            for (var t = list; t != null && t.GetComponent<Canvas>() == null; t = t.parent)
                if (t.name == "Panel")
                {
                    panel = t;
                    break;
                }
            if (panel == null)
                return;
            var layout = list.GetComponent<HorizontalOrVerticalLayoutGroup>();
            var height = settingsEntry.GetComponent<RectTransform>().rect.height + (layout != null ? layout.spacing : 0f);
            if (height <= 1f)
                return;
            for (var t = list; t != null; t = t.parent)
            {
                var rect = t.TryCast<RectTransform>();
                // Only boxes of fixed height: stretched ones follow their parent, fitted ones size themselves.
                if (rect != null && Mathf.Approximately(rect.anchorMin.y, rect.anchorMax.y) && t.GetComponent<ContentSizeFitter>() == null)
                    rect.sizeDelta += new Vector2(0f, height);
                if (t == panel)
                    break;
            }
        }

        static string Describe(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && p.name != "WindowsContainerCanvas"; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }
    }
}
