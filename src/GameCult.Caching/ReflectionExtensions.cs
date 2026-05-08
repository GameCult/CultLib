using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GameCult.Caching
{
    internal static class ReflectionExtensions
    {
        public static IEnumerable<Type> GetAttributedDocumentTypes()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(SafeGetTypes)
                .Where(type => type is { IsAbstract: false, IsInterface: false })
                .Where(type => type.GetCustomAttribute<CultDocumentAttribute>() != null);
        }

        public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null)!;
            }
        }
    }
}
