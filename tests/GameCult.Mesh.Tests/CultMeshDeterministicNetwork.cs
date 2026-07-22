using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace GameCult.Mesh.Tests;

internal enum CultMeshFaultKind
{
    Drop,
    Duplicate,
    Delay,
    Reorder,
    Partition,
    Corrupt,
    RotateEndpoint,
    Restart
}

internal sealed record CultMeshFault(
    long SendOrdinal,
    CultMeshFaultKind Kind,
    TimeSpan Delay = default,
    string Endpoint = "");

internal sealed record CultMeshTestPacket(
    long SendOrdinal,
    string Endpoint,
    byte[] Payload,
    DateTimeOffset DeliverAtUtc,
    long DeliveryOrdinal);

/// <summary>
/// Transport-neutral hostile-network harness. Delivery is driven only by the
/// injected clock and schedule, so a failure timeline is exactly replayable.
/// </summary>
internal sealed class CultMeshDeterministicNetwork
{
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<long, CultMeshFault[]> _faults;
    private readonly List<CultMeshTestPacket> _pending = new();
    private long _sendOrdinal;
    private long _deliveryOrdinal;
    private bool _partitioned;
    private string _endpoint;

    public CultMeshDeterministicNetwork(
        Func<DateTimeOffset> utcNow,
        string endpoint,
        IEnumerable<CultMeshFault>? faults = null)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _faults = (faults ?? Array.Empty<CultMeshFault>())
            .GroupBy(fault => fault.SendOrdinal)
            .ToDictionary(group => group.Key, group => group.ToArray());
    }

    public int RestartCount { get; private set; }

    public string Endpoint => _endpoint;

    public void Send(byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        var ordinal = ++_sendOrdinal;
        var copies = 1;
        var delay = TimeSpan.Zero;
        var reorder = false;
        var dropped = false;
        var bytes = payload.ToArray();

        foreach (var fault in _faults.GetValueOrDefault(ordinal, Array.Empty<CultMeshFault>()))
        {
            switch (fault.Kind)
            {
                case CultMeshFaultKind.Drop:
                    dropped = true;
                    break;
                case CultMeshFaultKind.Duplicate:
                    copies++;
                    break;
                case CultMeshFaultKind.Delay:
                    delay += fault.Delay;
                    break;
                case CultMeshFaultKind.Reorder:
                    reorder = true;
                    break;
                case CultMeshFaultKind.Partition:
                    _partitioned = !_partitioned;
                    break;
                case CultMeshFaultKind.Corrupt:
                    if (bytes.Length > 0) bytes[0] ^= 0xff;
                    break;
                case CultMeshFaultKind.RotateEndpoint:
                    _endpoint = fault.Endpoint;
                    break;
                case CultMeshFaultKind.Restart:
                    _pending.Clear();
                    _partitioned = false;
                    RestartCount++;
                    break;
            }
        }

        if (dropped || _partitioned) return;
        for (var copy = 0; copy < copies; copy++)
        {
            var deliveryOrdinal = ++_deliveryOrdinal;
            _pending.Add(new CultMeshTestPacket(
                ordinal,
                _endpoint,
                bytes.ToArray(),
                _utcNow() + delay,
                reorder ? -deliveryOrdinal : deliveryOrdinal));
        }
    }

    public IReadOnlyList<CultMeshTestPacket> ReceiveAvailable()
    {
        var now = _utcNow();
        var available = _pending
            .Where(packet => packet.DeliverAtUtc <= now)
            .OrderBy(packet => packet.DeliverAtUtc)
            .ThenBy(packet => packet.DeliveryOrdinal)
            .ToArray();
        foreach (var packet in available) _pending.Remove(packet);
        return available;
    }
}
