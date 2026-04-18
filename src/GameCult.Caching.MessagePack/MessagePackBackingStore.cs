using MessagePack;

namespace GameCult.Caching.MessagePack;

public class SingleFileMessagePackBackingStore : SingleFileBackingStore
{
	public SingleFileMessagePackBackingStore(string filePath) : base(filePath) { }

	public override byte[] Serialize(DatabaseEntry[] entries)
	{
		return MessagePackSerializer.Serialize(entries);
	}

	public override DatabaseEntry[] Deserialize(byte[] data)
	{
		return MessagePackSerializer.Deserialize<DatabaseEntry[]>(data);
	}
}

public class MultiFileMessagePackBackingStore : MultiFileBackingStore
{
	public MultiFileMessagePackBackingStore(string path) : base(path) { }

	public override byte[] Serialize(DatabaseEntry entry)
	{
		return MessagePackSerializer.Serialize(entry);
	}

	public override DatabaseEntry Deserialize(byte[] data)
	{
		return MessagePackSerializer.Deserialize<DatabaseEntry>(data);
	}

	public override string Extension => "msgpack";
}