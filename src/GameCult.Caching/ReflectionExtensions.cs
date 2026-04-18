using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GameCult.Caching
{
    public static class ReflectionExtensions
    {
        private static Dictionary<Type, Type[]> ParentTypes = new Dictionary<Type, Type[]>();

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
        public static Type[] GetAllChildClasses(this Type type)
        {
            if (ChildClasses.ContainsKey(type))
                return ChildClasses[type];
            return ChildClasses[type] = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(ass => ass.GetTypes()).Where(type.IsAssignableFrom).ToArray();
        }

    }
}