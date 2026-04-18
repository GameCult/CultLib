using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Unity.UI.Components;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameCult.Unity.UI
{
    [CreateAssetMenu(menuName="Inspector/Reflective Resolver")]
    public class ReflectiveResolver : ScriptableObject, IUIResolver
    {
        [SerializeField] private List<LayoutComponent> fieldPrefabs = new();
        [SerializeField] private List<ResolverComponent> componentPrefabs = new();
        [SerializeField] private DisplayOptions displayOptions;

        private List<IFieldHandler> _handlers = new();

        private void Awake()
        {
            foreach (var prefab in fieldPrefabs)
            {
                if (prefab is not IFieldHandler)
                {
                    Debug.LogWarning($"Prefab {prefab.gameObject.name} in Field Prefabs is not IFieldHandler");
                }
                _handlers.Add((IFieldHandler)prefab);
            }
            _handlers = _handlers.OrderByDescending(h => h.Priority).ToList();
        }

        public IFieldHandler? ResolveField(Type type, PreferredInspectorAttribute? attribute)
        {
            var match = _handlers.FirstOrDefault(h => h.CanHandle(type, attribute));
            if (match != null) return (IFieldHandler)Instantiate((LayoutComponent)match);
            Debug.LogWarning($"{name} unable to resolve {type}" +
                             (attribute != null ? $" with attribute {attribute.GetType().Name}" : ""));
            return null;
        }

        public T? Resolve<T>(string? prefabName = null) where T : ResolverComponent
        {
            foreach (var component in componentPrefabs)
            {
                if (component is T t && (prefabName==null || t.gameObject.name.StartsWith(prefabName))) return Instantiate(t);
            }
            foreach (var field in fieldPrefabs)
            {
                if (field is T t && (prefabName==null || t.gameObject.name.StartsWith(prefabName))) return Instantiate(t);
            }
            
            Debug.LogWarning($"{name} unable to resolve {typeof(T).Name}" + (prefabName != null ? $" with name {prefabName}" : ""));

            return null;
        }
    }
}