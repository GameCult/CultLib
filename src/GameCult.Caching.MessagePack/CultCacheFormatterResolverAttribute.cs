using System;

namespace GameCult.Caching.MessagePack;

// The resolver type exposes a public static Instance of IFormatterResolver or a public parameterless constructor.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class CultCacheFormatterResolverAttribute : Attribute
{
    public CultCacheFormatterResolverAttribute(Type resolverType)
    {
        ResolverType = resolverType ?? throw new ArgumentNullException(nameof(resolverType));
    }

    public Type ResolverType { get; }
}
