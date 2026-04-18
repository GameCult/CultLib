using UnityEngine;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    public class LayoutComponent : ResolverComponent, ILayoutElement
    {
        [SerializeField] private LayoutElement? layoutElement;

        public LayoutComponent Configure(IUIContext context, DisplayOptions? displayOptions = null)
        {
            if (displayOptions is not { PlaceInContext: true })
                transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            this.ApplyLayoutOptions(displayOptions);
            return this;
        }
        
        public float LayoutPriority
        {
            set
            {
                if (layoutElement != null) layoutElement.flexibleWidth = value;
            }
        }

        public LayoutElement? LayoutElement => layoutElement;
    }

    public static class LayoutComponentGeneratorExtensions
    {
        public static void AddLayoutElement(this IUIContext context,
            string prefabName,
            DisplayOptions? displayOptions = null) =>
            context.Resolver.Resolve<LayoutComponent>(prefabName)?.Configure(context, displayOptions);
    }
}
