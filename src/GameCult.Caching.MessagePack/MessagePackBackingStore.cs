using MessagePack;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// Persists all cache entries to a single MessagePack file.
/// </summary>
public class SingleFileMessagePackBackingStore : SingleFileBackingStore
{
	/// <summary>
	/// Initializes a single-file MessagePack backing store.
	/// </summary>
	/// <param name="filePath">The path to the backing file.</param>
	public SingleFileMessagePackBackingStore(string filePath) : base(filePath) { }

	/// <inheritdoc />
	public override byte[] Serialize(DatabaseEntry[] entries)
	{
		return DatabaseEntrySerialization.Serialize(entries);
	}

	/// <inheritdoc />
	public override DatabaseEntry[] Deserialize(byte[] data)
	{
		return DatabaseEntrySerialization.Deserialize<DatabaseEntry[]>(data);
	}
}

/// <summary>
/// Persists each cache entry to its own MessagePack file.
/// </summary>
public class MultiFileMessagePackBackingStore : MultiFileBackingStore
{
	/// <summary>
	/// Initializes a multi-file MessagePack backing store rooted at the supplied directory.
	/// </summary>
	/// <param name="path">The root directory for stored entry files.</param>
	public MultiFileMessagePackBackingStore(string path) : base(path) { }

	/// <inheritdoc />
	public override byte[] Serialize(DatabaseEntry entry)
	{
		return DatabaseEntrySerialization.Serialize(entry);
	}

	/// <inheritdoc />
	public override DatabaseEntry Deserialize(byte[] data)
	{
		return DatabaseEntrySerialization.Deserialize<DatabaseEntry>(data);
	}

	/// <inheritdoc />
	public override string Extension => "msgpack";
}
