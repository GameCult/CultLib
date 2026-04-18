using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameCult.Caching.NewtonsoftJson;

/// <summary>
/// Persists all cache entries to a single JSON file using Newtonsoft.Json.
/// </summary>
public class SingleFileNewtonsoftJsonBackingStore : SingleFileBackingStore
{
    private readonly KnownDatabaseEntryTypes _knownTypes = new();

    /// <summary>
    /// Initializes a single-file Newtonsoft.Json backing store.
    /// </summary>
    /// <param name="filePath">The path to the backing file.</param>
    public SingleFileNewtonsoftJsonBackingStore(string filePath) : base(filePath)
    {
    }

    /// <summary>
    /// Registers a concrete <see cref="DatabaseEntry"/> type for serialization.
    /// </summary>
    /// <typeparam name="T">The entry type to register.</typeparam>
    public void RegisterType<T>() where T : DatabaseEntry
    {
        _knownTypes.RegisterType(typeof(T));
    }

    /// <summary>
    /// Registers all concrete <see cref="DatabaseEntry"/> types from the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    public void RegisterAssembly(Assembly assembly)
    {
        _knownTypes.RegisterAssembly(assembly);
    }

    /// <summary>
    /// Rebuilds the known-type map from the currently loaded assemblies.
    /// </summary>
    public void RefreshKnownTypes()
    {
        _knownTypes.RegisterLoadedAssemblies();
    }

    /// <inheritdoc />
    public override byte[] Serialize(DatabaseEntry[] entries)
    {
        var serializer = JsonSerializer.Create(KnownDatabaseEntryTypes.SerializerSettings);
        var payload = entries.Select(entry => _knownTypes.ToEnvelope(entry, serializer)).ToArray();
        var json = JsonConvert.SerializeObject(payload, KnownDatabaseEntryTypes.SerializerSettings);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <inheritdoc />
    public override DatabaseEntry[] Deserialize(byte[] data)
    {
        _knownTypes.RegisterLoadedAssemblies();

        var json = Encoding.UTF8.GetString(data);
        var serializer = JsonSerializer.Create(KnownDatabaseEntryTypes.SerializerSettings);
        var payload = JsonConvert.DeserializeObject<DatabaseEntryEnvelope[]>(json, KnownDatabaseEntryTypes.SerializerSettings) ?? [];
        return payload.Select(envelope => _knownTypes.FromEnvelope(envelope, serializer)).ToArray();
    }
}

/// <summary>
/// Persists each cache entry to its own JSON file using Newtonsoft.Json.
/// </summary>
public class MultiFileNewtonsoftJsonBackingStore : MultiFileBackingStore
{
    private readonly KnownDatabaseEntryTypes _knownTypes = new();

    /// <summary>
    /// Initializes a multi-file Newtonsoft.Json backing store rooted at the supplied directory.
    /// </summary>
    /// <param name="path">The root directory for stored entry files.</param>
    public MultiFileNewtonsoftJsonBackingStore(string path) : base(path)
    {
    }

    /// <summary>
    /// Registers a concrete <see cref="DatabaseEntry"/> type for serialization.
    /// </summary>
    /// <typeparam name="T">The entry type to register.</typeparam>
    public void RegisterType<T>() where T : DatabaseEntry
    {
        _knownTypes.RegisterType(typeof(T));
    }

    /// <summary>
    /// Registers all concrete <see cref="DatabaseEntry"/> types from the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    public void RegisterAssembly(Assembly assembly)
    {
        _knownTypes.RegisterAssembly(assembly);
    }

    /// <summary>
    /// Rebuilds the known-type map from the currently loaded assemblies.
    /// </summary>
    public void RefreshKnownTypes()
    {
        _knownTypes.RegisterLoadedAssemblies();
    }

    /// <inheritdoc />
    public override byte[] Serialize(DatabaseEntry entry)
    {
        var serializer = JsonSerializer.Create(KnownDatabaseEntryTypes.SerializerSettings);
        var payload = _knownTypes.ToEnvelope(entry, serializer);
        var json = JsonConvert.SerializeObject(payload, KnownDatabaseEntryTypes.SerializerSettings);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <inheritdoc />
    public override DatabaseEntry Deserialize(byte[] data)
    {
        _knownTypes.RegisterLoadedAssemblies();

        var json = Encoding.UTF8.GetString(data);
        var serializer = JsonSerializer.Create(KnownDatabaseEntryTypes.SerializerSettings);
        var payload = JsonConvert.DeserializeObject<DatabaseEntryEnvelope>(json, KnownDatabaseEntryTypes.SerializerSettings)
                      ?? throw new JsonSerializationException("Failed to deserialize DatabaseEntry envelope.");
        return _knownTypes.FromEnvelope(payload, serializer);
    }

    /// <inheritdoc />
    public override string Extension => "json";
}

internal sealed class KnownDatabaseEntryTypes
{
    internal static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented
    };

    private readonly ConcurrentDictionary<string, Type> _discriminatorToType = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, string> _typeToDiscriminator = new();

    public KnownDatabaseEntryTypes()
    {
        RegisterLoadedAssemblies();
    }

    public void RegisterType(Type type)
    {
        if (!typeof(DatabaseEntry).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new ArgumentException($"Type '{type.FullName}' must be a non-abstract DatabaseEntry subtype.", nameof(type));
        }

        var discriminator = GetDiscriminator(type);
        _discriminatorToType[discriminator] = type;
        _typeToDiscriminator[type] = discriminator;
    }

    public void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in SafeGetTypes(assembly).Where(IsConcreteDatabaseEntry))
        {
            RegisterType(type);
        }
    }

    public void RegisterLoadedAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            RegisterAssembly(assembly);
        }
    }

    public DatabaseEntryEnvelope ToEnvelope(DatabaseEntry entry, JsonSerializer serializer)
    {
        var type = entry.GetType();
        if (!_typeToDiscriminator.TryGetValue(type, out var discriminator))
        {
            RegisterType(type);
            discriminator = _typeToDiscriminator[type];
        }

        return new DatabaseEntryEnvelope
        {
            Type = discriminator,
            Data = JObject.FromObject(entry, serializer)
        };
    }

    public DatabaseEntry FromEnvelope(DatabaseEntryEnvelope envelope, JsonSerializer serializer)
    {
        if (!_discriminatorToType.TryGetValue(envelope.Type, out var type))
        {
            throw new JsonSerializationException($"Unknown DatabaseEntry discriminator '{envelope.Type}'. Register the containing assembly or entry type before deserializing.");
        }

        return (DatabaseEntry?)envelope.Data.ToObject(type, serializer)
               ?? throw new JsonSerializationException($"Failed to deserialize DatabaseEntry discriminator '{envelope.Type}'.");
    }

    private static bool IsConcreteDatabaseEntry(Type type)
    {
        return typeof(DatabaseEntry).IsAssignableFrom(type) && !type.IsAbstract;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
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

    private static string GetDiscriminator(Type type)
    {
        return $"{type.Assembly.GetName().Name}:{type.FullName}";
    }
}

internal sealed class DatabaseEntryEnvelope
{
    public string Type { get; set; } = string.Empty;

    public JObject Data { get; set; } = new();
}
