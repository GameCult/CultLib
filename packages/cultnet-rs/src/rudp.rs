use anyhow::Result;
use anyhow::anyhow;

use crate::CultNetTransportChannel;
use crate::CultNetTransportDelivery;
use crate::CultNetTransportDescriptor;
use crate::CultNetTransportOrdering;
use crate::CultNetTransportProfile;
use crate::CultNetTransportProtocol;

const RUDP_MAGIC: [u8; 4] = [0x43, 0x4e, 0x52, 0x30];
const RUDP_VERSION: u8 = 0;
const RUDP_FIXED_HEADER_BYTES: usize = 36;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CultNetRudpPacketType {
    Connect,
    Accept,
    Data,
    Ack,
    Ping,
    Pong,
    Disconnect,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpPacket {
    pub packet_type: CultNetRudpPacketType,
    pub connection_id: u32,
    pub sequence: u32,
    pub ack: u32,
    pub ack_mask: u32,
    pub channel_id: String,
    pub reliable: bool,
    pub ordered: bool,
    pub sequenced: bool,
    pub fragment_id: u16,
    pub fragment_index: u16,
    pub fragment_count: u16,
    pub payload: Vec<u8>,
}

#[derive(Clone, Debug, Default)]
pub struct RudpTransportProfileOptions {
    pub transport_id: Option<String>,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub max_payload_bytes: Option<u32>,
    pub max_fragment_bytes: Option<u32>,
}

pub fn create_rudp_transport_profile(
    runtime_id: impl Into<String>,
    options: RudpTransportProfileOptions,
) -> CultNetTransportProfile {
    CultNetTransportProfile {
        schema_version: "cultnet.transport_profile.v0".to_string(),
        runtime_id: runtime_id.into(),
        transports: vec![CultNetTransportDescriptor {
            transport_id: options.transport_id.unwrap_or_else(|| "rudp".to_string()),
            protocol: CultNetTransportProtocol::Rudp,
            host: options.host,
            port: options.port,
            path: None,
            discovery_group: None,
            wire_contracts: Some(vec!["cultnet.schema.v0".to_string()]),
            channels: vec![
                CultNetTransportChannel {
                    channel_id: "schema".to_string(),
                    delivery: CultNetTransportDelivery::Reliable,
                    ordering: CultNetTransportOrdering::Ordered,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                },
                CultNetTransportChannel {
                    channel_id: "latest".to_string(),
                    delivery: CultNetTransportDelivery::Unreliable,
                    ordering: CultNetTransportOrdering::Sequenced,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                },
                CultNetTransportChannel {
                    channel_id: "realtime".to_string(),
                    delivery: CultNetTransportDelivery::Unreliable,
                    ordering: CultNetTransportOrdering::Unordered,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                },
            ],
        }],
    }
}

pub fn encode_rudp_packet(packet: &CultNetRudpPacket) -> Result<Vec<u8>> {
    let channel_id = packet.channel_id.as_bytes();
    if channel_id.len() > u8::MAX as usize {
        return Err(anyhow!(
            "CultNet RUDP channel id cannot exceed 255 UTF-8 bytes"
        ));
    }

    let header_bytes = RUDP_FIXED_HEADER_BYTES + channel_id.len();
    let mut wire = vec![0_u8; header_bytes + packet.payload.len()];
    wire[..4].copy_from_slice(&RUDP_MAGIC);
    wire[4] = RUDP_VERSION;
    wire[5] = packet_type_to_code(packet.packet_type);
    wire[6] = encode_flags(packet);
    wire[7] = header_bytes as u8;
    wire[8..12].copy_from_slice(&packet.connection_id.to_be_bytes());
    wire[12..16].copy_from_slice(&packet.sequence.to_be_bytes());
    wire[16..20].copy_from_slice(&packet.ack.to_be_bytes());
    wire[20..24].copy_from_slice(&packet.ack_mask.to_be_bytes());
    wire[24..26].copy_from_slice(&packet.fragment_id.to_be_bytes());
    wire[26..28].copy_from_slice(&packet.fragment_index.to_be_bytes());
    wire[28..30].copy_from_slice(&packet.fragment_count.to_be_bytes());
    wire[30..34].copy_from_slice(&(packet.payload.len() as u32).to_be_bytes());
    wire[34] = channel_id.len() as u8;
    wire[35] = 0;
    wire[RUDP_FIXED_HEADER_BYTES..header_bytes].copy_from_slice(channel_id);
    wire[header_bytes..].copy_from_slice(&packet.payload);
    Ok(wire)
}

pub fn decode_rudp_packet(wire: &[u8]) -> Result<CultNetRudpPacket> {
    if wire.len() < RUDP_FIXED_HEADER_BYTES {
        return Err(anyhow!(
            "CultNet RUDP packet is shorter than the fixed header"
        ));
    }
    if wire[..4] != RUDP_MAGIC {
        return Err(anyhow!("CultNet RUDP packet has the wrong magic"));
    }
    if wire[4] != RUDP_VERSION {
        return Err(anyhow!(
            "Unsupported CultNet RUDP packet version {}",
            wire[4]
        ));
    }

    let packet_type = packet_type_from_code(wire[5])?;
    let header_bytes = wire[7] as usize;
    let channel_id_len = wire[34] as usize;
    if header_bytes != RUDP_FIXED_HEADER_BYTES + channel_id_len {
        return Err(anyhow!(
            "CultNet RUDP packet header length does not match the channel id length"
        ));
    }
    let payload_len = u32::from_be_bytes(wire[30..34].try_into()?) as usize;
    if wire.len() != header_bytes + payload_len {
        return Err(anyhow!(
            "CultNet RUDP packet payload length does not match the packet size"
        ));
    }

    let flags = wire[6];
    Ok(CultNetRudpPacket {
        packet_type,
        reliable: (flags & 0b0000_0001) != 0,
        ordered: (flags & 0b0000_0010) != 0,
        sequenced: (flags & 0b0000_0100) != 0,
        connection_id: u32::from_be_bytes(wire[8..12].try_into()?),
        sequence: u32::from_be_bytes(wire[12..16].try_into()?),
        ack: u32::from_be_bytes(wire[16..20].try_into()?),
        ack_mask: u32::from_be_bytes(wire[20..24].try_into()?),
        fragment_id: u16::from_be_bytes(wire[24..26].try_into()?),
        fragment_index: u16::from_be_bytes(wire[26..28].try_into()?),
        fragment_count: u16::from_be_bytes(wire[28..30].try_into()?),
        channel_id: String::from_utf8(wire[RUDP_FIXED_HEADER_BYTES..header_bytes].to_vec())?,
        payload: wire[header_bytes..].to_vec(),
    })
}

fn encode_flags(packet: &CultNetRudpPacket) -> u8 {
    (if packet.reliable { 0b0000_0001 } else { 0 })
        | (if packet.ordered { 0b0000_0010 } else { 0 })
        | (if packet.sequenced { 0b0000_0100 } else { 0 })
        | (if packet.fragment_count > 0 {
            0b0000_1000
        } else {
            0
        })
}

fn packet_type_to_code(packet_type: CultNetRudpPacketType) -> u8 {
    match packet_type {
        CultNetRudpPacketType::Connect => 1,
        CultNetRudpPacketType::Accept => 2,
        CultNetRudpPacketType::Data => 3,
        CultNetRudpPacketType::Ack => 4,
        CultNetRudpPacketType::Ping => 5,
        CultNetRudpPacketType::Pong => 6,
        CultNetRudpPacketType::Disconnect => 7,
    }
}

fn packet_type_from_code(code: u8) -> Result<CultNetRudpPacketType> {
    match code {
        1 => Ok(CultNetRudpPacketType::Connect),
        2 => Ok(CultNetRudpPacketType::Accept),
        3 => Ok(CultNetRudpPacketType::Data),
        4 => Ok(CultNetRudpPacketType::Ack),
        5 => Ok(CultNetRudpPacketType::Ping),
        6 => Ok(CultNetRudpPacketType::Pong),
        7 => Ok(CultNetRudpPacketType::Disconnect),
        _ => Err(anyhow!("Unsupported CultNet RUDP packet type {code}")),
    }
}
