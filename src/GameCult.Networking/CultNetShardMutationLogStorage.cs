using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Stores accepted shard-log entries in their replica catch-up wire shape.
    /// </summary>
    public interface ICultNetShardMutationLogStore
    {
        /// <summary>
        /// Reads accepted log entries for a shard after the supplied sequence.
        /// </summary>
        IReadOnlyList<CultNetShardLogEntryMessage> Read(string shardId, long afterSequence = 0, int? limit = null);

        /// <summary>
        /// Appends or replaces one accepted log entry for a shard.
        /// </summary>
        void Append(string shardId, CultNetShardLogEntryMessage entry);
    }

    /// <summary>
    /// Persists shard-log entries as one MessagePack file per shard.
    /// </summary>
    public sealed class CultNetFileShardMutationLogStore : ICultNetShardMutationLogStore
    {
        private readonly string _rootPath;
        private readonly object _gate = new();

        /// <summary>
        /// Creates a file-backed shard mutation log store.
        /// </summary>
        public CultNetFileShardMutationLogStore(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Value must be non-empty.", nameof(rootPath));
            _rootPath = rootPath;
            Directory.CreateDirectory(_rootPath);
        }

        /// <inheritdoc />
        public IReadOnlyList<CultNetShardLogEntryMessage> Read(string shardId, long afterSequence = 0, int? limit = null)
        {
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence), "Sequence must be non-negative.");

            lock (_gate)
            {
                IEnumerable<CultNetShardLogEntryMessage> entries = ReadAll(shardId)
                    .Where(entry => entry.Sequence > afterSequence)
                    .OrderBy(entry => entry.Sequence);
                if (limit.HasValue)
                {
                    entries = entries.Take(limit.Value);
                }

                return entries.ToArray();
            }
        }

        /// <inheritdoc />
        public void Append(string shardId, CultNetShardLogEntryMessage entry)
        {
            if (string.IsNullOrWhiteSpace(shardId)) throw new ArgumentException("Value must be non-empty.", nameof(shardId));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(entry), "Log entry sequence must be positive.");

            lock (_gate)
            {
                var entries = ReadAll(shardId)
                    .Where(existing => existing.Sequence != entry.Sequence)
                    .Append(entry)
                    .OrderBy(existing => existing.Sequence)
                    .ToArray();
                var payload = MessagePackSerializer.Serialize(entries, CultNetSchemaMessageSerialization.Options);
                File.WriteAllBytes(GetShardPath(shardId), payload);
            }
        }

        private CultNetShardLogEntryMessage[] ReadAll(string shardId)
        {
            var path = GetShardPath(shardId);
            if (!File.Exists(path))
            {
                return Array.Empty<CultNetShardLogEntryMessage>();
            }

            var payload = File.ReadAllBytes(path);
            return MessagePackSerializer.Deserialize<CultNetShardLogEntryMessage[]>(
                payload,
                CultNetSchemaMessageSerialization.Options) ?? Array.Empty<CultNetShardLogEntryMessage>();
        }

        private string GetShardPath(string shardId)
        {
            return Path.Combine(_rootPath, HashShardId(shardId) + ".mpack");
        }

        private static string HashShardId(string shardId)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(shardId));
            return string.Concat(hash.Select(value => value.ToString("x2")));
        }
    }
}
