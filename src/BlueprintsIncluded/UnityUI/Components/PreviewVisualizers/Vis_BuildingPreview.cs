using BlueprintsV2.BlueprintData;
using BlueprintsV2.Visualizers.ReplacementVisualizers;
using BlueprintsV2.UnityUI;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UtilLibs;
using UtilLibs.UIcmp;
using static BlueprintsV2.STRINGS.UI.BLUEPRINTSELECTOR.BLUEPRINTINFO.STATS;
using static Grid.Restriction;

namespace BlueprintsV2.UnityUI.Components.PreviewVisualizers
{
    internal class Vis_BuildingPreview : KMonoBehaviour
    {
        protected RectTransform _rectTransform = null!;
        protected KBatchedAnimController kbac = null!;
        protected string defaultAnim = null!;

        protected FButton _disableToggle = null!;
        protected Image _disableToggleHover = null!;
        protected RectTransform _disableToggleSize = null!;

        private Color _color = Color.white;
        private Color _desaturated = new(1, 1, 1, 0.25f);
        private Color _disabledHighlighted = new(1, 1, 1, 0.50f);
        protected Color _tempDisabled = UIUtils.rgba(2, 198, 246, 0.75);

        protected BuildingConfig _building = null!;

        protected void InitClickable()
        {
            var toggle = transform.Find("ClickableOverlay");
            _disableToggleSize = toggle.rectTransform();
            _disableToggleHover = toggle.GetComponent<Image>();
            _disableToggle = transform.gameObject.AddOrGet<FButton>();

            _disableToggle.OnClick += () => ToggleBuildingDisabled(true);
            _disableToggle.OnRightClick += () => ToggleBuildingDisabled(false);
        }
        void ToggleBuildingDisabled(bool on)
        {
            _building?.BuildingDisabled = !on;
            BlueprintSelectionScreen.Instance?.RefreshPreview();
        }

        internal virtual Vis_BuildingPreview Init(BuildingConfig building)
        {
            // Callers only build a preview for a config whose BuildingDef resolved.
            BuildingDef def = building.BuildingDef!;
            _building = building;
            InitClickable();
            _rectTransform = GetComponent<RectTransform>();

            kbac = gameObject.AddComponent<KBatchedAnimController>();
            var renderer = gameObject.AddComponent<KBatchedAnimCanvasRenderer>();
            kbac.materialType = KAnimBatchGroup.MaterialType.UI;
            //kbac.visibilityType = KAnimControllerBase.VisibilityType.Always;
            kbac.setScaleFromAnim = false;
            kbac.sceneLayer = Grid.SceneLayer.FXFront;
            kbac.AnimFiles = def.AnimFiles;
            kbac.isMovable = true;

            kbac.defaultAnim = defaultAnim = def.DefaultAnimState;
            //SgtLogger.l("StartAnim " + def.name + ": " + defaultAnim);
            UpdatePosition(building);
            return this;
        }
        /// <summary>
        /// this mirrors Rotatable since kbac offset/pivot does not seem to work for ui kbacs
        /// do not try understanding the numbers, they work properly this way.
        /// </summary>
        /// <param name="building"></param>
        void UpdatePosition(BuildingConfig building)
        {
            Orientation orientation = building.Orientation;
            BuildingDef def = building.BuildingDef!;
            kbac.flipX = orientation == Orientation.FlipH;
            kbac.flipY = orientation == Orientation.FlipV;

            bool correctX = def.WidthInCells % 2 == 0;

            float width = def.WidthInCells;
            float heigh = def.HeightInCells;

            _rectTransform.pivot = new(1f / width, 1f / heigh);

            float xPosOffset = orientation == Orientation.FlipH ? -50 : 50;

            if (correctX)
            {
                switch (orientation)
                {
                    default:
                        transform.localPosition += new Vector3(xPosOffset, 0); break;
                    case Orientation.R90:
                        transform.localPosition += new Vector3(0, -50); break;
                    case Orientation.R180:
                        transform.localPosition += new Vector3(-xPosOffset, 0); break;
                    case Orientation.R270:
                        transform.localPosition += new Vector3(0, 50); break;
                }
            }


            switch (orientation)
            {
                case Orientation.Neutral:
                case Orientation.FlipV:
                case Orientation.FlipH:
                    break;
                case Orientation.R90:
                    transform.Rotate(0, 0, -90);
                    transform.localPosition += new Vector3(-50, 50, 0);
                    break;
                case Orientation.R180:
                    transform.Rotate(0, 0, -180);
                    transform.localPosition += new Vector3(0, 100f, 0);
                    break;
                case Orientation.R270:
                    transform.Rotate(0, 0, -270);
                    transform.localPosition += new Vector3(50, 50, 0);
                    break;
            }

            _disableToggleSize.sizeDelta = new(width * 100f, heigh * 100f);
            _disableToggleSize.localPosition = Vector3.zero;
        }

        void CorrectDefaultAnim()
        {
            ///Relevant for some logic buildings that usually have their anim set by the logic component
            if (!kbac.HasAnimation(defaultAnim))
            {
                //SgtLogger.l(defaultAnim + " anim not found");
                defaultAnim = kbac.AnimFiles.First()?.GetData()?.GetAnim(0)?.name ?? "off";
            }
        }

        public override void OnSpawn()
        {
            base.OnSpawn();
            CorrectDefaultAnim();
            kbac.Play(defaultAnim);

            kbac.SetSymbolVisiblity("booster", false);
            kbac.SetSymbolVisiblity("blue_light_bloom", false);
        }

        internal void RefreshOpacity(bool layerActive, bool useLowOpacity, bool highLighted)
        {
            bool disabledButHighlighted = !layerActive && highLighted;
            if (disabledButHighlighted)
                kbac.TintColour = _disabledHighlighted;
            else if (useLowOpacity)
                kbac.TintColour = _desaturated;
            else if (_building.BuildingDisabled)
                kbac.TintColour = _tempDisabled;
            else
                kbac.TintColour = Color.white;

            kbac.SetVisiblity(layerActive || highLighted);

            _disableToggleHover.raycastTarget = layerActive;
        }
    }
}
