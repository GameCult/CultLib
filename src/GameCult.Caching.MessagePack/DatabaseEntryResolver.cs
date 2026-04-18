using System;
using System.Collections.Concurrent;
using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching.MessagePack
{
    /// <summary>
    /// Resolver that knows how to resolve:<br/>
    ///  1. Source-generated singleton formatters for DatabaseEntry subclasses<br/>
    ///  2. Generic DatabaseLink&lt;T&gt; formatter
    /// </summary>
    public sealed class DatabaseEntryResolver : IFormatterResolver
    {
        /// <summary>
        /// Shared singleton instance of the resolver.
        /// </summary>
        public static readonly DatabaseEntryResolver Instance = new DatabaseEntryResolver();

        private readonly ConcurrentDictionary<Type, object> _formatterCache = new();

        private DatabaseEntryResolver() { }

        /// <summary>
        /// Resolves a formatter for a cache entry type or a <see cref="DatabaseLink{T}"/>.
        /// </summary>
        /// <typeparam name="T">The requested serialized type.</typeparam>
        /// <returns>The formatter if one is available; otherwise, <c>null</c>.</returns>
        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            // 1. Check generated resolver mapping
            var fmt = GeneratedDatabaseEntryResolvers.GetFormatter<T>();
            if (fmt != null) return fmt;

            // 2. Provide generic DatabaseLink<T> formatter
            var t = typeof(T);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DatabaseLink<>))
            {
                return (IMessagePackFormatter<T>)_formatterCache.GetOrAdd(
                    t,
                    static linkType =>
                    {
                        var valueType = linkType.GetGenericArguments()[0];
                        var formatterType = typeof(DatabaseLinkFormatter<>).MakeGenericType(valueType);
                        return Activator.CreateInstance(formatterType)!;
                    });
            }

            // 3. Nothing found
            return null;
        }
    }
}
