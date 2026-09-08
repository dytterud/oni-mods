using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace BlueprintsV2.UnityUI.Components
{
    internal class ColorLegendEntry : KMonoBehaviour
    {
        LocText Label = null!;
        Image Tintable = null!;
        public Color TargetColor;
        public string TargetText = null!;

        public override void OnSpawn()
        {
            base.OnSpawn();
            ApplyContent();

        }
        void ApplyContent()
        {
            if (TargetText == null || Label == null)
                return;

            Label.SetText(TargetText);
            Tintable.color = TargetColor;
        }

        public override void OnPrefabInit()
        {
            base.OnPrefabInit();

            Label = transform.Find("Label").gameObject.GetComponent<LocText>();
            Tintable = transform.Find("ColorPreview").gameObject.GetComponent<Image>();
        }
        public void SetContent(string label, Color color)
        {
            TargetColor = color;
            TargetText = label;
            ApplyContent();
        }
    }
}
