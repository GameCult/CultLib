using System;
using TMPro;

namespace GameCult.Unity.UI
{
    [Serializable]
    public class DisplayOptions
    {
        public TextAlignmentOptions? TextAlignment { get; }
        public VerticalAlignmentOptions? VerticalAlignment { get; }
        public float? LayoutPriority { get; }
        public float? LayoutHeight { get; }
        public float? TextSize { get; }
        public bool Interactable { get; }
        public bool PlaceInContext { get; }
        
        public DisplayOptions(bool placeInContext = true, 
            bool interactable = true, 
            float? layoutHeight = null, 
            float? textSize = null,
            float? layoutPriority = null,
            VerticalAlignmentOptions? verticalAlignment = null,
            TextAlignmentOptions? textAlignment = null)
        {
            PlaceInContext = placeInContext;
            LayoutHeight = layoutHeight;
            Interactable = interactable;
            TextSize = textSize;
            LayoutPriority = layoutPriority;
            VerticalAlignment = verticalAlignment;
            TextAlignment = textAlignment;
        }
    }
}