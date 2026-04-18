using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameCult.Unity.UI
{
    /// <summary>
    /// Convenience helper that lets you create "prototype" GameObjects in-scene which act like prefabs:<br/>
    ///   - The prototype (root) exists in the scene and deactivates itself on start.<br/>
    ///   - You can Instantiate&lt;T&gt;() to get an active copy in the same parent/transform space.<br/>
    ///   - When finished, call ReturnToPool() on the instance to return it to the prototype's pool
    ///     so it can be reused instead of being destroyed and recreated.
    /// </summary>
    public class Prototype : MonoBehaviour
    {
        /// <summary>
        /// Fired when an instance is returned into the prototype's pool (after it is deactivated).
        /// </summary>
        public event Action? OnReturnToPool;

        // True when this component is the prototype/root (i.e. not a pooled instance).
        public bool IsPrototype => _rootPrototype is null;

        // Always returns the prototype/root for this object. If this object is the prototype, it returns itself.
        public Prototype RootPrototype => _rootPrototype ?? this;

        private void Start()
        {
            // Prototype objects remain inactive in the scene by default.
            if (IsPrototype)
                gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            // When the prototype is destroyed, clean up pooled instances too.
            if (_instancePool != null)
            {
                foreach (var instance in _instancePool)
                    Destroy(instance.gameObject);
            }
        }

        /// <summary>
        /// Instantiate a copy of this Prototype and return the requested component T from the instance.
        /// If there is an available instance in the pool, reuse it.
        /// </summary>
        public T Instantiate<T>() where T : Component
        {
            Prototype instance;

            // Re-use from pool if possible (pool belongs to the prototype/root).
            var poolOwner = RootPrototype;
            if (poolOwner._instancePool != null && poolOwner._instancePool.Count > 0)
            {
                var lastIndex = poolOwner._instancePool.Count - 1;
                instance = poolOwner._instancePool[lastIndex];
                poolOwner._instancePool.RemoveAt(lastIndex);
                instance.transform.SetAsLastSibling();
            }
            else
            {
                // Create a fresh instance from this object (clone entire GameObject).
                // Place it under the same parent as the prototype.
                instance = UnityEngine.Object.Instantiate(this, transform.parent, false);

                // For UI RectTransforms, copy anchors/pivot/sizeDelta so layout matches.
                if (transform is RectTransform protoRT &&
                    instance.transform is RectTransform instRT)
                {
                    instRT.anchorMin = protoRT.anchorMin;
                    instRT.anchorMax = protoRT.anchorMax;
                    instRT.pivot = protoRT.pivot;
                    instRT.sizeDelta = protoRT.sizeDelta;
                }

                // Match local transform values exactly.
                instance.transform.localPosition = transform.localPosition;
                instance.transform.localRotation = transform.localRotation;
                instance.transform.localScale = transform.localScale;

                // Mark this new copy as an instance of this prototype.
                instance._rootPrototype = this;
            }

            instance.gameObject.SetActive(true);
            return instance.GetComponent<T>();
        }

        /// <summary>
        /// Return this instance to its prototype's pool for reuse.
        /// Can only be called on an instance (not on the prototype/root itself).
        /// </summary>
        public void ReturnToPool()
        {
            if (IsPrototype)
            {
                Debug.LogError($"Cannot ReturnToPool: '{name}' appears to be the prototype root (it has no root prototype). " +
                               "Only instantiated instances can be returned to a pool.");
                Destroy(gameObject);
                return;
            }

            // Move instance back under the prototype's parent and add to pool.
            transform.SetParent(RootPrototype.transform.parent);
            RootPrototype.AddToPool(this);

            // Clear any handlers to avoid leaking references while pooled.
            OnReturnToPool = null;
        }

        /// <summary>
        /// Get the requested component on the prototype (if this is the prototype) or on the root prototype.
        /// </summary>
        public T GetOriginal<T>()
        {
            return RootPrototype.GetComponent<T>();
        }

        /// <summary>
        /// Adds an instance back into this prototype's pool.
        /// This method is internal to the prototype; it should only be called on the prototype/root.
        /// </summary>
        private void AddToPool(Prototype instance)
        {
            if (!IsPrototype)
            {
                Debug.LogError($"Attempting to add '{instance.name}' to the pool of '{name}', but '{name}' is not the prototype root.");
                // still attempt to add to the real root if available
                if (_rootPrototype is not null)
                {
                    _rootPrototype.AddToPool(instance);
                    return;
                }
            }

            instance.gameObject.SetActive(false);

            _instancePool ??= new List<Prototype>();
            _instancePool.Add(instance);

            // Notify any subscribers that this instance has returned to the pool.
            instance.OnReturnToPool?.Invoke();
        }

        // Reference to the prototype/root. Non-null on instances; null on the prototype itself.
        private Prototype? _rootPrototype;

        // Pool of instances owned by the prototype/root.
        private List<Prototype>? _instancePool;
    }
}