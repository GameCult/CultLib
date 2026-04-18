using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameCult.Unity.UI.Components
{
    /// <summary>
    /// A UI element similar to Slider but allowing free 2D movement of the handle within a rect,
    /// constrained by an external validation function.
    /// </summary>
    /// <remarks>
    /// The handle RectTransform is driven by the ConstrainedHandle. The position is normalized (0-1) in both axes.
    /// An external Func<Vector2, Vector2> can be provided via constrainFunction to validate/constrain positions (e.g., to a circle for HSV picker).
    /// When a change to the handle value occurs, a callback is sent to any registered listeners of UI.ConstrainedHandle.onValueChanged.
    /// </remarks>
    [AddComponentMenu("UI/Constrained Slider 2D")]
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform))]
    public class ConstrainedSlider2D : Selectable, IDragHandler, IInitializePotentialDragHandler, ICanvasElement
    {
        [Serializable]
        public class ConstrainedSlider2DEvent : UnityEvent<Vector2> { }

        [SerializeField]
        private RectTransform? handleRect;

        public RectTransform? HandleRect
        {
            get => handleRect;
            set
            {
                if (handleRect == value) return;
                handleRect = value;
                UpdateCachedReferences();
                UpdateVisuals();
            }
        }

        [Space]

        [SerializeField]
        private ConstrainedSlider2DEvent onValueChanged = new ConstrainedSlider2DEvent();

        public ConstrainedSlider2DEvent OnValueChanged
        {
            get => onValueChanged;
            set => onValueChanged = value;
        }

        /// <summary>
        /// External function to constrain the normalized position (Vector2 in [0,1] range).
        /// Defaults to identity (no constraint).
        /// </summary>
        public System.Func<Vector2, Vector2> ConstrainFunction { get; set; } = pos => pos;

        // Private fields
        private RectTransform? _handleContainerRect;
        private Vector2 _offset = Vector2.zero;

        // field is never assigned warning
#pragma warning disable 649
        private DrivenRectTransformTracker _tracker;
#pragma warning restore 649

        // This "delayed" mechanism is required for case 1037681.
        private bool _delayedUpdateVisuals;

        [SerializeField]
        protected Vector2 value = new(0.5f, 0.5f);

        /// <summary>
        /// Normalized value (0-1 in both axes) representing handle position fraction within the container.
        /// </summary>
        public virtual Vector2 Value
        {
            get => value;
            set => Set(value);
        }

        public virtual void SetValueWithoutNotify(Vector2 input)
        {
            Set(input, false);
        }

        protected ConstrainedSlider2D() { }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();

            // OnValidate is called before OnEnable. We need to make sure not to touch any other objects before OnEnable is run.
            if (IsActive())
            {
                UpdateCachedReferences();
                // Update rects in next update since other things might affect them even if value didn't change.
                _delayedUpdateVisuals = true;
            }

            if (!PrefabUtility.IsPartOfPrefabAsset(this) && !Application.isPlaying)
                CanvasUpdateRegistry.RegisterCanvasElementForLayoutRebuild(this);
        }
#endif

        public virtual void Rebuild(CanvasUpdate executing)
        {
#if UNITY_EDITOR
            if (executing == CanvasUpdate.Prelayout)
                OnValueChanged.Invoke(Value);
#endif
        }

        /// <summary>
        /// See ICanvasElement.LayoutComplete
        /// </summary>
        public virtual void LayoutComplete() { }

        /// <summary>
        /// See ICanvasElement.GraphicUpdateComplete
        /// </summary>
        public virtual void GraphicUpdateComplete() { }

        protected override void OnEnable()
        {
            base.OnEnable();
            UpdateCachedReferences();
            Set(value, false);
            // Update rects since they need to be initialized correctly.
            UpdateVisuals();
#if UNITY_EDITOR
            Undo.undoRedoEvent -= OnUndoRedoEvent;
            Undo.undoRedoEvent += OnUndoRedoEvent;
#endif
        }

        protected override void OnDisable()
        {
#if UNITY_EDITOR
            Undo.undoRedoEvent -= OnUndoRedoEvent;
#endif
            _tracker.Clear();
            base.OnDisable();
        }

        /// <summary>
        /// Update the rect based on the delayed update visuals.
        /// Got around issue of calling SendMessage from OnValidate.
        /// </summary>
        protected virtual void Update()
        {
            if (_delayedUpdateVisuals)
            {
                _delayedUpdateVisuals = false;
                Set(value, false);
                UpdateVisuals();
            }
        }

        protected override void OnDidApplyAnimationProperties()
        {
            Vector2 animatedValue = ComputeValueFromVisuals();
            if (animatedValue != value)
            {
                value = animatedValue;
                OnValueChanged.Invoke(value);
            }
            base.OnDidApplyAnimationProperties();
        }

        private Vector2 ComputeValueFromVisuals()
        {
            if (_handleContainerRect is null || handleRect is null) return value;
            Vector2 size = _handleContainerRect.rect.size;
            if (size == Vector2.zero) return value;
            Vector2 delta = handleRect.anchoredPosition;
            return new Vector2(
                delta.x / size.x + 0.5f,
                delta.y / size.y + 0.5f
            );
        }

        void UpdateCachedReferences()
        {
            if (handleRect && handleRect != (RectTransform)transform)
            {
                _handleContainerRect = handleRect.parent as RectTransform;
            }
            else
            {
                handleRect = null;
                _handleContainerRect = null;
            }
        }

        protected virtual void Set(Vector2 input, bool sendCallback = true)
        {
            // Apply constraint and clamp
            Vector2 newValue = ConstrainFunction(input);
            newValue.x = Mathf.Clamp01(newValue.x);
            newValue.y = Mathf.Clamp01(newValue.y);

            if (value == newValue)
                return;

            value = newValue;
            UpdateVisuals();
            if (sendCallback)
            {
                UISystemProfilerApi.AddMarker("ConstrainedHandle.value", this);
                onValueChanged.Invoke(newValue);
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();

            // This can be invoked before OnEnable is called. So we shouldn't be accessing other objects before OnEnable is called.
            if (!IsActive())
                return;

            UpdateVisuals();
        }

#if UNITY_EDITOR
        void OnUndoRedoEvent(in UndoRedoInfo undo)
        {
            UpdateVisuals();
        }
#endif

        // Force-update the handle. Useful if you've changed the properties and want it to update visually.
        private void UpdateVisuals()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UpdateCachedReferences();
#endif

            _tracker.Clear();

            if (_handleContainerRect != null && handleRect != null)
            {
                _tracker.Add(this, handleRect, DrivenTransformProperties.AnchoredPosition);
                Vector2 size = _handleContainerRect.rect.size;
                if (size != Vector2.zero)
                {
                    Vector2 delta = (value - new Vector2(0.5f, 0.5f)) * size;
                    handleRect.anchoredPosition = delta;
                }
            }
        }

        // Update the handle's position based on the mouse.
        void UpdateDrag(PointerEventData eventData, Camera cam)
        {
            RectTransform? containerRect = _handleContainerRect;
            if (containerRect == null) return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(containerRect, eventData.position, cam, out var localCursor))
                return;

            Vector2 candidateAnchored = localCursor - _offset;
            Vector2 size = containerRect.rect.size;
            if (size == Vector2.zero) return;

            Vector2 candidateNorm = new Vector2(
                candidateAnchored.x / size.x + 0.5f,
                candidateAnchored.y / size.y + 0.5f
            );

            // Apply constraint here as well for drag consistency
            Vector2 constrainedNorm = ConstrainFunction(candidateNorm);
            constrainedNorm.x = Mathf.Clamp01(constrainedNorm.x);
            constrainedNorm.y = Mathf.Clamp01(constrainedNorm.y);

            Value = constrainedNorm;
        }

        private bool MayDrag(PointerEventData eventData)
        {
            return IsActive() && IsInteractable() && eventData.button == PointerEventData.InputButton.Left;
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            if (!MayDrag(eventData))
                return;

            base.OnPointerDown(eventData);

            _offset = Vector2.zero;
            Camera cam = eventData.pressEventCamera;
            if (handleRect != null && RectTransformUtility.RectangleContainsScreenPoint(handleRect, eventData.pointerPressRaycast.screenPosition, cam))
            {
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(handleRect, eventData.pointerPressRaycast.screenPosition, cam, out var localMousePos))
                    _offset = localMousePos;
            }
            else
            {
                // Outside the handle - jump to this point instead
                UpdateDrag(eventData, cam);
            }
        }

        public virtual void OnDrag(PointerEventData eventData)
        {
            if (!MayDrag(eventData))
                return;
            UpdateDrag(eventData, eventData.pressEventCamera);
        }

        public override void OnMove(AxisEventData eventData)
        {
            if (!IsActive() || !IsInteractable())
            {
                base.OnMove(eventData);
                return;
            }

            Set(value + eventData.moveVector * .05f);
        }

        public virtual void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }
    }
}