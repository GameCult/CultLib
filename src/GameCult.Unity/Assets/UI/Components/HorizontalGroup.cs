using System;
using UnityEngine;
using UnityEngine.UI;
#pragma warning disable CS8602 // Dereference of a possibly null reference.

namespace GameCult.Unity.UI.Components
{
    public class HorizontalGroup : ResolverComponent, IUIContext
    {
        [SerializeField] private HorizontalLayoutGroup? layoutGroup;
        private IUIContext? _parentContext;
        
        public IUIResolver Resolver => _parentContext.Resolver;
        public RectTransform ContentRoot => (RectTransform)transform;
        public Canvas Canvas => _parentContext.Canvas;
        public RectTransform CanvasRoot => _parentContext.CanvasRoot;
        public void Register(GameObject go) => _parentContext.Register(go);

        public Action? Refresh
        {
            get => _parentContext.Refresh;
            set => _parentContext.Refresh = value;
        }

        // public DisplayOptions InspectorLabelDisplayOptions => _parentContext.InspectorLabelDisplayOptions;
        // public DisplayOptions InspectorFieldDisplayOptions => _parentContext.InspectorFieldDisplayOptions;

        public HorizontalGroup Configure(IUIContext context, params ILayoutElement?[] children)
        {
            _parentContext = context;
            transform.SetParent(context.ContentRoot, false);
            context.Register(gameObject);
            
            foreach (var component in children)
            {
                if (component != null) AddComponent(component);
            }

            return this;
        }

        public void AddComponent(ILayoutElement component)
        {
            component.LayoutElement?.transform.SetParent(ContentRoot, false);
        }
    }
}