using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using R3;

namespace GameCult.Caching
{
    /// <summary>
    /// Describes how a CultCache-managed document changed.
    /// </summary>
    public enum CultCacheDocumentChangeKind
    {
        /// <summary>
        /// A document was added.
        /// </summary>
        Added,

        /// <summary>
        /// A document was updated.
        /// </summary>
        Updated,

        /// <summary>
        /// A document was removed.
        /// </summary>
        Removed
    }

    /// <summary>
    /// One reactive CultCache document change.
    /// </summary>
    public sealed class CultCacheDocumentChange<T> where T : class
    {
        /// <summary>
        /// Creates a document change.
        /// </summary>
        public CultCacheDocumentChange(
            CultCacheDocumentChangeKind kind,
            CultRecordKey key,
            T? document,
            T? previousDocument)
        {
            Kind = kind;
            Key = key;
            Document = document;
            PreviousDocument = previousDocument;
        }

        /// <summary>
        /// Gets the change kind.
        /// </summary>
        public CultCacheDocumentChangeKind Kind { get; }

        /// <summary>
        /// Gets the document record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Gets the current POCO presentation, when present.
        /// </summary>
        public T? Document { get; }

        /// <summary>
        /// Gets the previous POCO presentation, when present.
        /// </summary>
        public T? PreviousDocument { get; }
    }

    /// <summary>
    /// Reactive POCO presentation for one CultCache-managed document.
    /// </summary>
    public sealed class CultManagedDocument<T> where T : class
    {
        private readonly Func<T?> _read;
        private readonly Func<T, Task> _commit;
        private readonly Observable<T> _watch;

        /// <summary>
        /// Creates a managed document presentation.
        /// </summary>
        public CultManagedDocument(
            CultRecordKey key,
            Func<T?> read,
            Func<T, Task> commit,
            Observable<T> watch)
        {
            Key = key;
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            _watch = watch ?? throw new ArgumentNullException(nameof(watch));
        }

        /// <summary>
        /// Gets the document record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Gets the current POCO presentation.
        /// </summary>
        public T? Value => _read();

        /// <summary>
        /// Watches committed values for this document.
        /// </summary>
        public Observable<T> Watch()
        {
            return _watch;
        }

        /// <summary>
        /// Commits the current POCO value through the owner-supplied commit path.
        /// </summary>
        public Task CommitAsync()
        {
            var value = Value ?? throw new InvalidOperationException($"Document '{Key.Value}' is not present.");
            return _commit(value);
        }

        /// <summary>
        /// Replaces the current value through the owner-supplied commit path.
        /// </summary>
        public Task ReplaceAsync(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return _commit(value);
        }
    }

    /// <summary>
    /// CPU-local structure-of-arrays table owned by CultCache for one document type.
    /// </summary>
    public sealed class CultSoaTable<TDocument> where TDocument : class
    {
        private readonly Dictionary<string, object> _columns;

        internal CultSoaTable(
            CultRecordKey[] keys,
            TDocument[] documents,
            Dictionary<string, object> columns)
        {
            Keys = keys;
            Documents = documents;
            _columns = columns;
        }

        /// <summary>
        /// Gets record keys in row order.
        /// </summary>
        public IReadOnlyList<CultRecordKey> Keys { get; }

        /// <summary>
        /// Gets POCO presentations in row order.
        /// </summary>
        public IReadOnlyList<TDocument> Documents { get; }

        /// <summary>
        /// Gets the row count.
        /// </summary>
        public int Count => Keys.Count;

        /// <summary>
        /// Gets a contiguous unmanaged field/property column.
        /// </summary>
        public CultSoaColumn<TValue> Column<TValue>(string name) where TValue : unmanaged
        {
            if (!_columns.TryGetValue(name, out var column))
            {
                throw new KeyNotFoundException($"SoA table for {typeof(TDocument).Name} has no '{name}' column.");
            }

            if (column is not TValue[] values)
            {
                throw new InvalidOperationException(
                    $"SoA table column '{name}' stores {column.GetType().GetElementType()?.FullName}, not {typeof(TValue).FullName}.");
            }

            return new CultSoaColumn<TValue>(name, values);
        }
    }

    /// <summary>
    /// Contiguous typed SoA column.
    /// </summary>
    public readonly struct CultSoaColumn<TValue> where TValue : unmanaged
    {
        internal CultSoaColumn(string name, TValue[] values)
        {
            Name = name;
            Values = values;
        }

        /// <summary>
        /// Gets the column name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the backing array.
        /// </summary>
        public TValue[] Values { get; }

        /// <summary>
        /// Gets the backing array as a span.
        /// </summary>
        public Span<TValue> Span => Values.AsSpan();
    }

    internal sealed class CultCacheSoaStore
    {
        private readonly Dictionary<Type, CultCacheSoaTypeTable> _tables = new();

        public void Upsert(CultStoredDocument stored)
        {
            Table(stored.Descriptor.DocumentType).Upsert(stored.Key, stored.Document);
        }

        public void Remove(CultStoredDocument stored)
        {
            if (_tables.TryGetValue(stored.Descriptor.DocumentType, out var table))
            {
                table.Remove(stored.Key);
            }
        }

        public CultSoaTable<TDocument> Snapshot<TDocument>() where TDocument : class
        {
            return Table(typeof(TDocument)).Snapshot<TDocument>();
        }

        private CultCacheSoaTypeTable Table(Type documentType)
        {
            if (!_tables.TryGetValue(documentType, out var table))
            {
                table = new CultCacheSoaTypeTable(documentType);
                _tables[documentType] = table;
            }

            return table;
        }
    }

    internal sealed class CultCacheSoaTypeTable
    {
        private readonly Type _documentType;
        private readonly List<CultRecordKey> _keys = new();
        private readonly List<object> _documents = new();
        private readonly Dictionary<string, CultCacheSoaMember> _members;
        private readonly Dictionary<string, object> _columns;
        private readonly Dictionary<string, int> _rowsByKey = new(StringComparer.Ordinal);

        public CultCacheSoaTypeTable(Type documentType)
        {
            _documentType = documentType;
            _members = CultCacheSoaMember.Discover(documentType)
                .ToDictionary(member => member.Name, StringComparer.Ordinal);
            _columns = _members.Values.ToDictionary(
                member => member.Name,
                member => Array.CreateInstance(member.ValueType, 0) as object,
                StringComparer.Ordinal);
        }

        public void Upsert(CultRecordKey key, object document)
        {
            if (_rowsByKey.TryGetValue(key.Value, out var row))
            {
                _documents[row] = document;
            }
            else
            {
                row = _keys.Count;
                _rowsByKey[key.Value] = row;
                _keys.Add(key);
                _documents.Add(document);
                EnsureColumnCapacity(_keys.Count);
            }

            WriteRow(row, document);
        }

        public void Remove(CultRecordKey key)
        {
            if (!_rowsByKey.TryGetValue(key.Value, out var row))
            {
                return;
            }

            var lastRow = _keys.Count - 1;
            if (row != lastRow)
            {
                _keys[row] = _keys[lastRow];
                _documents[row] = _documents[lastRow];
                _rowsByKey[_keys[row].Value] = row;
                CopyRow(lastRow, row);
            }

            _rowsByKey.Remove(key.Value);
            _keys.RemoveAt(lastRow);
            _documents.RemoveAt(lastRow);
        }

        public CultSoaTable<TDocument> Snapshot<TDocument>() where TDocument : class
        {
            if (!typeof(TDocument).IsAssignableFrom(_documentType))
            {
                throw new InvalidOperationException($"{_documentType.FullName} is not assignable to {typeof(TDocument).FullName}.");
            }

            var count = _keys.Count;
            var columns = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in _columns)
            {
                var member = _members[pair.Key];
                var source = (Array)pair.Value;
                var target = Array.CreateInstance(member.ValueType, count);
                Array.Copy(source, target, count);
                columns[pair.Key] = target;
            }

            return new CultSoaTable<TDocument>(
                _keys.ToArray(),
                _documents.Cast<TDocument>().ToArray(),
                columns);
        }

        private void EnsureColumnCapacity(int count)
        {
            foreach (var member in _members.Values)
            {
                var column = (Array)_columns[member.Name];
                if (column.Length >= count)
                {
                    continue;
                }

                var nextLength = Math.Max(count, Math.Max(4, column.Length * 2));
                var next = Array.CreateInstance(member.ValueType, nextLength);
                Array.Copy(column, next, column.Length);
                _columns[member.Name] = next;
            }
        }

        private void WriteRow(int row, object document)
        {
            foreach (var member in _members.Values)
            {
                ((Array)_columns[member.Name]).SetValue(member.Get(document), row);
            }
        }

        private void CopyRow(int sourceRow, int targetRow)
        {
            foreach (var member in _members.Values)
            {
                var column = (Array)_columns[member.Name];
                column.SetValue(column.GetValue(sourceRow), targetRow);
            }
        }
    }

    internal sealed class CultCacheSoaMember
    {
        private CultCacheSoaMember(string name, Type valueType, Func<object, object?> getter)
        {
            Name = name;
            ValueType = valueType;
            Get = getter;
        }

        public string Name { get; }
        public Type ValueType { get; }
        public Func<object, object?> Get { get; }

        public static IEnumerable<CultCacheSoaMember> Discover(Type documentType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var field in documentType.GetFields(flags).OrderBy(field => field.MetadataToken))
            {
                if (IsColumnType(field.FieldType))
                {
                    yield return new CultCacheSoaMember(field.Name, field.FieldType, field.GetValue);
                }
            }

            foreach (var property in documentType.GetProperties(flags).OrderBy(property => property.MetadataToken))
            {
                if (property.GetMethod != null &&
                    property.GetIndexParameters().Length == 0 &&
                    IsColumnType(property.PropertyType))
                {
                    yield return new CultCacheSoaMember(property.Name, property.PropertyType, property.GetValue);
                }
            }
        }

        private static bool IsColumnType(Type type)
        {
            return type.IsEnum ||
                   type == typeof(byte) ||
                   type == typeof(sbyte) ||
                   type == typeof(short) ||
                   type == typeof(ushort) ||
                   type == typeof(int) ||
                   type == typeof(uint) ||
                   type == typeof(long) ||
                   type == typeof(ulong) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(bool);
        }
    }
}
