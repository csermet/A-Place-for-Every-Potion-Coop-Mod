using System;
using System.IO;
using System.Text;
using Il2Cpp;
using Il2CppCodeBase.Components.UI;
using Il2CppCodeBase.Services.UI.Mediator;
using Il2CppTMPro;
using MelonLoader.Utils;
using PotionCoop.Game;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;

namespace PotionCoop.UI
{
    /// <summary>
    /// Development aid, only with the command-line flag --coop-dump-ui: opens some of the game's windows in the main
    /// menu one after another and writes their hierarchy (components, layout, fonts, sprites) to UserData.
    /// </summary>
    internal static class UiDump
    {
        static readonly bool Enabled = Environment.GetCommandLineArgs() is { } args && Array.IndexOf(args, "--coop-dump-ui") >= 0;
        static readonly eUIWindow[] Windows =
        {
            eUIWindow.Stats, eUIWindow.QuitGameConfirm,
        };

        static int _step = -1;
        static float _next;
        static UIWindowFacade _open;
        static readonly StringBuilder Out = new();

        static readonly bool OpenOnStart = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-open") >= 0;
        static bool _openedOnStart;
        static readonly bool PauseInLab = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-pause") >= 0;
        static float _pauseAt = -1f;

        public static void Tick()
        {
            // Development aid: --coop-open shows the co-op window as soon as the main menu is ready.
            if (OpenOnStart && !_openedOnStart && GameRefs.State == eGameState.Lobby && UiClone.ButtonTemplate != null && UiClone.UiService != null)
            {
                _openedOnStart = true;
                CoopPanel.Show();
            }
            // --coop-pause: pause a few seconds after entering the lab (to look at the pause menu without a keyboard).
            if (PauseInLab && GameRefs.State == eGameState.Playing)
            {
                if (_pauseAt < 0f)
                    _pauseAt = Time.realtimeSinceStartup + 5f;
                else if (Time.realtimeSinceStartup >= _pauseAt)
                {
                    _pauseAt = float.MaxValue;
                    GameFlow.Machine?.Enter(eGameState.Paused);
                }
            }
            if (!Enabled || _step >= Windows.Length * 2 || GameRefs.State != eGameState.Lobby || Time.realtimeSinceStartup < _next)
                return;
            _next = Time.realtimeSinceStartup + 1.5f;
            try
            {
                var ui = FindUiService();
                if (ui == null)
                    return;
                if (_step == -1)
                {
                    var canvas = GameObject.Find("UISetup(Clone)/WindowsContainerCanvas");
                    if (canvas == null)
                        return;
                    Out.AppendLine("##### WindowsContainerCanvas (main menu open)");
                    Dump(canvas.transform, 0, 9);
                    _step = 0;
                    return;
                }

                var key = Windows[_step / 2];
                if (_step % 2 == 0)
                {
                    _open = ui.OpenWindowSync(key, false, true);
                    Out.AppendLine($"##### window {key}: {(_open == null ? "null" : _open.gameObject.name)}");
                }
                else
                {
                    if (_open != null)
                    {
                        try
                        {
                            Dump(_open.transform, 0, 14);
                            var document = _open.GetComponentInChildren<UIDocument>(true);
                            if (document != null && document.rootVisualElement != null)
                            {
                                Out.AppendLine($"  --- UI Toolkit document, panel settings {document.panelSettings?.name}");
                                DumpVisual(document.rootVisualElement, 1, 0);
                            }
                        }
                        finally
                        {
                            _open.Close(true, true, null);
                            _open = null;
                        }
                    }
                }
                _step++;
                if (_step >= Windows.Length * 2)
                {
                    var path = Path.Combine(MelonEnvironment.UserDataDirectory, "PotionCoop_uidump.txt");
                    File.WriteAllText(path, Out.ToString());
                    CoopMod.Log.Msg($"UI dump written: {path}");
                }
            }
            catch (Exception e)
            {
                Out.AppendLine($"!! step {_step}: {e}");
                CoopMod.Log.Warning($"UI dump step {_step} failed: {e}");
                _step++;
            }
        }

        static IUIService FindUiService()
        {
            var options = GameObject.Find("UISetup(Clone)/WindowsContainerCanvas/MainMenu(Clone)/Content/MenuItems/OptionsButton");
            var opener = options != null ? options.GetComponent<AdvancedButtonWindowOpener>() : null;
            return opener != null ? opener._uiService : null;
        }

        static void Dump(Transform t, int depth, int maxDepth)
        {
            var pad = new string(' ', depth * 2);
            var line = new StringBuilder($"{pad}{t.name}{(t.gameObject.activeSelf ? "" : " [off]")}");
            if (t.TryCast<RectTransform>() is { } r)
            {
                line.Append($"  rect a({r.anchorMin.x:0.##},{r.anchorMin.y:0.##})-({r.anchorMax.x:0.##},{r.anchorMax.y:0.##}) pos({r.anchoredPosition.x:0},{r.anchoredPosition.y:0}) size({r.sizeDelta.x:0},{r.sizeDelta.y:0}) piv({r.pivot.x:0.##},{r.pivot.y:0.##}) real({r.rect.width:0}x{r.rect.height:0})");
            }
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null)
                    continue;
                var type = c.GetIl2CppType()?.Name ?? c.GetType().Name;
                if (type is "RectTransform" or "Transform" or "CanvasRenderer")
                    continue;
                line.Append($" | {type}");
                if (c.TryCast<TMP_Text>() is { } text)
                {
                    var s = text.text ?? "";
                    line.Append($"(\"{(s.Length > 50 ? s.Substring(0, 50) : s).Replace("\n", "\\n")}\" font={text.font?.name} mat={text.fontSharedMaterial?.name} size={text.fontSize:0.#} auto={text.enableAutoSizing} style={text.fontStyle} col={Hex(text.color)} align={text.alignment} spacing={text.characterSpacing:0.#})");
                }
                else if (c.TryCast<Image>() is { } image)
                    line.Append($"(sprite={image.sprite?.name} col={Hex(image.color)} type={image.type} mat={image.material?.name} ppu={image.pixelsPerUnitMultiplier:0.##} ray={image.raycastTarget})");
                else if (c.TryCast<RawImage>() is { } raw)
                    line.Append($"(tex={raw.texture?.name} col={Hex(raw.color)})");
                else if (c.TryCast<Canvas>() is { } canvas)
                    line.Append($"(mode={canvas.renderMode} order={canvas.sortingOrder} override={canvas.overrideSorting})");
                else if (c.TryCast<CanvasScaler>() is { } scaler)
                    line.Append($"(mode={scaler.uiScaleMode} ref={scaler.referenceResolution} match={scaler.matchWidthOrHeight})");
                else if (c.TryCast<LayoutGroup>() is { } layout)
                {
                    line.Append($"(pad={layout.padding.left},{layout.padding.right},{layout.padding.top},{layout.padding.bottom} align={layout.childAlignment}");
                    if (c.TryCast<HorizontalOrVerticalLayoutGroup>() is { } hv)
                        line.Append($" spacing={hv.spacing} ctrlW={hv.childControlWidth} ctrlH={hv.childControlHeight} expW={hv.childForceExpandWidth} expH={hv.childForceExpandHeight}");
                    line.Append(')');
                }
                else if (c.TryCast<LayoutElement>() is { } element)
                    line.Append($"(min={element.minWidth:0}x{element.minHeight:0} pref={element.preferredWidth:0}x{element.preferredHeight:0} flex={element.flexibleWidth:0.#}x{element.flexibleHeight:0.#} ignore={element.ignoreLayout})");
                else if (c.TryCast<ContentSizeFitter>() is { } fitter)
                    line.Append($"(h={fitter.horizontalFit} v={fitter.verticalFit})");
                else if (c.TryCast<AdvancedButtonWindowOpener>() is { } opener)
                    line.Append($"(window={opener._windowID} exclusive={opener._exclusive})");
                else if (c.TryCast<AdvancedButtonWindowCloser>() is { } closer)
                    line.Append($"(now={closer._now} destroy={closer._destroy} delay={closer._delay} openOnClose={closer._openOnClose} asMenu={closer._asMenu})");
                else if (c.TryCast<UIWindowCloser>() is { } wcloser)
                    line.Append($"(stateChange={wcloser._closeOnStateChange} now={wcloser._closeNow} destroy={wcloser._destroy} cancelKey={wcloser._closeOnCancelKey})");
                else if (c.TryCast<CanvasGroup>() is { } group)
                    line.Append($"(alpha={group.alpha:0.##} interact={group.interactable} block={group.blocksRaycasts})");
            }
            Out.AppendLine(line.ToString());
            if (depth >= maxDepth)
            {
                if (t.childCount > 0)
                    Out.AppendLine($"{pad}  â€¦ {t.childCount} children");
                return;
            }
            for (var i = 0; i < t.childCount; i++)
                Dump(t.GetChild(i), depth + 1, maxDepth);
        }

        static void DumpVisual(VisualElement e, int depth, int index)
        {
            if (depth > 16)
                return;
            var pad = new string(' ', depth * 2);
            var type = e.GetIl2CppType()?.Name ?? "?";
            var text = e.TryCast<TextElement>() is { } te ? $" \"{te.text}\"" : "";
            Out.AppendLine($"{pad}<{type}> #{e.name}{text}");
            for (var i = 0; i < e.childCount; i++)
                DumpVisual(e.ElementAt(i), depth + 1, i);
        }

        static string Hex(Color c) => $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}{(int)(c.a * 255):X2}";
    }
}
