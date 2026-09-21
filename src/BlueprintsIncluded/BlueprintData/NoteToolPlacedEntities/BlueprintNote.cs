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

    /// <summary>
    /// Raised when <see cref="BlueprintState.ToggleNoteVisibility"/> runs. Static because the
    /// toggle is global and notes come and go; every seated note subscribes for its lifetime.
    /// </summary>
    private static event Action<bool>? OnNoteVisibilityChanged;

    internal static void TriggerNoteVisibilityChange(bool on) => OnNoteVisibilityChanged?.Invoke(on);

    private int refreshHandle = -1, cancelHandle = -1;

    public override void OnSpawn()
    {
        base.OnSpawn();
        if (SeatIndicator)
            Seat();

        refreshHandle = Subscribe((int)GameHashes.RefreshUserMenu, OnRefreshUserMenu);
        cancelHandle = Subscribe((int)GameHashes.Cancel, Cancel);

        ///a note restored from a save never goes through SetInfo, so fade it here too
        ApplyNoteOpacity();

        ///only seated notes own a rendered mesh; unseated ones have nothing to hide.
        if (SeatIndicator)
        {
            OnNoteVisibilityChanged += ChangeVisibility;
            ChangeVisibility(BlueprintState.NoteVisibility);
        }
    }

    public override void OnCleanUp()
    {
        Unsubscribe(cancelHandle);
        Unsubscribe(refreshHandle);
        ///a static event outlives the note, so failing to detach here would leak this instance
        ///and then fire ChangeVisibility on a destroyed object.
        if (SeatIndicator)
            OnNoteVisibilityChanged -= ChangeVisibility;

        base.OnCleanUp();
    }

    /// <summary>
    /// The guard is defence, not a known defect. Upstream crashes here (their issue #362) because
    /// their note re-creates its renderer and nulls the field in OnCleanUp; this fork keeps the
    /// placer prefab's own MeshRenderer and never reassigns it, so the field cannot be null by any
    /// path traced here - the unsubscribe above is still what keeps a destroyed note off the
    /// static event. Unity's == also covers a destroyed renderer, which is the one shape reading
    /// cannot rule out: the event is static and outlives a colony reload.
    /// </summary>
    private void ChangeVisibility(bool visible)
    {
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
