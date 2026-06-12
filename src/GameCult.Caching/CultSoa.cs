using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

[assembly: GameCult.Caching.CultGeneratedDocumentMetadataProvider(typeof(GameCult.Caching.CultSoaGeneratedDocumentMetadataProvider))]

namespace GameCult.Caching
{
    /// <summary>
    /// CultCache document that stores a CPU-local structure-of-arrays chunk.
    /// </summary>
    [CultDocument("gamecult.soa_chunk", "gamecult.soa_chunk.v1")]
    public sealed class CultSoaChunkDocument
    {
        /// <summary>
        /// Gets or sets the stable chunk identifier.
        /// </summary>
        [CultName]
        public string ChunkId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the layout/archetype identifier shared by chunks with the same columns.
        /// </summary>
        [CultIndex("archetype")]
        public string ArchetypeId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of live rows in this chunk.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Gets or sets the live row entity ids. The array length is the chunk capacity.
        /// </summary>
        public ulong[] EntityIds { get; set; } = Array.Empty<ulong>();

        /// <summary>
        /// Gets or sets the contiguous component columns owned by this chunk.
        /// </summary>
        public CultSoaColumnDocument[] Columns { get; set; } = Array.Empty<CultSoaColumnDocument>();
    }

    /// <summary>
    /// One contiguous column inside a structure-of-arrays chunk.
    /// </summary>
    public sealed class CultSoaColumnDocument
    {
        /// <summary>
        /// Gets or sets the stable column name, usually a component field path.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the CLR element type name stored in the column.
        /// </summary>
        public string ElementType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the size of one element in bytes.
        /// </summary>
        public int ElementByteLength { get; set; }

        /// <summary>
        /// Gets or sets the contiguous column payload.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Runtime view over a CultCache SoA chunk document.
    /// </summary>
    public sealed class CultSoaChunk
    {
        private readonly Dictionary<string, CultSoaColumnDocument> _columns;

        private CultSoaChunk(CultSoaChunkDocument document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            ValidateDocument(document);
            _columns = document.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        }

        /// <summary>
        /// Gets the underlying CultCache document.
        /// </summary>
        public CultSoaChunkDocument Document { get; }

        /// <summary>
        /// Gets the stable chunk identifier.
        /// </summary>
        public string ChunkId => Document.ChunkId;

        /// <summary>
        /// Gets the layout/archetype identifier.
        /// </summary>
        public string ArchetypeId => Document.ArchetypeId;

        /// <summary>
        /// Gets the number of live rows.
        /// </summary>
        public int Count => Document.Count;

        /// <summary>
        /// Gets the maximum row count this chunk can hold without reallocating.
        /// </summary>
        public int Capacity => Document.EntityIds.Length;

        /// <summary>
        /// Gets all entity ids, including unused capacity.
        /// </summary>
        public Span<ulong> EntityIds => Document.EntityIds.AsSpan();

        /// <summary>
        /// Gets live entity ids.
        /// </summary>
        public Span<ulong> ActiveEntityIds => Document.EntityIds.AsSpan(0, Document.Count);

        /// <summary>
        /// Creates an empty SoA chunk with the supplied capacity.
        /// </summary>
        public static CultSoaChunk Create(string chunkId, string archetypeId, int capacity)
        {
            if (string.IsNullOrWhiteSpace(chunkId)) throw new ArgumentException("Value must be non-empty.", nameof(chunkId));
            if (string.IsNullOrWhiteSpace(archetypeId)) throw new ArgumentException("Value must be non-empty.", nameof(archetypeId));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            return new CultSoaChunk(new CultSoaChunkDocument
            {
                ChunkId = chunkId,
                ArchetypeId = archetypeId,
                Count = 0,
                EntityIds = new ulong[capacity],
                Columns = Array.Empty<CultSoaColumnDocument>()
            });
        }

        /// <summary>
        /// Wraps a persisted SoA chunk document.
        /// </summary>
        public static CultSoaChunk Wrap(CultSoaChunkDocument document)
        {
            return new CultSoaChunk(document);
        }

        /// <summary>
        /// Sets the live row count after validating all columns have enough capacity.
        /// </summary>
        public void SetCount(int count)
        {
            if (count < 0 || count > Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            Document.Count = count;
        }

        /// <summary>
        /// Appends one entity row and returns its row index.
        /// </summary>
        public int AddEntity(ulong entityId)
        {
            if (Document.Count >= Capacity)
            {
                throw new InvalidOperationException($"SoA chunk '{ChunkId}' is full.");
            }

            var row = Document.Count;
            Document.EntityIds[row] = entityId;
            Document.Count++;
            return row;
        }

        /// <summary>
        /// Adds a contiguous typed column.
        /// </summary>
        public CultSoaColumn<T> AddColumn<T>(string name) where T : unmanaged
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value must be non-empty.", nameof(name));
            if (_columns.ContainsKey(name))
            {
                throw new InvalidOperationException($"SoA chunk '{ChunkId}' already has a '{name}' column.");
            }

            var column = new CultSoaColumnDocument
            {
                Name = name,
                ElementType = CultSoaTypeName.For<T>(),
                ElementByteLength = Marshal.SizeOf<T>(),
                Data = new byte[checked(Capacity * Marshal.SizeOf<T>())]
            };

            var columns = Document.Columns.ToList();
            columns.Add(column);
            Document.Columns = columns.ToArray();
            _columns[name] = column;
            return new CultSoaColumn<T>(this, column);
        }

        /// <summary>
        /// Gets an existing typed column.
        /// </summary>
        public CultSoaColumn<T> Column<T>(string name) where T : unmanaged
        {
            if (!_columns.TryGetValue(name, out var column))
            {
                throw new KeyNotFoundException($"SoA chunk '{ChunkId}' has no '{name}' column.");
            }

            var expectedType = CultSoaTypeName.For<T>();
            var expectedByteLength = Marshal.SizeOf<T>();
            if (!string.Equals(column.ElementType, expectedType, StringComparison.Ordinal) ||
                column.ElementByteLength != expectedByteLength)
            {
                throw new InvalidOperationException(
                    $"SoA column '{name}' stores {column.ElementType}/{column.ElementByteLength} bytes, not {expectedType}/{expectedByteLength}.");
            }

            var expectedLength = checked(Capacity * expectedByteLength);
            if (column.Data.Length != expectedLength)
            {
                throw new InvalidOperationException(
                    $"SoA column '{name}' has {column.Data.Length} bytes, expected {expectedLength} for chunk capacity {Capacity}.");
            }

            return new CultSoaColumn<T>(this, column);
        }

        private static void ValidateDocument(CultSoaChunkDocument document)
        {
            if (string.IsNullOrWhiteSpace(document.ChunkId))
            {
                throw new ArgumentException("SoA chunk documents require a chunk id.", nameof(document));
            }

            if (string.IsNullOrWhiteSpace(document.ArchetypeId))
            {
                throw new ArgumentException("SoA chunk documents require an archetype id.", nameof(document));
            }

            document.EntityIds ??= Array.Empty<ulong>();
            document.Columns ??= Array.Empty<CultSoaColumnDocument>();
            if (document.Count < 0 || document.Count > document.EntityIds.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(document), "SoA chunk count must fit inside entity id capacity.");
            }

            foreach (var column in document.Columns)
            {
                if (column == null) throw new ArgumentException("SoA chunk columns cannot contain null entries.", nameof(document));
                if (string.IsNullOrWhiteSpace(column.Name)) throw new ArgumentException("SoA columns require names.", nameof(document));
                if (string.IsNullOrWhiteSpace(column.ElementType)) throw new ArgumentException($"SoA column '{column.Name}' requires an element type.", nameof(document));
                if (column.ElementByteLength <= 0) throw new ArgumentOutOfRangeException(nameof(document), $"SoA column '{column.Name}' requires a positive element byte length.");
                column.Data ??= Array.Empty<byte>();
                var expectedLength = checked(document.EntityIds.Length * column.ElementByteLength);
                if (column.Data.Length != expectedLength)
                {
                    throw new ArgumentException(
                        $"SoA column '{column.Name}' has {column.Data.Length} bytes, expected {expectedLength}.",
                        nameof(document));
                }
            }
        }
    }

    /// <summary>
    /// Typed contiguous view over one SoA column.
    /// </summary>
    public readonly struct CultSoaColumn<T> where T : unmanaged
    {
        private readonly CultSoaChunk _owner;
        private readonly CultSoaColumnDocument _column;

        internal CultSoaColumn(CultSoaChunk owner, CultSoaColumnDocument column)
        {
            _owner = owner;
            _column = column;
        }

        /// <summary>
        /// Gets the column name.
        /// </summary>
        public string Name => _column.Name;

        /// <summary>
        /// Gets the full column capacity as a typed span.
        /// </summary>
        public Span<T> Span => MemoryMarshal.Cast<byte, T>(_column.Data.AsSpan());

        /// <summary>
        /// Gets the live rows as a typed span.
        /// </summary>
        public Span<T> ActiveSpan => Span.Slice(0, _owner.Count);
    }

    /// <summary>
    /// Convenience extensions for storing SoA chunks in CultCache.
    /// </summary>
    public static class CultSoaCacheExtensions
    {
        /// <summary>
        /// Adds or replaces a SoA chunk document in a CultCache.
        /// </summary>
        public static Task<CultRecordHandle<CultSoaChunkDocument>> UpsertSoaChunkAsync(
            this CultCache cache,
            CultSoaChunk chunk,
            CultRecordHandle<CultSoaChunkDocument>? handle = null)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            return cache.UpsertAsync(chunk.Document, handle);
        }

        /// <summary>
        /// Gets a SoA chunk view by record key.
        /// </summary>
        public static CultSoaChunk? GetSoaChunk(this CultCache cache, CultRecordKey key)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            var document = cache.Get<CultSoaChunkDocument>(key);
            return document == null ? null : CultSoaChunk.Wrap(document);
        }

        /// <summary>
        /// Projects ordinary typed CultCache documents into contiguous unmanaged columns for hot CPU loops.
        /// </summary>
        public static CultSoaDocumentTable<TDocument> ProjectSoa<TDocument>(this CultCache cache) where TDocument : class
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            return CultSoaDocumentTable<TDocument>.From(cache);
        }
    }

    /// <summary>
    /// Structure-of-arrays projection over ordinary CultCache documents.
    /// </summary>
    public sealed class CultSoaDocumentTable<TDocument> where TDocument : class
    {
        private readonly Dictionary<string, object> _columns;

        private CultSoaDocumentTable(
            CultRecordKey[] keys,
            TDocument[] documents,
            Dictionary<string, object> columns)
        {
            Keys = keys;
            Documents = documents;
            _columns = columns;
        }

        /// <summary>
        /// Gets the projected record keys in row order.
        /// </summary>
        public IReadOnlyList<CultRecordKey> Keys { get; }

        /// <summary>
        /// Gets the projected document references in row order.
        /// </summary>
        public IReadOnlyList<TDocument> Documents { get; }

        /// <summary>
        /// Gets the row count.
        /// </summary>
        public int Count => Documents.Count;

        /// <summary>
        /// Builds a contiguous projection from all documents of this type in the cache.
        /// </summary>
        public static CultSoaDocumentTable<TDocument> From(CultCache cache)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            var stored = cache.AllStoredDocuments
                .Where(entry => entry.Document is TDocument)
                .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal)
                .ToArray();
            var keys = stored.Select(entry => entry.Key).ToArray();
            var documents = stored.Select(entry => (TDocument)entry.Document).ToArray();
            var columns = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var member in CultSoaDocumentMember.Discover(typeof(TDocument)))
            {
                columns[member.Name] = member.Project(documents);
            }

            return new CultSoaDocumentTable<TDocument>(keys, documents, columns);
        }

        /// <summary>
        /// Gets a contiguous projected column by field or property name.
        /// </summary>
        public CultSoaDocumentColumn<TValue> Column<TValue>(string name) where TValue : unmanaged
        {
            if (!_columns.TryGetValue(name, out var column))
            {
                throw new KeyNotFoundException($"SoA projection for {typeof(TDocument).Name} has no '{name}' column.");
            }

            if (column is not TValue[] values)
            {
                throw new InvalidOperationException(
                    $"SoA projection column '{name}' stores {column.GetType().GetElementType()?.FullName}, not {typeof(TValue).FullName}.");
            }

            return new CultSoaDocumentColumn<TValue>(name, values);
        }
    }

    /// <summary>
    /// Contiguous projected column from ordinary CultCache documents.
    /// </summary>
    public readonly struct CultSoaDocumentColumn<TValue> where TValue : unmanaged
    {
        internal CultSoaDocumentColumn(string name, TValue[] values)
        {
            Name = name;
            Values = values;
        }

        /// <summary>
        /// Gets the projected field or property name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the backing array.
        /// </summary>
        public TValue[] Values { get; }

        /// <summary>
        /// Gets the column as a contiguous span.
        /// </summary>
        public Span<TValue> Span => Values.AsSpan();
    }

    internal sealed class CultSoaDocumentMember
    {
        private CultSoaDocumentMember(string name, Type valueType, Func<object, object?> getter)
        {
            Name = name;
            ValueType = valueType;
            Getter = getter;
        }

        public string Name { get; }
        public Type ValueType { get; }
        private Func<object, object?> Getter { get; }

        public static IEnumerable<CultSoaDocumentMember> Discover(Type documentType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var field in documentType.GetFields(flags).OrderBy(field => field.MetadataToken))
            {
                if (IsSupportedColumnType(field.FieldType))
                {
                    yield return new CultSoaDocumentMember(field.Name, field.FieldType, field.GetValue);
                }
            }

            foreach (var property in documentType.GetProperties(flags).OrderBy(property => property.MetadataToken))
            {
                if (property.GetMethod != null &&
                    property.GetIndexParameters().Length == 0 &&
                    IsSupportedColumnType(property.PropertyType))
                {
                    yield return new CultSoaDocumentMember(property.Name, property.PropertyType, property.GetValue);
                }
            }
        }

        public object Project(IReadOnlyList<object> documents)
        {
            var array = Array.CreateInstance(ValueType, documents.Count);
            for (var index = 0; index < documents.Count; index++)
            {
                array.SetValue(Getter(documents[index]) ?? Activator.CreateInstance(ValueType), index);
            }

            return array;
        }

        private static bool IsSupportedColumnType(Type type)
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

    internal static class CultSoaTypeName
    {
        public static string For<T>() where T : unmanaged
        {
            var type = typeof(T);
            if (type == typeof(byte)) return "u8";
            if (type == typeof(sbyte)) return "i8";
            if (type == typeof(short)) return "i16";
            if (type == typeof(ushort)) return "u16";
            if (type == typeof(int)) return "i32";
            if (type == typeof(uint)) return "u32";
            if (type == typeof(long)) return "i64";
            if (type == typeof(ulong)) return "u64";
            if (type == typeof(float)) return "f32";
            if (type == typeof(double)) return "f64";
            return type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        }
    }

    /// <summary>
    /// Built-in metadata and payload codec for CultCache SoA documents.
    /// </summary>
    public sealed class CultSoaGeneratedDocumentMetadataProvider : ICultGeneratedDocumentMetadataProvider
    {
        /// <inheritdoc />
        public IEnumerable<CultGeneratedDocumentDefinition> GetDocumentDefinitions()
        {
            yield return new CultGeneratedDocumentDefinition(
                typeof(CultSoaChunkDocument),
                "gamecult.soa_chunk",
                "gamecult.soa_chunk.v1",
                isGlobal: false,
                nameMember: nameof(CultSoaChunkDocument.ChunkId),
                nameAccessor: document => ((CultSoaChunkDocument)document).ChunkId,
                serializePayload: SerializeChunk,
                deserializePayload: DeserializeChunk,
                indexAccessors: new[]
                {
                    new CultGeneratedDocumentIndexAccessor(
                        "archetype",
                        document => ((CultSoaChunkDocument)document).ArchetypeId)
                },
                members: new[]
                {
                    new CultGeneratedDocumentMemberDefinition(nameof(CultSoaChunkDocument.ChunkId), 0, typeof(string).FullName!, false, false, null, true, null),
                    new CultGeneratedDocumentMemberDefinition(nameof(CultSoaChunkDocument.ArchetypeId), 1, typeof(string).FullName!, false, false, null, false, "archetype"),
                    new CultGeneratedDocumentMemberDefinition(nameof(CultSoaChunkDocument.Count), 2, typeof(int).FullName!, false, false, null, false, null),
                    new CultGeneratedDocumentMemberDefinition(nameof(CultSoaChunkDocument.EntityIds), 3, typeof(ulong[]).FullName!, false, false, null, false, null),
                    new CultGeneratedDocumentMemberDefinition(nameof(CultSoaChunkDocument.Columns), 4, typeof(CultSoaColumnDocument[]).FullName!, false, false, null, false, null)
                });
        }

        private static byte[] SerializeChunk(object value)
        {
            var chunk = (CultSoaChunkDocument)value;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(1);
            writer.Write(chunk.ChunkId ?? string.Empty);
            writer.Write(chunk.ArchetypeId ?? string.Empty);
            writer.Write(chunk.Count);

            var entityIds = chunk.EntityIds ?? Array.Empty<ulong>();
            writer.Write(entityIds.Length);
            foreach (var entityId in entityIds)
            {
                writer.Write(entityId);
            }

            var columns = chunk.Columns ?? Array.Empty<CultSoaColumnDocument>();
            writer.Write(columns.Length);
            foreach (var column in columns)
            {
                writer.Write(column.Name ?? string.Empty);
                writer.Write(column.ElementType ?? string.Empty);
                writer.Write(column.ElementByteLength);
                var data = column.Data ?? Array.Empty<byte>();
                writer.Write(data.Length);
                writer.Write(data);
            }

            writer.Flush();
            return stream.ToArray();
        }

        private static object DeserializeChunk(byte[] payload)
        {
            using var stream = new MemoryStream(payload);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var version = reader.ReadInt32();
            if (version != 1)
            {
                throw new InvalidOperationException($"Unsupported SoA chunk payload version '{version}'.");
            }

            var chunk = new CultSoaChunkDocument
            {
                ChunkId = reader.ReadString(),
                ArchetypeId = reader.ReadString(),
                Count = reader.ReadInt32()
            };

            var entityCount = reader.ReadInt32();
            chunk.EntityIds = new ulong[entityCount];
            for (var index = 0; index < entityCount; index++)
            {
                chunk.EntityIds[index] = reader.ReadUInt64();
            }

            var columnCount = reader.ReadInt32();
            chunk.Columns = new CultSoaColumnDocument[columnCount];
            for (var index = 0; index < columnCount; index++)
            {
                var column = new CultSoaColumnDocument
                {
                    Name = reader.ReadString(),
                    ElementType = reader.ReadString(),
                    ElementByteLength = reader.ReadInt32()
                };
                var dataLength = reader.ReadInt32();
                column.Data = reader.ReadBytes(dataLength);
                chunk.Columns[index] = column;
            }

            return chunk;
        }
    }
}
