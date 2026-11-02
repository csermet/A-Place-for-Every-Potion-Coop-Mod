using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppCodeBase.Components.UI;
using Il2CppCodeBase.Services.UI.Mediator;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.UI
{
    /// <summary>
    /// Builds our UI out of copies of the game's own widgets. A copy keeps the look, highlight and sounds, but two
    /// things must be fixed: its action components (open Settings, quit…) are removed, and the services the game
    /// injected into the original at load (input, sounds, UI) are not copied by Instantiate, so they are copied here.
    /// Clicks on our copies arrive through <see cref="MenuButtonClickPatch"/> and are routed with <see cref="OnClick"/>.
    /// </summary>
    internal static class UiClone
    {
        public const string CanvasPath = "UISetup(Clone)/WindowsContainerCanvas";
        const string ButtonTemplatePath = CanvasPath + "/MainMenu(Clone)/Content/DemoFooter/WishlistOnSteamButton";
        const string MenuEntryPath = CanvasPath + "/MainMenu(Clone)/Content/MenuItems/OptionsButton";

        static readonly string[] ActionComponents =
        {
            "AdvancedButtonWindowOpener", "AdvancedButtonGameStateSwitcher", "AdvancedButtonContinueStarter",
            "AdvancedButtonNewGameStarter", "AdvancedButtonGameQuitter", "AdvancedButtonWindowCloser",
            "AdvancedButtonSteamStoreOpener", "AdvancedButtonSupportBundleExporter", "AdvancedButtonLabKeeper",
            "AdvancedButtonLabRemaker", "AdvancedButtonAccessibilitySettingsRequester", "AdvancedButtonPourModeWriter",
            "LocalizedTextUpdater", "NewLabHighlighter", "ContinueNumeralAppender", "NewGameNumeralAppender",
        };

        /// <summary>The game's window service (opens/closes windows); taken from a menu button that has it.</summary>
        public static IUIService UiService { get; private set; }

        /// <summary>A framed button (the main menu's hidden "Wishlist" button), kept across scenes.</summary>
        public static GameObject ButtonTemplate { get; private set; }

        static GameObject _holder;
        static readonly List<GameObject> Donors = new();
        static readonly Dictionary<int, Action> Clicks = new();
        static readonly HashSet<string> ReportedMissing = new();

        /// <summary>Called with every Settings entry found in a menu: its services serve all our copies.</summary>
        public static void Remember(AdvancedButtonWindowOpener opener)
        {
            if (opener == null)
                return;
            if (UiService == null && opener._uiService != null)
                UiService = opener._uiService;
            Donors.RemoveAll(d => d == null);
            if (!Donors.Contains(opener.gameObject))
                Donors.Add(opener.gameObject);
        }

        /// <summary>In the main menu: keep a copy of the framed button for windows opened later (also in the lab).</summary>
        public static void EnsureButtonTemplate()
        {
            if (ButtonTemplate != null)
                return;
            var source = GameObject.Find(CanvasPath)?.transform.Find(ButtonTemplatePath.Substring(CanvasPath.Length + 1));
            var fallback = GameObject.Find(MenuEntryPath);
            if (source == null && fallback == null)
                return;
            if (_holder == null)
            {
                _holder = new GameObject("PotionCoopTemplates");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }
            ButtonTemplate = CloneInactive((source ?? fallback.transform).gameObject, _holder.transform);
            ButtonTemplate.name = "ButtonTemplate";
            ButtonTemplate.SetActive(true); // still inactive in the hierarchy: the holder is off
            CoopMod.Log.Msg(source != null ? "co-op window: button template ready" : "co-op window: framed button not found, using the menu entry look");
        }

        /// <summary>
        /// A copy of <paramref name="source"/> under <paramref name="parent"/>, inactive, with the game's actions removed
        /// and its services filled in. Set it up, then activate it.
        /// </summary>
        public static GameObject CloneInactive(GameObject source, Transform parent)
        {
            var wasActive = source.activeSelf;
            source.SetActive(false);
            GameObject clone;
            try
            {
                clone = Object.Instantiate(source, parent, false);
            }
            finally
            {
                source.SetActive(wasActive);
            }
            Strip(clone, ActionComponents);
            CopyServices(clone, source);
            return clone;
        }

        /// <summary>Removes the components with these (Il2Cpp) type names from the object and its children.</summary>
        public static void Strip(GameObject root, params string[] typeNames)
        {
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var type = behaviour.GetIl2CppType();
                if (type != null && typeNames.Contains(type.Name))
                    Object.DestroyImmediate(behaviour);
            }
        }

        public static void OnClick(GameObject button, Action action) => Clicks[button.GetInstanceID()] = action;

        public static void Forget(GameObject button)
        {
            if (button != null)
                Clicks.Remove(button.GetInstanceID());
        }

        /// <summary>
        /// From the click patch: runs our action if the pressed button is one of ours. Next frame, outside the game's
        /// button code, because the action may rebuild the window and destroy the very button being pressed.
        /// </summary>
        public static void TryClick(BaseButton button)
        {
            if (Clicks.TryGetValue(button.gameObject.GetInstanceID(), out var action))
                MainThread.Post(action);
        }

        static readonly Dictionary<string, Sprite> Sprites = new();

        /// <summary>A loaded sprite of the game by name (e.g. "btn-primary"), or null.</summary>
        public static Sprite FindSprite(string name)
        {
            if (Sprites.TryGetValue(name, out var cached) && cached != null)
                return cached;
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite.name == name)
                {
                    Sprites[name] = sprite;
                    return sprite;
                }
            }
            return null;
        }

        // ---------- injected services ----------

        static readonly Dictionary<string, Type> ManagedTypes = new();
        static readonly MethodInfo CastMethod = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!;

        /// <summary>
        /// Fills every empty interface-typed "_IService" field of the copy's components from a component of the same
        /// type in the source or in a remembered menu entry (the services are the game's singletons).
        /// </summary>
        static void CopyServices(GameObject clone, GameObject source)
        {
            var donors = new List<MonoBehaviour>(source.GetComponentsInChildren<MonoBehaviour>(true));
            foreach (var donor in Donors)
                if (donor != null)
                    donors.AddRange(donor.GetComponentsInChildren<MonoBehaviour>(true));

            foreach (var target in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var il2cppType = target.GetIl2CppType();
                var managedType = ManagedTypeOf(il2cppType);
                if (managedType == null)
                    continue;
                var sameType = donors.Where(d => d.GetIl2CppType()?.FullName == il2cppType.FullName).ToList();
                if (sameType.Count == 0)
                    continue;
                var cast = CastMethod.MakeGenericMethod(managedType);
                object typedTarget;
                try { typedTarget = cast.Invoke(target, null); }
                catch (Exception) { continue; }

                for (var t = managedType; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!IsServiceField(property) || property.GetValue(typedTarget) != null)
                            continue;
                        object value = null;
                        foreach (var donor in sameType)
                        {
                            try { value = property.GetValue(cast.Invoke(donor, null)); }
                            catch (Exception) { value = null; }
                            if (value != null)
                                break;
                        }
                        if (value != null)
                            property.SetValue(typedTarget, value);
                        else if (ReportedMissing.Add($"{il2cppType.Name}.{property.Name}"))
                            CoopMod.Log.Warning($"co-op UI: no {il2cppType.Name}.{property.Name} to copy");
                    }
                }
            }
        }

        static bool IsServiceField(PropertyInfo property)
        {
            var kind = property.PropertyType;
            return property.Name.StartsWith("_") && kind.Name.Length > 1 && kind.Name[0] == 'I' && char.IsUpper(kind.Name[1])
                   && typeof(Il2CppObjectBase).IsAssignableFrom(kind) && !typeof(Object).IsAssignableFrom(kind)
                   && property.CanRead && property.CanWrite;
        }

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
    }
}
