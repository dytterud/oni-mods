using UnityEngine;
using UnityEngine.UI;
using UtilLibs.UIcmp;

namespace BlueprintsV2.UnityUI.Components
{
    internal class IconSelectionEntry : KMonoBehaviour
    {
        [SerializeField] public LocText IconName = null!;
        [SerializeField] public Image Icon = null!;
        [SerializeField] public FButton Button = null!;
        string _name = null!;

        public void CollectReferences()
        {
            IconName = transform.Find("Label").gameObject.GetComponent<LocText>();
            Icon = transform.Find("Image").gameObject.GetComponent<Image>();
            Button = gameObject.AddComponent<FButton>();
        }

        public void Init(string name, Sprite icon, System.Action OnSelect, Color tint)
        {
            Button.OnClick += OnSelect;
            IconName.SetText(name);
            _name = name;
            Icon.sprite = icon;
            Icon.color = tint;
        }
        public override void OnSpawn()
        {
            base.OnSpawn();
            if (!_name.IsNullOrWhiteSpace())
                IconName.SetText(name);
        }
    }
}
