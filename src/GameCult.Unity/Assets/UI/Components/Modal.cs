using System;
using System.Collections;
using R3;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GameCult.Unity.UI.Components
{
    /// <summary>
    /// Generic modal UI element that can either center itself
    /// or align to another RectTransform while staying within screen bounds.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class Modal : Generator
    {
        public event Action? BeforeDestroy;
        [SerializeField] protected VerticalLayoutGroup? layoutGroup;

        /// <summary>
        /// Positions the modal below the given element (or above if there's no space), matching its width
        /// </summary>
        public void AlignVertical(RectTransform alignToElement)
        {
            var rectTransform = (RectTransform)transform;

            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            var modalSize = rectTransform.rect.size;

            var pivot = rectTransform.pivot;

            pivot.x = 0;
            var scaleFactor = Canvas.scaleFactor;
            var corners = new Vector3[4];
            alignToElement.GetWorldCorners(corners);
            var pivotTop = corners[0].y > modalSize.y * scaleFactor;
            pivot.y = pivotTop ? 1 : 0;
            rectTransform.pivot = pivot;
            var delta = alignToElement.sizeDelta;
            if (layoutGroup != null)
                delta.x = alignToElement.sizeDelta.x + (layoutGroup.padding.left + layoutGroup.padding.right);
            rectTransform.sizeDelta = delta;
            rectTransform.position = pivotTop ? corners[0] : corners[1];
            if (layoutGroup != null)
                rectTransform.anchoredPosition += Vector2.left * layoutGroup.padding.left;
        }

        /// <summary>
        /// Positions the modal along the right or left edge,
        /// pivoting down or up, respectively, according to available space
        /// </summary>
        public void AlignEdge(RectTransform alignToElement)
        {
            var rectTransform = (RectTransform)transform;

            LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
            var modalSize = rectTransform.rect.size;
            var canvasRect = CanvasRoot.rect;

            var pivot = rectTransform.pivot;

            var scaleFactor = Canvas.scaleFactor;
            var corners = new Vector3[4];
            alignToElement.GetWorldCorners(corners);
            // Is there enough space for the element to drop down from the upper edge?
            var pivotTop = corners[2].y > modalSize.y * scaleFactor;
            pivot.y = pivotTop ? 1 : 0;
            // Is there enough space for the modal to sit on the right side?
            var pivotRight = corners[2].x / scaleFactor + modalSize.x < canvasRect.x;
            pivot.x = pivotRight ? 1 : 0;
            rectTransform.pivot = pivot;
            rectTransform.position = pivotTop ? corners[0] : corners[1];
        }

        public void Center()
        {
            var rect = (RectTransform)transform;
            rect.pivot = new Vector2(.5f,.5f);
            rect.anchoredPosition = Vector2.zero;
        }

        private void OnDestroy()
        {
            BeforeDestroy?.Invoke();
        }
    }
    
    public static class ModalGeneratorExtensions
    {
        /// <summary>
        /// Spawns a modal. Automatically creates and wires a click catcher that closes both.
        /// </summary>
        public static Modal? ShowModal(
            this IUIContext context)
        {
            // Instantiate click catcher behind modal
            var catcher = context.Resolver.Resolve<ClickCatcher>();
            if (catcher == null) return null;
            catcher.transform.SetParent(context.CanvasRoot, false);
            catcher.transform.SetAsLastSibling();

            // Instantiate modal on top
            var modal = context.Resolver.Resolve<Modal>();
            if (modal == null) return null;
            modal.transform.SetParent(context.CanvasRoot, false);
            modal.transform.SetAsLastSibling();
            
            // If modal is ever destroyed, also destroy click catcher
            modal.BeforeDestroy += () => Object.Destroy(catcher.gameObject);

            // Hook up dismissal
            catcher.OnClick.Subscribe(_ => Object.Destroy(modal.gameObject));

            return modal;
        }
    }

}