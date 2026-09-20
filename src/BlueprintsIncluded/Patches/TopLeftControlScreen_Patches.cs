using BlueprintsV2.BlueprintData;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UtilLibs;

namespace BlueprintsV2.Patches;

/// <summary>
/// Adds a note-visibility toggle to the top-left control screen, next to the sandbox toggle.
/// Ported from upstream 6c0a4238.
/// </summary>
internal class TopLeftControlScreen_Patches
{
    ///The live button, or null before the screen has activated (and after a reload, until it
    ///activates again). Everything that touches it goes through RefreshNoteVisibilityToggle so
    ///the hotkey path works whether or not the button exists yet.
    private static MultiToggle? _toggleButton;

    /// <summary>Syncs the button's visual state to <see cref="BlueprintState.NoteVisibility"/>.</summary>
    internal static void RefreshNoteVisibilityToggle()
    {
        if (_toggleButton != null)
            _toggleButton.ChangeState(BlueprintState.NoteVisibility ? 2 : 1);
    }

    [HarmonyPatch(typeof(TopLeftControlScreen), nameof(TopLeftControlScreen.OnActivate))]
    public static class AddNoteVisibilityToggle
    {
        private static void OnClick()
        {
            BlueprintState.ToggleNoteVisibility();
            RefreshNoteVisibilityToggle();
            KMonoBehaviour.PlaySound(GlobalAssets.GetSound("HUD_Click"));
        }

        public static void Postfix(TopLeftControlScreen __instance)
        {
            ///cloned from the Klei item-drop button because it is already a MultiToggle with the
            ///right styling and a ToolTip; only the icon, colour and click handler differ.
            var button = Util.KInstantiateUI(
                __instance.kleiItemDropButton.gameObject,
                __instance.sandboxToggle.transform.parent.gameObject,
                true).transform;
            button.name = "toggleNoteVisibility";

            var icon = button.transform.FindComponent<Image>();
            if (icon != null)
                icon.sprite = ModAssets.NoteToolIcon_Sprite;

            var foreground = button.Find("FG");
            if (foreground != null && foreground.TryGetComponent<Image>(out var fgImage))
            {
                fgImage.sprite = ModAssets.NoteToolIcon_Sprite;
                fgImage.overrideSprite = ModAssets.NoteToolIcon_Sprite;
            }

            if (!button.TryGetComponent<MultiToggle>(out var toggle))
            {
                SgtLogger.warning("note visibility toggle: cloned button has no MultiToggle, skipping");
                return;
            }
            _toggleButton = toggle;
            ///routed through BlueprintState so the hotkey - or anything else - keeps the button
            ///in sync without every caller having to remember to refresh it.
            BlueprintState.NoteVisibilityUiRefresh = RefreshNoteVisibilityToggle;

            var buttonColor = UIUtils.rgb(31, 161, 255);
            toggle.states[2].color = buttonColor;
            toggle.states[2].color_on_hover = UIUtils.Lighten(buttonColor, 20);
            toggle.onClick += OnClick;

            button.SetSiblingIndex(__instance.kleiItemDropButton.transform.GetSiblingIndex());

            if (button.TryGetComponent<ToolTip>(out var tooltip))
            {
                ///rebuilt each time it is shown, not once here: the screen only activates on load,
                ///so a text fixed now would keep advertising the old key after a mid-session
                ///rebind (#68).
                tooltip.OnToolTip = TooltipText;
                tooltip.SetSimpleTooltip(TooltipText());
            }

            RefreshNoteVisibilityToggle();
        }

        private static string TooltipText()
        {
            ///checked on the binding, not the string: GetHotkeyString renders an unbound action as
            ///a localized "[NONE]", never as empty. The action ships unbound, so that is the default.
            var action = ModAssets.Actions.BlueprintsToggleNoteVisibility.GetKAction();
            bool bound = Array.Exists(GameInputMapping.KeyBindings,
                b => b.mAction == action && b.mKeyCode != KKeyCode.None);
            return bound
                ? STRINGS.UI.ACTIONS.TOGGLENOTEVIS + " " + GameUtil.GetHotkeyString(action)
                : STRINGS.UI.ACTIONS.TOGGLENOTEVIS;
        }
    }
}
