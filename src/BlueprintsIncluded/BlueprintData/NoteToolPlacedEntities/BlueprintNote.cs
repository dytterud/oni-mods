using BlueprintsV2.BlueprintData.OniTogether_Integration;
using KSerialization;
using UnityEngine;
using static BlueprintsV2.STRINGS.BLUEPRINTS_BLUEPRINTNOTE;

namespace BlueprintsV2.BlueprintData.NoteToolPlacedEntities;

public class BlueprintNote : KMonoBehaviour
{
    public static string FILTERLAYER = ("BLUEPRINTV2_FILTER_NOTES");
    [Serialize]
    public bool SeatIndicator = false;
    [MyCmpReq] protected InfoDescription description = null!;
    [MyCmpReq] protected KSelectable selectable = null!;
    protected MeshRenderer renderer = null!;
    public override void OnPrefabInit()
    {
        base.OnPrefabInit();
        renderer = GetComponentInChildren<MeshRenderer>();
    }

    public override void OnSpawn()
    {
        base.OnSpawn();
        if (SeatIndicator)
        {
            Seat();
            ///only seated notes own a mesh the player placed; unseated ones are transient previews
            ///and stay out of the global show/hide switch
            OnNoteVisibilityChanged += SetVisible;
            SetVisible(BlueprintState.NoteVisibility);
        }

        Subscribe((int)GameHashes.RefreshUserMenu, OnRefreshUserMenu);
        Subscribe((int)GameHashes.Cancel, Cancel);

        ///a note restored from a save never goes through SetInfo, so fade it here too
        ApplyNoteOpacity();
    }

    public override void OnCleanUp()
    {
        ///the event is static, so a note that stayed on it would outlive its GameObject and be
        ///called on a destroyed renderer. Removing a handler that was never added is a no-op.
        OnNoteVisibilityChanged -= SetVisible;
        base.OnCleanUp();
    }

    /// <summary>
    /// Every seated note listens here for the global show/hide switch in
    /// <see cref="BlueprintState.ToggleNoteVisibility"/>. A static event rather than a scan over
    /// the note layer, so a toggle reaches exactly the live seated notes and each note owns its
    /// own subscribe and unsubscribe.
    /// </summary>
    private static event Action<bool>? OnNoteVisibilityChanged;

    /// <summary>
    /// Pushes a new visibility to every seated note that currently exists.
    /// </summary>
    internal static void NotifyNoteVisibility(bool visible) => OnNoteVisibilityChanged?.Invoke(visible);

    private void SetVisible(bool visible)
    {
        ///Unity's overloaded == also catches a renderer that has been destroyed under us
        if (renderer == null)
            return;
        renderer.enabled = visible;
    }

    /// <summary>
    /// Fades the note to the configured opacity. The note's own colour carries an alpha already
    /// (its material is Klei's transparent placer shader), so this scales that rather than
    /// replacing it - at the default of 1 a note looks exactly as it always has.
    ///
    /// Call after any change to the material's colour: both note types set it themselves, and the
    /// tint they set is the unfaded one.
    /// </summary>
    protected void ApplyNoteOpacity()
    {
        if (renderer == null || renderer.material == null)
            return;

        var colour = renderer.material.color;
        colour.a = BaseAlpha * Mathf.Clamp01(Config.Instance.NoteOpacity);
        renderer.material.color = colour;
    }

    ///the alpha the note's own material ships with, captured before anything fades it, so
    ///repeated fades cannot compound.
    private float baseAlpha = -1f;
    private float BaseAlpha
    {
        get
        {
            if (baseAlpha < 0f)
                baseAlpha = renderer == null || renderer.material == null ? 1f : renderer.material.color.a;
            return baseAlpha;
        }
    }
    private void OnRefreshUserMenu(object data)
    {
        Game.Instance.userMenu.AddButton(this.gameObject, new KIconButtonMenu.ButtonInfo("action_cancel", DELETE_NOTE.NAME, new System.Action(this.OnCancel), tooltipText: DELETE_NOTE.TOOLTIP));
    }
    protected void Cancel(object? _ = null) => OnCancel();
    protected void OnCancel()
    {
        DetailsScreen.Instance.Show(false);
        MP_Helpers.HandleNoteDeletion(this);
        this.DeleteObject();
    }
    void Seat()
    {
        SetDescription();
        Grid.Objects[Grid.PosToCell(this), (int)ModAssets.BlueprintNotesLayer] = this.gameObject;
        //gameObject.SetLayerRecursively(LayerMask.NameToLayer("Default"));
    }
    public virtual void SetDescription()
    {

    }
    protected void RefreshSelection()
    {
        if (selectable.IsSelected)
        {
            DetailsScreen.Instance.target = null;
            DetailsScreen.Instance.Refresh(gameObject);///should refresh screen, make sure to not have infinite loop by having selection changed event trigger this again with no changes
			}
    }
    public virtual BlueprintNoteData GetNoteData(Vector2I? newPosition = null)
    {
        return new BlueprintNoteData();
    }

    public static void ClearExistingNote(int cell)
    {
        if (!Grid.IsValidCell(cell))
            return;

        var existingItem = Grid.Objects[cell, (int)ModAssets.BlueprintNotesLayer];

        if (existingItem != null)
        {
            existingItem.DeleteObject();
            Grid.Objects[cell, (int)ModAssets.BlueprintNotesLayer] = null;
        }
    }
}
