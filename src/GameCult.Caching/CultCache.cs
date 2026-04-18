using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using GameCult.Logging;
using R3;

namespace GameCult.Caching
{
    /// <summary>
    /// Specifies that the decorated class contains global settings.
    /// Use this attribute to mark classes intended to hold application-wide configuration or settings.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class GlobalSettingsAttribute : Attribute { }
    
    /// <summary>
    /// Represents an in-memory and persistent cache for <see cref="DatabaseEntry"/> objects, supporting global entries,
    /// named entries, generic field indexes, and multiple backing stores. Provides thread-safe operations for adding,
    /// retrieving, indexing, and removing entries, with support for event notifications and logging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Features:</b>
    /// <list type="bullet">
    /// <item>Supports multiple <see cref="CacheBackingStore"/>s for persistence and synchronization.</item>
    /// <item>Handles global settings entries via <see cref="GlobalSettingsAttribute"/>.</item>
    /// <item>Provides fast lookup by ID, name (for <see cref="INamedEntry"/>), and indexed fields.</item>
    /// <item>Allows registering generic indexes for any public field of a <see cref="DatabaseEntry"/> type.</item>
    /// <item>Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/> and <see cref="ConcurrentHashSet{T}"/>.</item>
    /// <item>Notifies on entry updates via <see cref="OnUpdate"/> event.</item>
    /// <item>Supports both synchronous and asynchronous add operations.</item>
    /// <item>Integrates with <see cref="ILogger"/> for error and event logging.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// <code>
    /// var cache = new CultCache();
    /// cache.RegisterIndex&lt;PlayerData&gt;("Email");
    /// cache.AddAsync(new PlayerData { ... }).GetAwaiter().GetResult();
    /// var player = cache.GetByIndex&lt;PlayerData&gt;("Email", "user@example.com");
    /// </code>
    /// </para>
    /// </remarks>
    public class CultCache : IDisposable
    {
        private readonly object addLock = new object(); // Retained for synchronous Add compatibility

        /// <summary>
        /// Occurs when a <see cref="DatabaseEntry"/> is updated.
        /// The event provides the old and new <see cref="DatabaseEntry"/> instances.
        /// </summary>
        /// <remarks>
        /// The first parameter is the previous entry, and the second parameter is the updated entry.
        /// The first parameter will be <c>null</c> if the entry is newly added,
        /// and the second parameter will be <c>null</c> if the entry is deleted.
        /// </remarks>
        public event Action<DatabaseEntry?, DatabaseEntry?>? OnUpdate;

        private readonly List<CacheBackingStore> _backingStores = new();
        private readonly ConcurrentDictionary<Type, CacheBackingStore> _typeStores = new();
        private readonly ConcurrentDictionary<CacheBackingStore, Type[]> _storeTypes = new();
        private readonly ConcurrentDictionary<Guid, DatabaseEntry> _entries = new();
        private readonly ConcurrentDictionary<Type, DatabaseEntry?> _globals = new();
        private readonly ConcurrentDictionary<Type, ConcurrentHashSet<DatabaseEntry>> _types = new();
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, Guid>> _nameToIdMap = new();
        private readonly ConcurrentDictionary<long, Task> _pendingStoreOperations = new();
        // Generic indexes: Map (Type, FieldName) -> (Value -> ID). Allows registering indexes for any field
        private readonly ConcurrentDictionary<(Type Type, string FieldName), ConcurrentDictionary<string, Guid>> _indexes = new();
        // Cache getters per type (list of (field, getter) for that type only). Avoids scanning all indexes.
        private readonly ConcurrentDictionary<Type, List<(string FieldName, Func<DatabaseEntry, string> Getter)>> _typeToGetters = new();
        private ILogger _logger = new NullLogger();
        private long _storeOperationId;
        
        /// <summary>
        /// Gets or sets the <see cref="ILogger"/> instance used for logging within the cache.
        /// If set to <c>null</c>, a <see cref="NullLogger"/> will be used as a fallback.
        /// </summary>
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }

        /// <summary>
        /// Gets all <see cref="DatabaseEntry"/> objects currently stored in the cache.
        /// </summary>
        public IEnumerable<DatabaseEntry> AllEntries => _entries.Values;

        /// <summary>
        /// Initializes a new instance of the <see cref="CultCache"/> class.
        /// Sets up the cache by registering all child classes of <see cref="DatabaseEntry"/>.
        /// For each child type:
        /// <list type="bullet">
        /// <item>Adds a concurrent hash set for caching entries of that type.</item>
        /// <item>If the type is marked with <see cref="GlobalSettingsAttribute"/>, adds it to the global settings and creates an instance.</item>
        /// <item>If the type implements <see cref="INamedEntry"/>, initializes a name-to-ID mapping dictionary.</item>
        /// </list>
        /// Also sets the static cache reference in <see cref="DatabaseLinkBase"/>.
        /// </summary>
        public CultCache()
        {
            DatabaseLinkBase.Cache = this;

            foreach (var type in typeof(DatabaseEntry).GetAllChildClasses())
            {
                _types.TryAdd(type, new ConcurrentHashSet<DatabaseEntry>());
                if (type.GetCustomAttribute<GlobalSettingsAttribute>() != null)
                {
                    _globals.TryAdd(type, null);
                    try
                    {
                        var global = (DatabaseEntry)Activator.CreateInstance(type)!;
                        AddAsync(global).GetAwaiter().GetResult();
                        _globals[type] = global;  // Update after successful add
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to initialize global {type.Name}: {ex.Message}");
                        _globals[type] = null;
                    }
                }
                // Initialize name-to-ID map for INamedEntry types
                if (typeof(INamedEntry).IsAssignableFrom(type))
                {
                    _nameToIdMap.TryAdd(type, new ConcurrentDictionary<string, Guid>());
                }
            }
        }

        /// <summary>
        /// Adds a backing store to the cache system and configures domain type associations and event subscriptions.
        /// </summary>
        /// <param name="store">The <see cref="CacheBackingStore"/> instance to add.</param>
        /// <param name="domain">
        /// Optional array of <see cref="Type"/> objects representing domain types associated with the backing store.
        /// If provided, the store is mapped to these types; otherwise, the store is added to the general backing store list and subscribes to existing stores.
        /// </param>
        /// <remarks>
        /// <para>
        /// If no domain types are specified, the store subscribes to all existing backing stores to receive their updates,
        /// ensuring synchronization across stores, with the first store added acting as the primary source of truth.
        /// </para>
        /// <para>
        /// Specifying domain types allows for targeted storage and retrieval of specific <see cref="DatabaseEntry"/> types,
        /// for example, using a database store for user data and a file-based store for configuration data or game assets.
        /// </para>
        /// </remarks>
        public void AddBackingStore(CacheBackingStore store, params Type[] domain)
        {
            if (domain.Length > 0)
            {
                _storeTypes.TryAdd(store, domain);
                foreach (var t in domain) _typeStores.TryAdd(t, store);
            }
            else
            {
                foreach (var existingStore in _backingStores) store.SubscribeTo(existingStore);
                _backingStores.Add(store);
            }
            store.EntryAdded.Subscribe(entry => TrackStoreOperation(async () =>
            {
                try // Try-catch logs without swallowing. Added to EntryDeleted for consistency (even though sync)
                {
                    await AddAsync(entry, store);
                    OnUpdate?.Invoke(null, entry);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"EntryAdded processing failed for {entry?.ID}: {ex.Message}");
                }
            }));
            store.EntryUpdated.Subscribe(entry => TrackStoreOperation(async () =>
            {
                try
                {
                    await AddAsync(entry, store);
                    OnUpdate?.Invoke(entry, entry);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"EntryUpdated processing failed for {entry?.ID}: {ex.Message}");
                }
            }));
            store.EntryDeleted.Subscribe(entry => TrackStoreOperation(() =>
            {
                try
                {
                    Remove(entry, store);
                    OnUpdate?.Invoke(entry, null);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"EntryDeleted processing failed for {entry?.ID}: {ex.Message}");
                }

                return Task.CompletedTask;
            }));
            store.Logger = Logger;
        }
        
        /// <summary>
        /// Asynchronously pulls all data from the backing stores.
        /// Initiates parallel tasks to execute the <c>PullAll</c> method on each backing store and store type,
        /// and waits for all tasks to complete.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public async Task PullAllBackingStoresAsync()
        {
            var tasks = _backingStores.Select(store => Task.Run(store.PullAll))
                .Concat(_storeTypes.Keys.Select(store => Task.Run(store.PullAll)))
                .ToArray();
            await Task.WhenAll(tasks);
            await WaitForPendingStoreOperationsAsync();
        }

        /// <summary>
        /// Adds a <see cref="DatabaseEntry"/> to the cache asynchronously, updating all relevant maps and indexes.
        /// If the entry already exists, cleans up old mappings before updating.
        /// Also persists the entry to the backing store if available.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to add or update in the cache.</param>
        /// <param name="source">
        /// The <see cref="CacheBackingStore"/> source from which the entry originated, used internally to avoid redundant writes.
        /// Optional; defaults to <c>null</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public async Task AddAsync(DatabaseEntry entry, CacheBackingStore? source = null)
        {
            if (entry == null) return;

            var type = entry.GetType();

            try
            {
                if (_typeStores.TryGetValue(type, out var store))
                {
                    if (store != source)
                        await Task.Run(() => store.Push(entry));
                }
                else if (_backingStores.Any() && _backingStores.First() != source)
                {
                    await Task.Run(() => _backingStores.First().Push(entry));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to persist entry {entry.ID}: {ex.Message}");
                throw;
            }

            // Pre-fetch existing to detect updates (for map/index cleanup)
            if (!_entries.TryGetValue(entry.ID, out var existing)) existing = null;

            // Cleanup old maps/indexes BEFORE updating _entries
            if (existing != null)
            {
                // For name map (if updating existing)
                if (existing is INamedEntry existingNamed && _nameToIdMap.TryGetValue(type, out var nameMap))
                {
                    nameMap.TryRemove(existingNamed.EntryName, out _);
                }

                // For generic indexes (if registered)
                if (_typeToGetters.TryGetValue(type, out var getters))
                {
                    foreach (var (fieldName, getter) in getters)
                    {
                        var oldValue = getter(existing);
                        if (!string.IsNullOrEmpty(oldValue))
                        {
                            var indexKey = (type, fieldName);
                            _indexes[indexKey].TryRemove(oldValue, out _);
                        }
                    }
                }
                
                // For type sets
                _types[existing.GetType()].TryRemove(existing);
                foreach (var parentType in existing.GetType().GetParentTypes())
                {
                    if (_types.TryGetValue(parentType, out var typeSet))
                        typeSet.TryRemove(existing);
                }
            }

            _entries[entry.ID] = entry;
            _types[type].Add(entry);  // Assumes exists; throws if not (edge case)
            foreach (var parentType in type.GetParentTypes())
            {
                if (_types.TryGetValue(parentType, out var typeSet))
                    typeSet.Add(entry);
            }

            if (_globals.ContainsKey(type))
            {
                _globals.AddOrUpdate(type, entry, (_, old) => entry);
            }

            // Add new values (always, for both new and updates)
            // Update name-to-ID map if INamedEntry
            if (entry is INamedEntry namedEntry && _nameToIdMap.TryGetValue(type, out var nameMap2))
            {
                nameMap2[namedEntry.EntryName] = entry.ID;
            }

            // Update generic indexes if registered for this type
            if (_typeToGetters.TryGetValue(type, out var getters2))
            {
                foreach (var (fieldName, getter) in getters2)
                {
                    var value = getter(entry);
                    if (!string.IsNullOrEmpty(value))
                    {
                        var indexKey = (type, fieldName);
                        _indexes[indexKey][value] = entry.ID;
                    }
                }
            }

        }

        /// <summary>
        /// Determines whether the specified <see cref="DatabaseEntry"/> is a global settings entry.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to check.</param>
        /// <returns><c>true</c> if the entry is global; otherwise, <c>false</c>.</returns>
        public bool IsGlobal(DatabaseEntry entry) => _globals.ContainsKey(entry.GetType());

        /// <summary>
        /// Adds all <see cref="DatabaseEntry"/> objects from the specified collection to the cache asynchronously.
        /// </summary>
        /// <param name="entries">The collection of <see cref="DatabaseEntry"/> objects to add.</param>
        /// <param name="source">
        /// The <see cref="CacheBackingStore"/> source from which the entries originated, used to avoid redundant writes.
        /// Optional; defaults to <c>null</c>.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public Task AddAllAsync(IEnumerable<DatabaseEntry> entries, CacheBackingStore? source = null)
        {
            var tasks = entries.Select(entry => AddAsync(entry, source)).ToArray();
            return Task.WhenAll(tasks);
        }

        /// <summary>
        /// Retrieves a <see cref="DatabaseEntry"/> from the cache by its unique <see cref="Guid"/> identifier.
        /// </summary>
        /// <param name="guid">The unique identifier of the entry to retrieve.</param>
        /// <returns>The <see cref="DatabaseEntry"/> associated with the specified <see cref="Guid"/>, or <c>null</c> if not found.</returns>
        public DatabaseEntry Get(Guid guid)
        {
            _entries.TryGetValue(guid, out var entry);
            return entry;
        }

        /// <summary>
        /// Retrieves a <see cref="DatabaseEntry"/> of type <typeparamref name="T"/> from the cache by its unique <see cref="Guid"/> identifier.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> to retrieve.</typeparam>
        /// <param name="guid">The unique identifier of the entry to retrieve.</param>
        /// <returns>The entry of type <typeparamref name="T"/> associated with the specified <see cref="Guid"/>, or <c>null</c> if not found or not of type <typeparamref name="T"/>.</returns>
        public T? Get<T>(Guid guid) where T : DatabaseEntry
        {
            return Get(guid) as T;
        }

        /// <summary>
        /// Retrieves the global <see cref="DatabaseEntry"/> instance for the specified type, if it exists.
        /// </summary>
        /// <param name="type">The type of the global entry to retrieve.</param>
        /// <returns>The global <see cref="DatabaseEntry"/> instance associated with the specified type, or <c>null</c> if not found.</returns>
        public DatabaseEntry? GetGlobal(Type type)
        {
            _globals.TryGetValue(type, out var entry);
            return entry;
        }

        /// <summary>
        /// Retrieves the global <see cref="DatabaseEntry"/> instance of type <typeparamref name="T"/>, if it exists.
        /// </summary>
        /// <typeparam name="T">The type of the global entry to retrieve.</typeparam>
        /// <returns>The global entry of type <typeparamref name="T"/>, or <c>null</c> if not found.</returns>
        public T? GetGlobal<T>() where T : DatabaseEntry
        {
            return GetGlobal(typeof(T)) as T;
        }

        /// <summary>
        /// Retrieves all <see cref="DatabaseEntry"/> objects of the specified type from the cache.
        /// </summary>
        /// <param name="type">The type of <see cref="DatabaseEntry"/> to retrieve.</param>
        /// <returns>An enumerable collection of <see cref="DatabaseEntry"/> objects of the specified type.</returns>
        public IEnumerable<DatabaseEntry> GetAll(Type type)
        {
            return _types.TryGetValue(type, out var entries) ? entries : Enumerable.Empty<DatabaseEntry>();
        }

        /// <summary>
        /// Retrieves all <see cref="DatabaseEntry"/> objects of type <typeparamref name="T"/> from the cache.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> to retrieve.</typeparam>
        /// <returns>An enumerable collection of <typeparamref name="T"/> objects from the cache.</returns>
        public IEnumerable<T> GetAll<T>() where T : DatabaseEntry
        {
            return _types.TryGetValue(typeof(T), out var entries) ? entries.Cast<T>() : Enumerable.Empty<T>();
        }
        
        /// <summary>
        /// Retrieves the unique identifier (<see cref="Guid"/>) of an entry of type <typeparamref name="T"/> by its name.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> that implements <see cref="INamedEntry"/>.</typeparam>
        /// <param name="name">The name of the entry to look up.</param>
        /// <returns>The <see cref="Guid"/> of the entry if found; otherwise, <c>null</c>.</returns>
        public Guid? GetIdByName<T>(string name) where T : DatabaseEntry, INamedEntry
        {
            var type = typeof(T);
            if (_nameToIdMap.TryGetValue(type, out var nameMap) && nameMap.TryGetValue(name, out var id))
                return id;
            return null;
        }

        /// <summary>
        /// Retrieves an entry of type <typeparamref name="T"/> by its name from the cache.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> that implements <see cref="INamedEntry"/>.</typeparam>
        /// <param name="name">The name of the entry to retrieve.</param>
        /// <returns>The entry of type <typeparamref name="T"/> if found; otherwise, <c>null</c>.</returns>
        public T? GetByName<T>(string name) where T : DatabaseEntry, INamedEntry
        {
            return GetIdByName<T>(name) is { } id ? Get<T>(id) : null;
        }
        
        /// <summary>
        /// Registers an index for a public member (field or property) of the specified <see cref="DatabaseEntry"/> type.
        /// This enables fast lookup of entries by the value of the indexed member.
        /// Call this method once per member to be indexed.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> for which to register the index.</typeparam>
        /// <param name="memberName">The name of the public member to index.</param>
        /// <exception cref="ArgumentException">Thrown if the specified member does not exist on the type.</exception>
        public void RegisterIndex<T>(string memberName) where T : DatabaseEntry
        {
            var type = typeof(T);
            var key = (type, memberName);
            if (_indexes.ContainsKey(key))
                return;  // Already registered; ignore

            _indexes[key] = new ConcurrentDictionary<string, Guid>();

            // Cache a getter func for perf.
            if (type.GetField(memberName) is { } field)
            {
                var getter = (Func<DatabaseEntry, string>)(entry => field.GetValue(entry)?.ToString() ?? string.Empty);
                _typeToGetters.AddOrUpdate(type,
                    _ => new List<(string, Func<DatabaseEntry, string>)> { (memberName, getter) },
                    (_, list) => { list.Add((memberName, getter)); return list; });
            }
            else if (type.GetProperty(memberName) is { } property)
            {
                var getter = (Func<DatabaseEntry, string>)(entry => property.GetValue(entry)?.ToString() ?? string.Empty);
                _typeToGetters.AddOrUpdate(type,
                    _ => new List<(string, Func<DatabaseEntry, string>)> { (memberName, getter) },
                    (_, list) => { list.Add((memberName, getter)); return list; });
            }
            else
            {
                throw new ArgumentException($"No public field or property '{memberName}' on {type.Name}");
            }
        }

        /// <summary>
        /// Retrieves the unique identifier (<see cref="Guid"/>) of an entry of type <typeparamref name="T"/> by the value of an indexed field.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> for which the index is registered.</typeparam>
        /// <param name="fieldName">The name of the indexed field.</param>
        /// <param name="value">The value of the indexed field to look up.</param>
        /// <returns>The <see cref="Guid"/> of the entry if found; otherwise, <c>null</c>.</returns>
        public Guid? GetIdByIndex<T>(string fieldName, string value) where T : DatabaseEntry
        {
            var key = (typeof(T), fieldName);
            if (_indexes.TryGetValue(key, out var indexMap) && indexMap.TryGetValue(value, out var id))
                return id;
            return null;
        }

        /// <summary>
        /// Retrieves an entry of type <typeparamref name="T"/> by the value of an indexed field from the cache.
        /// </summary>
        /// <typeparam name="T">The type of <see cref="DatabaseEntry"/> for which the index is registered.</typeparam>
        /// <param name="fieldName">The name of the indexed field.</param>
        /// <param name="value">The value of the indexed field to look up.</param>
        /// <returns>The entry of type <typeparamref name="T"/> if found; otherwise, <c>null</c>.</returns>
        public T? GetByIndex<T>(string fieldName, string value) where T : DatabaseEntry
        {
            return GetIdByIndex<T>(fieldName, value) is { } id ? Get<T>(id) : null;
        }
        
        /// <summary>
        /// Removes the specified <see cref="DatabaseEntry"/> from the cache, including all associated indexes, type stores, 
        /// global references, parent type sets, and name-to-ID mappings.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to remove from the cache.</param>
        /// <param name="source">
        /// The <see cref="CacheBackingStore"/> to exclude from deletion. Used internally to avoid redundant deletions when the entry originates from a specific store.
        /// </param>
        public void Remove(DatabaseEntry entry, CacheBackingStore? source = null)
        {
            var type = entry.GetType();
            if (!_entries.TryRemove(entry.ID, out _)) return;

            // Clean up type sets
            _types[type].TryRemove(entry);
            foreach (var parentType in type.GetParentTypes())
            {
                if (_types.TryGetValue(parentType, out var typeSet))
                    typeSet.TryRemove(entry);
            }

            // Clean up global if applicable
            if (_globals.TryGetValue(type, out var global) && global != null && global.Equals(entry)) _globals[type] = null!;

            // Propagate delete to backing stores (except source)
            if (_typeStores.TryGetValue(type, out var typeStore))
                typeStore.Delete(entry);
            else
            {
                foreach (var store in _backingStores)
                {
                    if (store != source)
                        store.Delete(entry);
                }
            }

            // Clean up name-to-ID map if INamedEntry
            if (entry is INamedEntry namedEntry && _nameToIdMap.TryGetValue(type, out var nameMap))
            {
                nameMap.TryRemove(namedEntry.EntryName, out _);
            }

            // Clean up generic indexes if registered for this type
            if (_typeToGetters.TryGetValue(type, out var getters))
            {
                foreach (var (fieldName, getter) in getters)
                {
                    var value = getter(entry);
                    if (!string.IsNullOrEmpty(value))
                    {
                        var indexKey = (type, fieldName);
                        _indexes[indexKey].TryRemove(value, out _);
                    }
                }
            }
        }
        
        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (var store in _backingStores)
                if (store is IDisposable d) d.Dispose();
            foreach (var store in _storeTypes.Keys)
                if (store is IDisposable d) d.Dispose();
        }

        private void TrackStoreOperation(Func<Task> operationFactory)
        {
            var operationId = Interlocked.Increment(ref _storeOperationId);
            var operationTask = Task.Run(operationFactory);
            _pendingStoreOperations[operationId] = operationTask;

            _ = operationTask.ContinueWith(
                (_, state) => _pendingStoreOperations.TryRemove((long)state!, out _),
                operationId,
                TaskScheduler.Default);
        }

        private async Task WaitForPendingStoreOperationsAsync()
        {
            while (true)
            {
                var pendingOperations = _pendingStoreOperations.Values.ToArray();
                if (pendingOperations.Length == 0)
                {
                    return;
                }

                await Task.WhenAll(pendingOperations);
            }
        }
    }

    /// <summary>
    /// Represents a backing store that supports observing real-time changes.
    /// </summary>
    public interface IRealtimeBackingStore
    {
        /// <summary>
        /// Begins observing real-time changes in the backing store and triggers appropriate events when changes occur.
        /// </summary>
        public void ObserveChanges();
    }

    /// <summary>
    /// Represents an abstract backing store for caching <see cref="DatabaseEntry"/> objects.
    /// Provides mechanisms for pulling, pushing, deleting, and synchronizing entries, as well as event notifications for entry changes.
    /// </summary>
    public abstract class CacheBackingStore : IDisposable
    {
        private ILogger _logger = new NullLogger();
        
        /// <summary>
        /// Gets or sets the <see cref="ILogger"/> instance used for logging within the cache.
        /// If set to <c>null</c>, a <see cref="NullLogger"/> will be used as a fallback.
        /// </summary>
        public ILogger Logger
        {
            get => _logger;
            set => _logger = value ?? new NullLogger();
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="CacheBackingStore"/> class.
        /// Sets up subjects for entry added, deleted, and updated events.
        /// </summary>
        protected CacheBackingStore()
        {
            EntryAdded = new Subject<DatabaseEntry>();
            EntryDeleted = new Subject<DatabaseEntry>();
            EntryUpdated = new Subject<DatabaseEntry>();
        }

        /// <summary>
        /// Pulls all entries from the backing store into the cache.
        /// Implementations should load all persisted <see cref="DatabaseEntry"/> objects and notify via <see cref="EntryAdded"/>.
        /// </summary>
        public abstract void PullAll();
        /// <summary>
        /// Persists the specified <see cref="DatabaseEntry"/> to the backing store.
        /// Implementations should save or update the entry in the underlying storage.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to persist.</param>
        public abstract void Push(DatabaseEntry entry);
        /// <summary>
        /// Deletes the specified <see cref="DatabaseEntry"/> from the backing store.
        /// Implementations should remove the entry from the underlying storage.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to delete.</param>
        public abstract void Delete(DatabaseEntry entry);
        /// <summary>
        /// Persists all <see cref="DatabaseEntry"/> objects to the backing store.
        /// Implementations should save or update all entries in the underlying storage.
        /// </summary>
        /// <param name="soft">
        /// If <c>true</c>, performs a soft push (implementation-specific); otherwise, performs a full push.
        /// </param>
        public abstract void PushAll(bool soft = false);
        
        /// <summary>
        /// Gets the subject that notifies when a <see cref="DatabaseEntry"/> is added to the backing store.
        /// </summary>
        public Subject<DatabaseEntry> EntryAdded { get; }
        /// <summary>
        /// Gets the subject that notifies when a <see cref="DatabaseEntry"/> is deleted from the backing store.
        /// </summary>
        public Subject<DatabaseEntry> EntryDeleted { get; }
        /// <summary>
        /// Gets the subject that notifies when a <see cref="DatabaseEntry"/> is updated in the backing store.
        /// </summary>
        public Subject<DatabaseEntry> EntryUpdated { get; }

        /// <summary>
        /// Stores all <see cref="DatabaseEntry"/> objects managed by this backing store, keyed by their unique <see cref="Guid"/> identifier.
        /// </summary>
        protected ConcurrentDictionary<Guid, DatabaseEntry> Entries = new();

        /// <summary>
        /// Subscribes this backing store to the entry events of another <see cref="CacheBackingStore"/>, 
        /// so that added, deleted, and updated entries in the target store are automatically pushed or deleted in this store.
        /// </summary>
        /// <param name="targetStore">The target <see cref="CacheBackingStore"/> to subscribe to.</param>
        public void SubscribeTo(CacheBackingStore targetStore)
        {
            targetStore.EntryAdded.Subscribe(Push);
            targetStore.EntryDeleted.Subscribe(Delete);
            targetStore.EntryUpdated.Subscribe(Push);
        }

        /// <inheritdoc/>
        public virtual void Dispose()
        {
            EntryAdded?.Dispose();
            EntryDeleted?.Dispose();
            EntryUpdated?.Dispose();
        }
    }
    
    /// <summary>
    /// An abstract cache backing store that persists <see cref="DatabaseEntry"/> objects as individual files,
    /// organizing them by entry type in subdirectories under a root directory. Supports real-time change observation
    /// via <see cref="FileSystemWatcher"/> and provides serialization/deserialization hooks for custom formats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry type is mapped to its own directory, and entries are stored as files with a configurable extension.
    /// The class maintains a mapping between file paths and entry IDs for efficient lookup and change tracking.
    /// </para>
    /// <para>
    /// Real-time changes (creation, modification, deletion) to files are observed and propagated to upstream subscribers
    /// using Rx subjects and debounced event handling.
    /// </para>
    /// <para>
    /// Subclasses must implement <see cref="Serialize(DatabaseEntry)"/>, <see cref="Deserialize(byte[])"/>, and <see cref="Extension"/>
    /// to define the file format.
    /// </para>
    /// </remarks>
    public abstract class MultiFileBackingStore : CacheBackingStore, IRealtimeBackingStore, IDisposable
    {
        /// <summary>
        /// Gets the root directory where all entry files are stored.
        /// </summary>
        public DirectoryInfo RootDirectory { get; }
        /// <summary>
        /// Maps each <see cref="DatabaseEntry"/> type to its corresponding subdirectory.
        /// </summary>
        protected readonly Dictionary<Type, DirectoryInfo> EntryTypeDirectories = new();
        /// <summary>
        /// Maps file paths to their corresponding <see cref="DatabaseEntry"/> IDs for quick lookup during change detection.
        /// </summary>
        protected readonly ConcurrentDictionary<string, Guid> _filePathToIdMap = new();
        private FileSystemWatcher? watcher;
        private Subject<string>? _changedSubject;
        private IDisposable? _changedSubscription;

        /// <summary>
        /// Serializes a <see cref="DatabaseEntry"/> into a byte array for file storage.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to serialize.</param>
        /// <returns>A byte array representing the serialized entry.</returns>
        public abstract byte[] Serialize(DatabaseEntry entry);
        /// <summary>
        /// Deserializes a byte array into a <see cref="DatabaseEntry"/> object.
        /// </summary>
        /// <param name="data">The byte array containing the serialized entry.</param>
        /// <returns>The deserialized <see cref="DatabaseEntry"/> object.</returns>
        public abstract DatabaseEntry Deserialize(byte[] data);
        /// <summary>
        /// Gets the file extension used for storing entries (e.g., ".json", ".msgpack").
        /// </summary>
        public abstract string Extension { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiFileBackingStore"/> class.
        /// Creates the root directory if it does not exist and sets up subdirectories for each <see cref="DatabaseEntry"/> child type.
        /// </summary>
        /// <param name="path">The path to the root directory for storing entry files.</param>
        public MultiFileBackingStore(string path)
        {
            RootDirectory = new DirectoryInfo(path);
            foreach (var type in typeof(DatabaseEntry).GetAllChildClasses())
            {
                EntryTypeDirectories[type] = GetDirectoryForType(type);
            }
        }

        private DirectoryInfo GetDirectoryForType(Type type)
        {
            var stack = new Stack<string>();
            var t = type;
            while (t != null && t != typeof(DatabaseEntry))
            {
                stack.Push(t.Name);
                t = t.BaseType;
            }
            stack.Push(RootDirectory.FullName);
            // ToArray() copies in LIFO order (top/first: root, then parents, deepest child last)
            // Ensures Path.Combine yields: Root\Parent\...\Child
            return Directory.CreateDirectory(Path.Combine(stack.ToArray()));
        }
        
        /// <summary>
        /// Loads all <see cref="DatabaseEntry"/> objects from individual files in the root directory and its subdirectories,
        /// organized by entry type. Each file is deserialized using <see cref="Deserialize(byte[])"/>,
        /// added to the local <see cref="CacheBackingStore.Entries"/> dictionary, and notifies upstream via <see cref="CacheBackingStore.EntryAdded"/>.
        /// Skips files that cannot be deserialized, logging errors without failing the entire operation.
        /// Only processes files matching the <see cref="Extension"/>.
        /// </summary>
        public override void PullAll()
        {
            if (!RootDirectory.Exists) return;

            foreach (var directory in EntryTypeDirectories.Values)
            {
                foreach (var file in directory.EnumerateFiles($"*.{Extension}"))
                {
                    try
                    {
                        var entry = Deserialize(File.ReadAllBytes(file.FullName));
                        Entries[entry.ID] = entry;
                        EntryAdded.OnNext(entry);
                        _filePathToIdMap[file.FullName] = entry.ID;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to pull entry from {file.FullName}: {ex.Message}");
                    }
                }
            }
        }

        private string GetFileName(DatabaseEntry entry)
        {
            string name = entry is INamedEntry namedEntry ? namedEntry.EntryName : entry.ID.ToString();
            // Sanitize to prevent invalid chars/path traversal
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return $"{name}.{Extension}";
        }
        
        /// <summary>
        /// Persists the specified <see cref="DatabaseEntry"/> to a file in the appropriate type subdirectory.
        /// The file name is generated using <see cref="GetFileName(DatabaseEntry)"/>, which prefers the entry name if available.
        /// The entry is serialized via <see cref="Serialize(DatabaseEntry)"/> and written to disk.
        /// Updates the local <see cref="CacheBackingStore.Entries"/> dictionary and the <see cref="_filePathToIdMap"/> for change tracking.
        /// Logs errors if the write fails but re-throws to allow caller handling.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to persist to a file.</param>
        public override void Push(DatabaseEntry entry)
        {
            var type = entry.GetType();
            Entries[entry.ID] = entry;
            var directory = EntryTypeDirectories[type];
            var filePath = Path.Combine(directory.FullName, GetFileName(entry));
            if (TryGetExistingPath(entry.ID, out var existingPath) &&
                !string.Equals(existingPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(existingPath);
                _filePathToIdMap.TryRemove(existingPath, out _);
            }

            WriteAllBytesAtomic(filePath, Serialize(entry));
            _filePathToIdMap[filePath] = entry.ID;
        }

        /// <summary>
        /// Removes the specified <see cref="DatabaseEntry"/> from the local <see cref="CacheBackingStore.Entries"/> dictionary
        /// and deletes its corresponding file in the type subdirectory.
        /// Updates the <see cref="_filePathToIdMap"/> and notifies upstream via <see cref="CacheBackingStore.EntryDeleted"/>.
        /// File deletion failures are logged but do not prevent local removal or notification.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to delete from storage.</param>
        public override void Delete(DatabaseEntry entry)
        {
            if (Entries.ContainsKey(entry.ID))
            {
                Entries.TryRemove(entry.ID, out _);
                var filePath = TryGetExistingPath(entry.ID, out var existingPath)
                    ? existingPath
                    : Path.Combine(EntryTypeDirectories[entry.GetType()].FullName, GetFileName(entry));
                _filePathToIdMap.TryRemove(filePath, out _);
                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Persists all entries in <see cref="CacheBackingStore.Entries"/> to their respective files by invoking <see cref="Push(DatabaseEntry)"/> for each.
        /// The <paramref name="soft"/> parameter is ignored in this implementation, as all pushes are full writes.
        /// Errors during individual pushes are logged but do not halt the process.
        /// Suitable for batch synchronization after bulk changes.
        /// </summary>
        /// <param name="soft">If <c>true</c>, performs a soft push (not supported; treated as full push).</param>
        public override void PushAll(bool soft = false)
        {
            foreach (var entry in Entries.Values.ToArray()) Push(entry);
        }
        
        /// <summary>
        /// Starts monitoring the root directory and its subdirectories for file system changes related to entry files.
        /// Initializes a <see cref="FileSystemWatcher"/> with filters for creation, modification, and deletion events on files matching <see cref="Extension"/>.
        /// Uses debouncing for change events to handle bursty writes (100ms delay via <see cref="_changedSubject"/> and R3).
        /// Disposes any existing watcher before creating a new one to prevent duplicates.
        /// Logs a warning if the root directory does not exist and skips observation.
        /// Logs info when observation starts successfully.
        /// Call this method to enable real-time synchronization with external file modifications.
        /// </summary>
        public void ObserveChanges()
        {
            if (watcher != null) // Guard against re-creation
                StopObservingChanges();

            watcher = new FileSystemWatcher(RootDirectory.FullName, $"*.{Extension}")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true
            };

            // Use R3 to throttle events if file writes are bursty
            _changedSubject = new Subject<string>();  // For paths
            _changedSubscription = _changedSubject.Debounce(TimeSpan.FromMilliseconds(100)).Subscribe(HandleFileChange);

            watcher.Changed += (sender, args) =>
            {
                _changedSubject.OnNext(args.FullPath);  // Debounced handling
            };
            watcher.Created += (sender, args) =>
            {
                HandleFileCreation(args.FullPath);
            };
            watcher.Deleted += (sender, args) =>
            {
                HandleFileRemoval(args.FullPath);
            };
        }

        private void HandleFileCreation(string fullPath)
        {
            try
            {
                var entry = Deserialize(File.ReadAllBytes(fullPath));
                Entries[entry.ID] = entry;
                _filePathToIdMap[fullPath] = entry.ID;
                EntryAdded.OnNext(entry);  // Upstream notify
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to handle creation of {fullPath}: {ex.Message}");
            }
        }

        private void HandleFileChange(string fullPath)
        {
            try
            {
                var entry = Deserialize(File.ReadAllBytes(fullPath));
                Entries[entry.ID] = entry;
                _filePathToIdMap[fullPath] = entry.ID;
                EntryUpdated.OnNext(entry);  // Upstream notify
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to handle change in {fullPath}: {ex.Message}");
            }
        }

        private void HandleFileRemoval(string fullPath)
        {
            try
            {
                // External deletion detected—handle without deserializing
                if (_filePathToIdMap.TryGetValue(fullPath, out var id))
                {
                    _filePathToIdMap.TryRemove(fullPath, out _);  // Clean map immediately

                    // Emit delete if we have the entry locally
                    if (Entries.TryGetValue(id, out var entry))
                    {
                        Entries.TryRemove(id, out _);  // Sync local dict
                        EntryDeleted.OnNext(entry);
                    }
                    else
                    {
                        // Fallback: Stub if race/removed already (rare)
                        var type = GetTypeFromPath(fullPath);  // See helper below
                        var stub = CreateStubEntry(id, type);
                        EntryDeleted.OnNext(stub);
                        Logger.LogWarning($"External deletion of {fullPath} (ID: {id}) with no local entry; emitted stub for cleanup.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to handle deletion of {fullPath}: {ex.Message}");
            }
        }

        // Helper: Infer type from path (e.g., based on directory name matching EntryTypeDirectories keys)
        private Type GetTypeFromPath(string filePath)
        {
            var currentDir = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty);
            // Walk up the directory tree to find matching EntryTypeDirectories (handles sub-subdirs)
            while (currentDir is { Exists: true })
            {
                var matchingType = EntryTypeDirectories.FirstOrDefault(kvp => kvp.Value.FullName == currentDir.FullName).Key;
                if (matchingType != null)
                    return matchingType;
                currentDir = currentDir.Parent;  // Walk up (e.g., from "PlayerData/backup" to "PlayerData")
            }
            return typeof(DatabaseEntry);  // Fallback if no match (e.g., manual file)
        }

        // Stub creator (minimal; only for rare fallback—extend if Deletion events need more data)
        private DatabaseEntry CreateStubEntry(Guid id, Type type)
        {
            var entry = type == typeof(DatabaseEntry)
                ? new DeletedDatabaseEntry()
                : (DatabaseEntry)Activator.CreateInstance(type)!;
            typeof(DatabaseEntry).GetField("ID")?.SetValue(entry, id);  // Set ID via reflection
            return entry;
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            StopObservingChanges();
            base.Dispose();
        }

        private bool TryGetExistingPath(Guid id, out string path)
        {
            foreach (var pair in _filePathToIdMap)
            {
                if (pair.Value == id)
                {
                    path = pair.Key;
                    return true;
                }
            }

            path = string.Empty;
            return false;
        }

        private static void WriteAllBytesAtomic(string filePath, byte[] data)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = Path.Combine(directory ?? string.Empty, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempFilePath, data);

            if (File.Exists(filePath))
            {
                File.Replace(tempFilePath, filePath, null);
            }
            else
            {
                File.Move(tempFilePath, filePath);
            }
        }

        private static void TryDeleteFile(string filePath)
        {
            var file = new FileInfo(filePath);
            if (file.Exists)
            {
                file.Delete();
            }
        }

        private void StopObservingChanges()
        {
            _changedSubscription?.Dispose();
            _changedSubscription = null;
            _changedSubject?.Dispose();
            _changedSubject = null;
            watcher?.Dispose();
            watcher = null;
        }

        private sealed class DeletedDatabaseEntry : DatabaseEntry
        {
        }
    }

    /// <summary>
    /// An abstract cache backing store that persists all <see cref="DatabaseEntry"/> objects in a single file.
    /// Supports serialization of the entire collection and provides hooks for custom binary formats.
    /// Suitable for smaller datasets or when atomic updates are preferred over individual file management.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All entries are loaded and saved as a single array in the specified file.
    /// Changes do not trigger a write, must manually call PushAll to persist.
    /// </para>
    /// <para>
    /// Subclasses must implement <see cref="Serialize(DatabaseEntry[])"/> and <see cref="Deserialize(byte[])"/>
    /// to define the storage format (e.g., JSON array, MessagePack array).
    /// </para>
    /// </remarks>
    public abstract class SingleFileBackingStore : CacheBackingStore
    {
        /// <summary>
        /// Gets the file where all entries are stored.
        /// </summary>
        public FileInfo FileInfo { get; }

        /// <summary>
        /// Serializes an array of <see cref="DatabaseEntry"/> objects into a byte array for single-file storage.
        /// </summary>
        /// <param name="entries">The array of <see cref="DatabaseEntry"/> objects to serialize.</param>
        /// <returns>A byte array representing the serialized entries.</returns>
        public abstract byte[] Serialize(DatabaseEntry[] entries);
        /// <summary>
        /// Deserializes a byte array into an array of <see cref="DatabaseEntry"/> objects.
        /// </summary>
        /// <param name="data">The byte array containing the serialized entries.</param>
        /// <returns>An array of deserialized <see cref="DatabaseEntry"/> objects.</returns>
        public abstract DatabaseEntry[] Deserialize(byte[] data);

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleFileBackingStore"/> class.
        /// </summary>
        /// <param name="filePath">The path to the single file for storing all entries.</param>
        public SingleFileBackingStore(string filePath)
        {
            FileInfo = new FileInfo(filePath);
        }
        
        /// <summary>
        /// Loads all <see cref="DatabaseEntry"/> objects from the single storage file by reading its contents,
        /// deserializing via <see cref="Deserialize(byte[])"/> into an array, and adding each to the local <see cref="CacheBackingStore.Entries"/> dictionary.
        /// Notifies upstream via <see cref="CacheBackingStore.EntryAdded"/> for each entry.
        /// If the file does not exist or deserialization fails, logs an error and returns without adding entries.
        /// Logs the number of loaded entries for debugging.
        /// </summary>
        public override void PullAll()
        {
            if (!FileInfo.Exists) return;

            foreach (var entry in Deserialize(File.ReadAllBytes(FileInfo.FullName)))
            {
                Entries[entry.ID] = entry;
                EntryAdded.OnNext(entry);
            }
        }

        /// <summary>
        /// Adds or updates the specified <see cref="DatabaseEntry"/> in the local <see cref="CacheBackingStore.Entries"/> dictionary.
        /// Does not immediately persist to the single file; call <see cref="PushAll(bool)"/> to save changes.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to persist.</param>
        public override void Push(DatabaseEntry entry)
        {
            var type = entry.GetType();
            Entries[entry.ID] = entry;
        }

        /// <summary>
        /// Removes the specified <see cref="DatabaseEntry"/> from the local <see cref="CacheBackingStore.Entries"/> dictionary.
        /// Does not immediately persist to the single file; call <see cref="PushAll(bool)"/> to save changes.
        /// </summary>
        /// <param name="entry">The <see cref="DatabaseEntry"/> to delete.</param>
        public override void Delete(DatabaseEntry entry)
        {
            if (Entries.TryGetValue(entry.ID, out _))
            {
                Entries.TryRemove(entry.ID, out _);
            }
        }

        /// <summary>
        /// Serializes all entries in <see cref="CacheBackingStore.Entries"/> to a byte array using <see cref="Serialize(DatabaseEntry[])"/>
        /// and writes it to the single storage file, overwriting any existing content.
        /// The <paramref name="soft"/> parameter is ignored in this implementation, as saves are always full rewrites.
        /// Logs the number of saved entries and errors if the write fails, re-throwing for caller handling.
        /// </summary>
        /// <param name="soft">If <c>true</c>, performs a soft push (not supported; treated as full push).</param>
        public override void PushAll(bool soft = false)
        {
            var entriesArray = Entries.Values.ToArray();
            var directory = FileInfo.DirectoryName;
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = Path.Combine(directory ?? string.Empty, $"{Path.GetFileName(FileInfo.FullName)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(tempFilePath, Serialize(entriesArray));
            if (FileInfo.Exists)
            {
                File.Replace(tempFilePath, FileInfo.FullName, null);
            }
            else
            {
                File.Move(tempFilePath, FileInfo.FullName);
            }
        }
    }
}
