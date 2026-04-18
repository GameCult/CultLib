using UnityEngine;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    public class LayoutComponent : ResolverComponent, ILayoutElement
    {
        [SerializeField] private LayoutElement? layoutElement;
        
        public float LayoutPriority
        {
            set
            {
                if (layoutElement != null) layoutElement.flexibleWidth = value;
            }
        }

        public LayoutElement? LayoutElement => layoutElement;
    }
}