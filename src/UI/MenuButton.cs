using System;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using Il2CppCodeBase.Components.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTMPro;
using PotionCoop.Game;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PotionCoop.UI
{
    /// <summary>
    /// Adds a "Co-op" entry to the game's main menu. It is a copy of the Settings entry, so it has the game's own
    /// highlight band, text style and sounds; only its action is replaced (see <see cref="MenuButtonClickPatch"/>).
    /// If copying fails, a plain look-alike button is used instead.
    /// </summary>
    internal static class MenuButton
    {
        public const string ObjectName = "PotionCoopButton";
        const string MenuItemsPath = "UISetup(Clone)/WindowsContainerCanvas/MainMenu(Clone)/Content/MenuItems";
        const string TemplateName = "OptionsButton";

        // The template's own action components; the copy must not open Settings or start a game.
        static readonly string[] ActionComponents =
        {
            "AdvancedButtonWindowOpener", "AdvancedButtonGameStateSwitcher", "AdvancedButtonContinueStarter",
            "AdvancedButtonNewGameStarter", "AdvancedButtonGameQuitter", "AdvancedButtonWindowCloser",
            "AdvancedButtonSteamStoreOpener", "AdvancedButtonSupportBundleExporter", "AdvancedButtonLabKeeper",
            "AdvancedButtonLabRemaker", "AdvancedButtonAccessibilitySettingsRequester", "AdvancedButtonPourModeWriter",
            "LocalizedTextUpdater",
        };

        static GameObject _button;
        static float _nextTry;
        static bool _loggedMissing;
        static bool _cloneFailed;

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

            if (!_cloneFailed)
            {
                try
                {
                    _button = CloneEntry(template.gameObject);
                    CoopMod.Log.Msg("Co-op entry added to the main menu (copy of Settings)");
                    return;
                }
                catch (Exception e)
                {
                    _cloneFailed = true;
                    CoopMod.Log.Warning($"copying the Settings entry failed, using a plain button: {e.Message}");
                    var leftover = template.parent.Find(ObjectName);
                    if (leftover != null)
                        Object.Destroy(leftover.gameObject);
                }
            }

            try
            {
                _button = PlainEntry(template.gameObject);
                CoopMod.Log.Msg("Co-op entry added to the main menu (plain)");
            }
            catch (Exception e)
            {
                CoopMod.Log.Error($"could not add the Co-op menu entry: {e}");
                _nextTry = float.MaxValue; // the hotkeys still work
            }
        }

        /// <summary>The game's Settings entry, copied while inactive so nothing runs before we rewire it.</summary>
        static GameObject CloneEntry(GameObject template)
        {
            var wasActive = template.activeSelf;
            template.SetActive(false);
            GameObject clone;
            try
            {
                clone = Object.Instantiate(template, template.transform.parent);
            }
            finally
            {
                template.SetActive(wasActive);
            }
            clone.name = ObjectName;
            clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex());

            foreach (var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetIl2CppType();
                if (type != null && ActionComponents.Contains(type.Name))
                    Object.DestroyImmediate(behaviour);
            }

            CopyInjectedServices(template, clone);

            foreach (var text in clone.GetComponentsInChildren<TMP_Text>(true))
                text.text = "Co-op";

            clone.SetActive(true);
            return clone;
        }

        /// <summary>
        /// The game injects services (input, sounds…) into its UI components at load; a copy would have them null.
        /// Copy every interface-typed field that is set on the template's component and empty on the copy's.
        /// </summary>
        static void CopyInjectedServices(GameObject template, GameObject clone)
        {
            var sources = template.GetComponentsInChildren<MonoBehaviour>(true);
            var targets = clone.GetComponentsInChildren<MonoBehaviour>(true);
            var copied = 0;
            foreach (var target in targets)
            {
                var targetType = target.GetIl2CppType();
                var source = sources.FirstOrDefault(s => s.GetIl2CppType()?.FullName == targetType?.FullName && s.gameObject.name == target.gameObject.name);
                if (source == null)
                    continue;
                var managedType = ManagedTypeOf(targetType);
                if (managedType == null)
                    continue;
                var typed = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!;
                var castMethod = typed.MakeGenericMethod(managedType);
                object sourceTyped, targetTyped;
                try
                {
                    sourceTyped = castMethod.Invoke(source, null);
                    targetTyped = castMethod.Invoke(target, null);
                }
                catch (Exception)
                {
                    continue;
                }
                for (var t = managedType; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var kind = property.PropertyType;
                        var looksLikeService = property.Name.StartsWith("_") && kind.Name.Length > 1 && kind.Name[0] == 'I' && char.IsUpper(kind.Name[1])
                                               && typeof(Il2CppObjectBase).IsAssignableFrom(kind) && !typeof(Object).IsAssignableFrom(kind);
                        if (!looksLikeService || !property.CanRead || !property.CanWrite)
                            continue;
                        if (property.GetValue(targetTyped) != null)
                            continue;
                        var value = property.GetValue(sourceTyped);
                        if (value == null)
                            continue;
                        property.SetValue(targetTyped, value);
                        copied++;
                    }
                }
            }
            CoopMod.Log.Msg($"Co-op entry: {copied} injected service(s) copied from Settings");
        }

        static readonly System.Collections.Generic.Dictionary<string, Type> ManagedTypes = new();

        /// <summary>The interop wrapper type for an Il2Cpp type ("CodeBase.X" lives in managed "Il2CppCodeBase.X").</summary>
        static Type ManagedTypeOf(Il2CppSystem.Type il2cppType)
        {
            if (il2cppType == null)
                return null;
            var name = il2cppType.FullName;
            if (ManagedTypes.TryGetValue(name, out var cached))
                return cached;
            Type found = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType("Il2Cpp" + name, false) ?? assembly.GetType(name, false) ?? assembly.GetType("Il2Cpp." + name, false);
                if (found != null && typeof(Il2CppObjectBase).IsAssignableFrom(found))
                    break;
                found = null;
            }
            ManagedTypes[name] = found;
            return found;
        }

        /// <summary>Fallback: our own uGUI button with the template's font, size and colour.</summary>
        static GameObject PlainEntry(GameObject template)
        {
            var source = template.GetComponentInChildren<TMP_Text>(true);
            var templateRect = template.GetComponent<RectTransform>();

            var go = new GameObject(ObjectName);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(template.transform.parent, false);
            rect.SetSiblingIndex(template.transform.GetSiblingIndex());
            rect.anchorMin = templateRect.anchorMin;
            rect.anchorMax = templateRect.anchorMax;
            rect.pivot = templateRect.pivot;
            rect.sizeDelta = templateRect.sizeDelta;
            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = templateRect.rect.width;
            layout.preferredHeight = templateRect.rect.height;
            var hit = go.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);

            var labelObject = new GameObject("Label");
            var labelRect = labelObject.AddComponent<RectTransform>();
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            var label = labelObject.AddComponent<TextMeshProUGUI>();
            if (source != null)
            {
                label.font = source.font;
                label.fontSharedMaterial = source.fontSharedMaterial;
                label.fontSize = source.fontSize;
                label.fontStyle = source.fontStyle;
                label.alignment = source.alignment;
                label.characterSpacing = source.characterSpacing;
            }
            label.color = new Color(0.95f, 0.88f, 0.74f);
            label.enableAutoSizing = false;
            label.raycastTarget = false;
            label.text = "Co-op";

            var button = go.AddComponent<Button>();
            button.targetGraphic = label;
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, 0.86f, 0.55f);
            colors.pressedColor = new Color(0.85f, 0.7f, 0.4f);
            button.colors = colors;
            button.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(new Action(CoopPanel.Toggle)));
            return go;
        }
    }
}
