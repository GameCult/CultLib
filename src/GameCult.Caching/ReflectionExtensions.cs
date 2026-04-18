using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameCult.Caching
{
    /// <summary>
    /// Provides cached reflection helpers used by the cache infrastructure.
    /// </summary>
    public static class ReflectionExtensions
    {
        private static Dictionary<Type, Type[]> ParentTypes = new Dictionary<Type, Type[]>();

        /// <summary>
        /// Gets all base types for the supplied type, caching the result.
        /// </summary>
        /// <param name="type">The type whose parent types should be returned.</param>
        /// <returns>An array of base types ordered from immediate parent upward.</returns>
        public static Type[] GetParentTypes(this Type type)
        {
            if (ParentTypes.ContainsKey(type))
                return ParentTypes[type];
            return ParentTypes[type] = type.GetParents().ToArray();
        }

        private static IEnumerable<Type> GetParents(this Type type)
        {
            // is there any base type?
            if (type == null)
            {
                yield break;
            }

            // return all inherited types
            var currentBaseType = type.BaseType;
            while (currentBaseType != null)
            {
                yield return currentBaseType;
                currentBaseType = currentBaseType.BaseType;
            }
        }

        private static Dictionary<Type, Type[]> ChildClasses = new Dictionary<Type, Type[]>();

        /// <summary>
        /// Gets all loaded types assignable to the supplied type, caching the result.
        /// </summary>
        /// <param name="type">The base type or interface to search from.</param>
        /// <returns>An array of matching loaded types.</returns>
        public static Type[] GetAllChildClasses(this Type type)
        {
            if (ChildClasses.ContainsKey(type))
                return ChildClasses[type];
            return ChildClasses[type] = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(ass => ass.GetTypes()).Where(type.IsAssignableFrom).ToArray();
        }
    }
}
