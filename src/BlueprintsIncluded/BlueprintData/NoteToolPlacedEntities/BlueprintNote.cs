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

    private void ChangeVisibility(bool visible) => renderer.enabled = visible;
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
