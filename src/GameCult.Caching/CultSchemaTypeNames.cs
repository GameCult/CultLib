using System;
using System.Linq;
using System.Text;

namespace GameCult.Caching
{
    internal static class CultSchemaTypeNames
    {
        public static string FromType(Type type)
        {
            if (type.IsArray)
            {
                return FromType(type.GetElementType()!) + "[]";
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                var name = definition.FullName ?? definition.Name;
                var tickIndex = name.IndexOf('`');
                if (tickIndex >= 0)
                {
                    name = name[..tickIndex];
                }

                var arguments = string.Join(", ", type.GetGenericArguments().Select(FromType));
                return $"{name}<{arguments}>";
            }

            return type.FullName ?? type.Name;
        }

        public static string EscapeForLiteral(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(character switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    _ => character.ToString()
                });
            }

            return builder.ToString();
        }
    }
}
