using BlueprintsV2.BlueprintData;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UtilLibs;

namespace BlueprintsV2.UnityUI;

/// <summary>
/// The show/hide-notes button in the game's top-left control cluster, next to the sandbox toggle
/// and the Klei item-drop button.
///
/// The screen is rebuilt on every colony load, so a fresh button is made each time and only the
/// newest one is tracked. The state itself lives in <see cref="BlueprintState.NoteVisibility"/>;
/// this class only paints it.
/// </summary>
internal static class NoteVisibilityButton
{
    /// <summary>
    /// The harness finds the button by this name.
    /// </summary>
    public const string ButtonName = "toggleNoteVisibility";

    private const int STATE_HIDDEN = 0;
    private const int STATE_VISIBLE = 1;

    /// <summary>
    /// Klei's selected-blue, used while notes are shown.
    /// </summary>
    private static readonly Color VisibleColour = new Color32(31, 161, 255, 255);

    private static MultiToggle? current;

    /// <summary>
    /// Builds the button on <paramref name="screen"/> by cloning one of its existing small toggles,
    /// so it picks up the same size, background and layout as its neighbours without this code
    /// having to know their prefab's layout.
    /// </summary>
    public static void Create(TopLeftControlScreen screen)
    {
        var prototype = screen.sandboxToggle != null ? screen.sandboxToggle : screen.kleiItemDropButton;
        if (prototype == null || prototype.transform.parent == null)
        {
            SgtLogger.warning("TopLeftControlScreen has no toggle to model the note visibility button on");
            return;
        }

        var row = prototype.transform.parent.gameObject;

        ///OnActivate running twice on one screen must not stack a second button into the row
        if (current != null && current.transform.parent == row.transform)
        {
            Refresh();
            return;
        }

        var go = Util.KInstantiateUI(prototype.gameObject, row, force_active: true);
        go.name = ButtonName;
        go.transform.SetAsLastSibling();

        var toggle = go.GetComponent<MultiToggle>();
        StripLabels(go);
        SetIcon(toggle);
        toggle.states = BuildStates(toggle);
        ///the click sound is played by hand after the toggle, not by whatever the clone carried
        toggle.play_sound_on_click = false;
        toggle.play_sound_on_release = false;
        toggle.onClick = OnClick;

        if (!go.TryGetComponent<ToolTip>(out var tooltip))
            tooltip = go.AddComponent<ToolTip>();
        tooltip.UseFixedStringKey = false;
        tooltip.ClearMultiStringTooltip();
        ///rebuilt on every show: a rebind mid-session does not reactivate this screen
        tooltip.OnToolTip = BuildTooltip;

        FitToIcon(go.transform as RectTransform, row.transform as RectTransform, prototype.transform as RectTransform);

        current = toggle;
        Refresh();
    }

    /// <summary>
    /// Paints the current button to match <see cref="BlueprintState.NoteVisibility"/>. A no-op
    /// while there is no live button, e.g. a hotkey press before the screen first activates.
    /// </summary>
    public static void Refresh()
    {
        if (current == null)
            return;
        current.ChangeState(BlueprintState.NoteVisibility ? STATE_VISIBLE : STATE_HIDDEN);
    }

    /// <summary>
    /// Drops the reference to a button whose screen is being torn down.
    /// </summary>
    public static void Forget() => current = null;

    private static void OnClick()
    {
        BlueprintState.ToggleNoteVisibility();
        KFMOD.PlayUISound(GlobalAssets.GetSound("HUD_Click"));
    }

    /// <summary>
    /// The action's title, plus its key only when one is really bound. GetHotkeyString renders an
    /// unbound action as a localized "[NONE]", so the binding table is asked instead of the string.
    /// </summary>
    internal static string BuildTooltip()
    {
        string text = STRINGS.UI.ACTIONS.TOGGLENOTEVIS;
        var action = ModAssets.Actions.BlueprintsToggleNoteVisibility.GetKAction();
        if (IsBound(action))
            text += " " + GameUtil.GetHotkeyString(action);
        return text;
    }

    private static bool IsBound(global::Action action)
    {
        var bindings = GameInputMapping.KeyBindings;
        if (bindings == null)
            return false;
        foreach (var binding in bindings)
        {
            if (binding.mAction == action && binding.mKeyCode != KKeyCode.None)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Two states built from the prototype's "off" look: hidden keeps it untouched, visible swaps
    /// in the blue with a lighter hover.
    /// </summary>
    private static ToggleState[] BuildStates(MultiToggle toggle)
    {
        var source = toggle.states ?? [];
        int offIndex = (int)TopLeftControlScreen.MultiToggleState.Off;
        ToggleState hidden = source.Length > offIndex ? source[offIndex]
            : source.Length > 0 ? source[0]
            : new ToggleState { color = Color.white, color_on_hover = Color.white };

        hidden.Name = "NotesHidden";
        hidden.on_click_override_sound_path = string.Empty;
        hidden.on_release_override_sound_path = string.Empty;
        hidden.has_sound_parameter = false;
        hidden.additional_display_settings = CopySettings(hidden.additional_display_settings);

        var visible = hidden;
        visible.Name = "NotesVisible";
        visible.color = VisibleColour;
        visible.color_on_hover = Color.Lerp(VisibleColour, Color.white, 0.3f);
        visible.use_color_on_hover = true;
        ///a struct copy still shares the array; the two states must not alias it
        visible.additional_display_settings = CopySettings(hidden.additional_display_settings);

        return [hidden, visible];
    }

    private static StatePresentationSetting[] CopySettings(StatePresentationSetting[]? settings) =>
        settings == null ? [] : (StatePresentationSetting[])settings.Clone();

    /// <summary>
    /// Takes the prototype's caption off the clone. The sandbox toggle, the usual prototype, is
    /// labelled "SANDBOX", and a copied label would read as a second sandbox button. The button is
    /// icon-only; its tooltip names it.
    ///
    /// Nothing here relies on the prefab's child names: every text graphic under the clone is
    /// found by type. A label on its own object is switched off, so it also drops out of the
    /// button's layout; one sharing an object with an image, or sitting on the button itself, is
    /// blanked and disabled instead, so the icon and background stay.
    /// </summary>
    private static void StripLabels(GameObject button)
    {
        foreach (var graphic in button.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic is not (TMP_Text or Text))
                continue;

            var holder = graphic.gameObject;
            if (holder != button && holder.GetComponentInChildren<Image>(true) == null)
            {
                holder.SetActive(false);
                continue;
            }

            if (graphic is TMP_Text tmp)
                tmp.text = string.Empty;
            else if (graphic is Text text)
                text.text = string.Empty;
            graphic.enabled = false;
        }
    }

    /// <summary>
    /// With its caption gone the clone would still be as wide as the prototype, leaving an empty
    /// slot where the text was. Makes it square at the height the row gives it, which is the height
    /// of its neighbours. The row is laid out first so that height is real; if it still reads zero
    /// (e.g. the canvas is not live yet) the prototype's height is used, and failing that the width
    /// is left to the button's own layout, which no longer counts the hidden label.
    /// </summary>
    private static void FitToIcon(RectTransform? button, RectTransform? row, RectTransform? prototype)
    {
        if (button == null)
            return;

        if (row != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(row);
        float size = button.rect.height;
        if (size <= 0f && prototype != null)
            size = prototype.rect.height;
        if (size <= 0f)
            return;

        if (!button.TryGetComponent<LayoutElement>(out var layout))
            layout = button.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = size;
        layout.preferredWidth = size;
        layout.flexibleWidth = 0f;
        ///for a row that does not drive its children's width
        button.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size);

        if (row != null)
            LayoutRebuilder.MarkLayoutForRebuild(row);
    }

    /// <summary>
    /// Puts the note icon on the clone. If the prototype draws its icon on a child image, that
    /// image gets the note sprite, including in any per-state setting that would swap it back;
    /// if its only image is the toggle image itself, the icon is carried by the states' sprite.
    /// </summary>
    private static void SetIcon(MultiToggle toggle)
    {
        var sprite = ModAssets.NoteToolIcon_Sprite;
        Image? icon = null;
        foreach (var image in toggle.GetComponentsInChildren<Image>(true))
        {
            ///the root's own image, if any, is background, not icon
            if (image != toggle.toggle_image && image.gameObject != toggle.gameObject)
            {
                icon = image;
                break;
            }
        }

        var states = toggle.states ?? [];
        if (icon == null)
        {
            for (int i = 0; i < states.Length; i++)
                states[i].sprite = sprite;
            if (toggle.toggle_image != null)
                toggle.toggle_image.sprite = sprite;
            return;
        }

        icon.sprite = sprite;
        for (int i = 0; i < states.Length; i++)
        {
            var settings = states[i].additional_display_settings;
            if (settings == null)
                continue;
            for (int j = 0; j < settings.Length; j++)
            {
                if (settings[j].image_target == icon)
                    settings[j].sprite = sprite;
            }
        }
    }
}
