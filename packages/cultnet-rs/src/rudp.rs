use anyhow::Result;
use anyhow::anyhow;
use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::collections::VecDeque;
use std::io::ErrorKind;
use std::net::SocketAddr;
use std::net::UdpSocket;
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use uuid::Uuid;

use crate::CultNetMessage;
use crate::CultNetReconnectController;
use crate::CultNetReconnectDecision;
use crate::CultNetReconnectPolicy;
use crate::CultNetTransportChannel;
use crate::CultNetTransportDelivery;
use crate::CultNetTransportDescriptor;
use crate::CultNetTransportFrame;
use crate::CultNetTransportOrdering;
use crate::CultNetTransportProfile;
use crate::CultNetTransportProtocol;
use crate::CultNetTransportStats;
use crate::CultNetWireContract;
use crate::create_reconnect_policy;
use crate::decode_cultnet_message_from_slice;
use crate::encode_cultnet_message_to_vec;

const RUDP_MAGIC: [u8; 4] = [0x43, 0x4e, 0x52, 0x30];
const RUDP_VERSION: u8 = 0;
const RUDP_FIXED_HEADER_BYTES: usize = 36;
pub const CULTNET_RUDP_DEFAULT_MAX_PAYLOAD_BYTES: usize = 16 * 1024 * 1024;
pub const CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS: usize = 32;
const RUDP_RECEIVED_SEQUENCE_WINDOW: usize = 4_096;

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

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpDeliveredFrame {
    pub channel_id: String,
    pub payload: Vec<u8>,
    pub sequence: u32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpReceiveResult {
    pub delivered: Vec<CultNetRudpDeliveredFrame>,
    pub ready_to_send: Vec<CultNetRudpPacket>,
    pub reply: Option<CultNetRudpPacket>,
    pub pong: bool,
    pub pong_payload: Vec<u8>,
    pub disconnected: bool,
    pub disconnect_reason: Vec<u8>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpSessionOptions {
    pub connection_id: u32,
    pub initial_sequence: u32,
    pub resend_delay_ms: u64,
    pub max_pending_reliable_packets: Option<usize>,
}

impl Default for CultNetRudpSessionOptions {
    fn default() -> Self {
        Self {
            connection_id: 0,
            initial_sequence: 1,
            resend_delay_ms: 250,
            max_pending_reliable_packets: None,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CultNetRudpSendOptions {
    pub reliable: bool,
    pub ordered: bool,
    pub sequenced: bool,
    pub now_ms: u64,
}

/// Identifies every transport packet belonging to one non-expiring reliable
/// send. Receipts are local to the session that issued them and cannot survive
/// a peer reset.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpReliableSendReceipt {
    session_scope: Uuid,
    sequences: Vec<u32>,
}

impl CultNetRudpReliableSendReceipt {
    pub fn packet_sequences(&self) -> &[u32] {
        &self.sequences
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CultNetRudpReliableSendStatus {
    Pending,
    Acknowledged,
    Invalidated,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct PendingReliablePacket {
    packet: CultNetRudpPacket,
    last_sent_at_ms: u64,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct PendingOrderedFrame {
    frame: CultNetRudpDeliveredFrame,
    next_sequence: u32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct FragmentBuffer {
    channel_id: String,
    ordered: bool,
    fragment_count: u16,
    payloads: BTreeMap<u16, Vec<u8>>,
    sequences: BTreeMap<u16, u32>,
}

pub struct CultNetRudpSession {
    session_scope: Uuid,
    connection_id: u32,
    resend_delay_ms: u64,
    max_pending_reliable_packets: Option<usize>,
    max_payload_bytes: Option<usize>,
    max_pending_fragment_sets: usize,
    initial_sequence: u32,
    next_sequence: u32,
    next_sequenced_by_channel: BTreeMap<String, u32>,
    next_fragment_id: u16,
    connected: bool,
    last_received_at_ms: Option<u64>,
    highest_received_sequence: Option<u32>,
    received_sequences: BTreeSet<u32>,
    latest_sequenced_by_channel: BTreeMap<String, u32>,
    pending_reliable: BTreeMap<u32, PendingReliablePacket>,
    queued_reliable: VecDeque<CultNetRudpPacket>,
    ordered_next_sequence_by_channel: BTreeMap<String, u32>,
    ordered_buffers: BTreeMap<String, BTreeMap<u32, PendingOrderedFrame>>,
    fragment_buffers: BTreeMap<(String, u16), FragmentBuffer>,
}

impl CultNetRudpSession {
    pub fn new(options: CultNetRudpSessionOptions) -> Self {
        Self {
            session_scope: Uuid::new_v4(),
            connection_id: options.connection_id,
            resend_delay_ms: options.resend_delay_ms,
            max_pending_reliable_packets: options.max_pending_reliable_packets,
            max_payload_bytes: None,
            max_pending_fragment_sets: 64,
            initial_sequence: options.initial_sequence,
            next_sequence: options.initial_sequence,
            next_sequenced_by_channel: BTreeMap::new(),
            next_fragment_id: 1,
            connected: false,
            last_received_at_ms: None,
            highest_received_sequence: None,
            received_sequences: BTreeSet::new(),
            latest_sequenced_by_channel: BTreeMap::new(),
            pending_reliable: BTreeMap::new(),
            queued_reliable: VecDeque::new(),
            ordered_next_sequence_by_channel: BTreeMap::new(),
            ordered_buffers: BTreeMap::new(),
            fragment_buffers: BTreeMap::new(),
        }
    }

    pub fn connection_id(&self) -> u32 {
        self.connection_id
    }

    pub fn resend_delay_ms(&self) -> u64 {
        self.resend_delay_ms
    }

    pub fn connected(&self) -> bool {
        self.connected
    }

    pub fn assume_connected(&mut self, now_ms: u64) {
        self.connected = true;
        self.last_received_at_ms = Some(now_ms);
    }

    pub fn pending_reliable_sequences(&self) -> Vec<u32> {
        self.pending_reliable.keys().copied().collect()
    }

    pub fn queued_reliable_packet_count(&self) -> usize {
        self.queued_reliable.len()
    }

    pub fn outstanding_reliable_packet_count(&self) -> usize {
        self.pending_reliable.len() + self.queued_reliable.len()
    }

    fn pending_accept_for_resend(&mut self, now_ms: u64) -> Option<CultNetRudpPacket> {
        let pending = self
            .pending_reliable
            .values_mut()
            .find(|pending| pending.packet.packet_type == CultNetRudpPacketType::Accept)?;
        pending.last_sent_at_ms = now_ms;
        Some(pending.packet.clone())
    }

    fn reliable_send_status(
        &self,
        receipt: &CultNetRudpReliableSendReceipt,
    ) -> CultNetRudpReliableSendStatus {
        if receipt.session_scope != self.session_scope {
            return CultNetRudpReliableSendStatus::Invalidated;
        }
        if receipt
            .sequences
            .iter()
            .any(|sequence| self.pending_reliable.contains_key(sequence))
        {
            CultNetRudpReliableSendStatus::Pending
        } else {
            CultNetRudpReliableSendStatus::Acknowledged
        }
    }

    fn non_expiring_reliable_receipt(
        &self,
        packets: &[CultNetRudpPacket],
    ) -> Result<CultNetRudpReliableSendReceipt> {
        if packets.is_empty() || packets.iter().any(|packet| !packet.reliable) {
            return Err(anyhow!(
                "RUDP acknowledgement receipts require a non-empty reliable packet set"
            ));
        }
        Ok(CultNetRudpReliableSendReceipt {
            session_scope: self.session_scope,
            sequences: packets.iter().map(|packet| packet.sequence).collect(),
        })
    }

    pub fn last_received_at_ms(&self) -> Option<u64> {
        self.last_received_at_ms
    }

    pub fn set_max_payload_bytes(&mut self, max_payload_bytes: Option<usize>) {
        self.max_payload_bytes = max_payload_bytes;
    }

    pub fn set_max_pending_fragment_sets(
        &mut self,
        max_pending_fragment_sets: usize,
    ) -> Result<()> {
        if max_pending_fragment_sets == 0 {
            return Err(anyhow!(
                "RUDP max_pending_fragment_sets must be greater than zero"
            ));
        }
        self.max_pending_fragment_sets = max_pending_fragment_sets;
        Ok(())
    }

    pub fn reset_peer_state(&mut self) {
        self.session_scope = Uuid::new_v4();
        self.next_sequence = self.initial_sequence;
        self.next_sequenced_by_channel.clear();
        self.next_fragment_id = 1;
        self.connected = false;
        self.last_received_at_ms = None;
        self.highest_received_sequence = None;
        self.received_sequences.clear();
        self.latest_sequenced_by_channel.clear();
        self.pending_reliable.clear();
        self.queued_reliable.clear();
        self.ordered_next_sequence_by_channel.clear();
        self.ordered_buffers.clear();
        self.fragment_buffers.clear();
    }

    pub fn create_connect(&mut self, now_ms: u64, payload: Vec<u8>) -> Result<CultNetRudpPacket> {
        self.ensure_reliable_capacity(1)?;
        let packet = self.create_packet(
            CultNetRudpPacketType::Connect,
            "control",
            payload,
            true,
            true,
            false,
        );
        self.track_reliable(packet.clone(), now_ms);
        Ok(packet)
    }

    pub fn accept_connect(
        &mut self,
        packet: &CultNetRudpPacket,
        now_ms: u64,
        payload: Vec<u8>,
    ) -> Result<CultNetRudpPacket> {
        self.require_connection(packet)?;
        if packet.packet_type != CultNetRudpPacketType::Connect {
            return Err(anyhow!(
                "Expected RUDP connect packet, got {:?}",
                packet.packet_type
            ));
        }

        self.ensure_reliable_capacity(1)?;
        self.remember_received(packet.sequence);
        self.last_received_at_ms = Some(now_ms);
        self.connected = true;
        let response = self.create_packet(
            CultNetRudpPacketType::Accept,
            "control",
            payload,
            true,
            true,
            false,
        );
        self.track_reliable(response.clone(), now_ms);
        Ok(response)
    }

    pub fn send(
        &mut self,
        channel_id: &str,
        payload: Vec<u8>,
        options: CultNetRudpSendOptions,
    ) -> Result<CultNetRudpPacket> {
        if options.reliable
            && self.pending_reliable.len() >= CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS
        {
            return Err(anyhow!(
                "RUDP reliable send window is full; receive acknowledgements before sending"
            ));
        }
        self.send_many(channel_id, payload, options, None)?
            .into_iter()
            .next()
            .ok_or_else(|| anyhow!("RUDP send produced no packets"))
    }

    pub fn send_many(
        &mut self,
        channel_id: &str,
        payload: Vec<u8>,
        options: CultNetRudpSendOptions,
        max_fragment_bytes: Option<usize>,
    ) -> Result<Vec<CultNetRudpPacket>> {
        if !self.connected {
            return Err(anyhow!(
                "Cannot send RUDP data before the session is connected"
            ));
        }
        if options.ordered && !options.reliable {
            return Err(anyhow!("RUDP ordered delivery requires reliability"));
        }
        self.require_payload_size(payload.len())?;

        if let Some(max_fragment_bytes) = max_fragment_bytes {
            if max_fragment_bytes == 0 {
                return Err(anyhow!("RUDP max_fragment_bytes must be greater than zero"));
            }
            if payload.len() > max_fragment_bytes {
                let fragment_count = payload.len().div_ceil(max_fragment_bytes);
                if fragment_count > u16::MAX as usize {
                    return Err(anyhow!("RUDP payload requires more than 65535 fragments"));
                }
                self.ensure_reliable_capacity(if options.reliable { fragment_count } else { 0 })?;
                let fragment_id = self.allocate_fragment_id();
                let mut packets = Vec::new();
                for index in 0..fragment_count {
                    let start = index * max_fragment_bytes;
                    let end = (start + max_fragment_bytes).min(payload.len());
                    let packet = self.create_packet_with_fragments(
                        CultNetRudpPacketType::Data,
                        channel_id,
                        payload[start..end].to_vec(),
                        options.reliable,
                        options.ordered,
                        options.sequenced,
                        fragment_id,
                        index as u16,
                        fragment_count as u16,
                    );
                    packets.push(packet);
                }
                return Ok(if options.reliable {
                    self.admit_reliable_packets(packets, options.now_ms)
                } else {
                    packets
                });
            }
        }

        self.ensure_reliable_capacity(if options.reliable { 1 } else { 0 })?;
        let packet = self.create_packet(
            CultNetRudpPacketType::Data,
            channel_id,
            payload,
            options.reliable,
            options.ordered,
            options.sequenced,
        );
        if packet.reliable {
            return Ok(self.admit_reliable_packets(vec![packet], options.now_ms));
        }
        Ok(vec![packet])
    }

    pub fn receive(
        &mut self,
        packet: &CultNetRudpPacket,
        now_ms: u64,
    ) -> Result<CultNetRudpReceiveResult> {
        self.require_connection(packet)?;
        self.apply_acknowledgements(packet);
        let ready_to_send = self.promote_queued_reliable(now_ms);
        self.last_received_at_ms = Some(now_ms);
        let expected_sequence_if_uninitialized = self
            .highest_received_sequence
            .map(|sequence| sequence + 1)
            .unwrap_or(packet.sequence);

        if packet.packet_type == CultNetRudpPacketType::Accept {
            self.remember_received(packet.sequence);
            self.connected = true;
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: false,
                pong_payload: Vec::new(),
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        }

        if packet.packet_type == CultNetRudpPacketType::Ping {
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: Some(self.create_packet(
                    CultNetRudpPacketType::Pong,
                    "control",
                    packet.payload.clone(),
                    false,
                    false,
                    false,
                )),
                pong: false,
                pong_payload: Vec::new(),
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        }

        if packet.packet_type == CultNetRudpPacketType::Ack
            || packet.packet_type == CultNetRudpPacketType::Pong
        {
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: packet.packet_type == CultNetRudpPacketType::Pong,
                pong_payload: if packet.packet_type == CultNetRudpPacketType::Pong {
                    packet.payload.clone()
                } else {
                    Vec::new()
                },
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        }

        if packet.packet_type == CultNetRudpPacketType::Disconnect {
            self.connected = false;
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: false,
                pong_payload: Vec::new(),
                disconnected: true,
                disconnect_reason: packet.payload.clone(),
            });
        }

        if packet.packet_type != CultNetRudpPacketType::Data {
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: false,
                pong_payload: Vec::new(),
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        }

        let duplicate = packet.reliable
            && (self.received_sequences.contains(&packet.sequence)
                || self.highest_received_sequence.is_some_and(|highest| {
                    packet.sequence < highest
                        && highest - packet.sequence >= RUDP_RECEIVED_SEQUENCE_WINDOW as u32
                }));
        if packet.reliable {
            self.remember_received(packet.sequence);
        }
        if duplicate {
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: false,
                pong_payload: Vec::new(),
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        }

        let Some((frame, ordered, next_sequence)) = self.reassemble(packet)? else {
            return Ok(CultNetRudpReceiveResult {
                delivered: Vec::new(),
                ready_to_send,
                reply: None,
                pong: false,
                pong_payload: Vec::new(),
                disconnected: false,
                disconnect_reason: Vec::new(),
            });
        };
        let delivered = if ordered {
            self.deliver_ordered(frame, next_sequence, expected_sequence_if_uninitialized)
        } else if packet.sequenced {
            let latest = self
                .latest_sequenced_by_channel
                .get(&frame.channel_id)
                .copied();
            let newest_sequence = next_sequence.saturating_sub(1);
            if latest.is_some_and(|latest| newest_sequence <= latest) {
                Vec::new()
            } else {
                self.latest_sequenced_by_channel
                    .insert(frame.channel_id.clone(), newest_sequence);
                vec![frame]
            }
        } else {
            vec![frame]
        };
        Ok(CultNetRudpReceiveResult {
            delivered,
            ready_to_send,
            reply: None,
            pong: false,
            pong_payload: Vec::new(),
            disconnected: false,
            disconnect_reason: Vec::new(),
        })
    }

    pub fn create_ack(&mut self) -> CultNetRudpPacket {
        let (ack, ack_mask) = self.ack_state();
        CultNetRudpPacket {
            packet_type: CultNetRudpPacketType::Ack,
            connection_id: self.connection_id,
            sequence: 0,
            ack,
            ack_mask,
            channel_id: "control".to_string(),
            reliable: false,
            ordered: false,
            sequenced: false,
            fragment_id: 0,
            fragment_index: 0,
            fragment_count: 0,
            payload: Vec::new(),
        }
    }

    pub fn create_ack_for(&self, sequence: u32) -> CultNetRudpPacket {
        CultNetRudpPacket {
            packet_type: CultNetRudpPacketType::Ack,
            connection_id: self.connection_id,
            sequence: 0,
            ack: sequence,
            ack_mask: 0,
            channel_id: "control".to_string(),
            reliable: false,
            ordered: false,
            sequenced: false,
            fragment_id: 0,
            fragment_index: 0,
            fragment_count: 0,
            payload: Vec::new(),
        }
    }

    pub fn create_ack_for_received(&mut self, sequence: u32) -> CultNetRudpPacket {
        let (ack, _) = self.ack_state();
        if ack >= sequence && ack - sequence <= 32 {
            self.create_ack()
        } else {
            self.create_ack_for(sequence)
        }
    }

    pub fn create_ping(&mut self, payload: Vec<u8>) -> CultNetRudpPacket {
        self.create_packet(
            CultNetRudpPacketType::Ping,
            "control",
            payload,
            false,
            false,
            false,
        )
    }

    pub fn create_disconnect(&mut self, reason: Vec<u8>) -> CultNetRudpPacket {
        self.connected = false;
        self.create_packet(
            CultNetRudpPacketType::Disconnect,
            "control",
            reason,
            false,
            false,
            false,
        )
    }

    pub fn check_timeout(&mut self, now_ms: u64, timeout_ms: u64) -> bool {
        if !self.connected {
            return false;
        }
        let Some(last_received_at_ms) = self.last_received_at_ms else {
            return false;
        };
        if now_ms.saturating_sub(last_received_at_ms) <= timeout_ms {
            return false;
        }
        self.connected = false;
        true
    }

    pub fn due_resends(&mut self, now_ms: u64) -> Vec<CultNetRudpPacket> {
        let mut due = Vec::new();
        for pending in self.pending_reliable.values_mut() {
            if now_ms.saturating_sub(pending.last_sent_at_ms) >= self.resend_delay_ms {
                pending.last_sent_at_ms = now_ms;
                due.push(pending.packet.clone());
            }
        }
        due.sort_by_key(|packet| packet.sequence);
        due
    }

    fn create_packet(
        &mut self,
        packet_type: CultNetRudpPacketType,
        channel_id: &str,
        payload: Vec<u8>,
        reliable: bool,
        ordered: bool,
        sequenced: bool,
    ) -> CultNetRudpPacket {
        self.create_packet_with_fragments(
            packet_type,
            channel_id,
            payload,
            reliable,
            ordered,
            sequenced,
            0,
            0,
            0,
        )
    }

    #[allow(clippy::too_many_arguments)]
    fn create_packet_with_fragments(
        &mut self,
        packet_type: CultNetRudpPacketType,
        channel_id: &str,
        payload: Vec<u8>,
        reliable: bool,
        ordered: bool,
        sequenced: bool,
        fragment_id: u16,
        fragment_index: u16,
        fragment_count: u16,
    ) -> CultNetRudpPacket {
        let sequence = if reliable {
            let sequence = self.next_sequence;
            self.next_sequence = self
                .next_sequence
                .checked_add(1)
                .expect("reliable sequence overflow");
            sequence
        } else if sequenced {
            let next = self
                .next_sequenced_by_channel
                .entry(channel_id.to_string())
                .or_insert(1);
            let sequence = *next;
            *next = next.checked_add(1).expect("sequenced channel overflow");
            sequence
        } else {
            0
        };
        let (ack, ack_mask) = self.ack_state();
        CultNetRudpPacket {
            packet_type,
            connection_id: self.connection_id,
            sequence,
            ack,
            ack_mask,
            channel_id: channel_id.to_string(),
            reliable,
            ordered,
            sequenced,
            fragment_id,
            fragment_index,
            fragment_count,
            payload,
        }
    }

    fn track_reliable(&mut self, packet: CultNetRudpPacket, now_ms: u64) {
        self.pending_reliable.insert(
            packet.sequence,
            PendingReliablePacket {
                packet,
                last_sent_at_ms: now_ms,
            },
        );
    }

    fn admit_reliable_packets(
        &mut self,
        packets: Vec<CultNetRudpPacket>,
        now_ms: u64,
    ) -> Vec<CultNetRudpPacket> {
        let available =
            CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS.saturating_sub(self.pending_reliable.len());
        let mut ready = Vec::with_capacity(available.min(packets.len()));
        for packet in packets {
            if ready.len() < available {
                self.track_reliable(packet.clone(), now_ms);
                ready.push(packet);
            } else {
                self.queued_reliable.push_back(packet);
            }
        }
        ready
    }

    fn promote_queued_reliable(&mut self, now_ms: u64) -> Vec<CultNetRudpPacket> {
        let available =
            CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS.saturating_sub(self.pending_reliable.len());
        let mut ready = Vec::with_capacity(available.min(self.queued_reliable.len()));
        for _ in 0..available {
            let Some(packet) = self.queued_reliable.pop_front() else {
                break;
            };
            self.track_reliable(packet.clone(), now_ms);
            ready.push(packet);
        }
        ready
    }

    fn ensure_reliable_capacity(&self, packet_count: usize) -> Result<()> {
        if packet_count == 0 {
            return Ok(());
        }
        if let Some(limit) = self.max_pending_reliable_packets {
            if self.outstanding_reliable_packet_count() + packet_count > limit {
                return Err(anyhow!("RUDP reliable send queue is full"));
            }
        }
        Ok(())
    }

    fn apply_acknowledgements(&mut self, packet: &CultNetRudpPacket) {
        self.pending_reliable.remove(&packet.ack);
        for bit in 0..32 {
            if (packet.ack_mask & (1_u32 << bit)) != 0 && packet.ack > bit {
                self.pending_reliable.remove(&(packet.ack - bit - 1));
            }
        }
    }

    fn remember_received(&mut self, sequence: u32) {
        self.received_sequences.insert(sequence);
        if self
            .highest_received_sequence
            .is_none_or(|highest| sequence > highest)
        {
            self.highest_received_sequence = Some(sequence);
        }
        if self.received_sequences.len() > RUDP_RECEIVED_SEQUENCE_WINDOW {
            let keep_from = self
                .highest_received_sequence
                .unwrap_or(sequence)
                .saturating_sub(RUDP_RECEIVED_SEQUENCE_WINDOW as u32 - 1);
            self.received_sequences = self.received_sequences.split_off(&keep_from);
        }
    }

    fn ack_state(&self) -> (u32, u32) {
        let ack = self.highest_received_sequence.unwrap_or(0);
        let mut ack_mask = 0_u32;
        for bit in 0..32 {
            if ack > bit && self.received_sequences.contains(&(ack - bit - 1)) {
                ack_mask |= 1_u32 << bit;
            }
        }
        (ack, ack_mask)
    }

    fn reassemble(
        &mut self,
        packet: &CultNetRudpPacket,
    ) -> Result<Option<(CultNetRudpDeliveredFrame, bool, u32)>> {
        if packet.fragment_count == 0 {
            self.require_payload_size(packet.payload.len())?;
            return Ok(Some((
                CultNetRudpDeliveredFrame {
                    channel_id: packet.channel_id.clone(),
                    payload: packet.payload.clone(),
                    sequence: packet.sequence,
                },
                packet.ordered,
                packet.sequence + 1,
            )));
        }
        if packet.fragment_id == 0 {
            return Err(anyhow!(
                "RUDP fragmented packet must have a non-zero fragment id"
            ));
        }
        if packet.fragment_index >= packet.fragment_count {
            return Err(anyhow!(
                "RUDP fragment index must be lower than fragment count"
            ));
        }

        let key = (packet.channel_id.clone(), packet.fragment_id);
        if !self.fragment_buffers.contains_key(&key)
            && self.fragment_buffers.len() >= self.max_pending_fragment_sets
        {
            return Err(anyhow!("RUDP pending fragment-set limit reached"));
        }
        let buffered_bytes = self
            .fragment_buffers
            .get(&key)
            .map(|buffer| {
                buffer
                    .payloads
                    .iter()
                    .filter(|(index, _)| **index != packet.fragment_index)
                    .map(|(_, payload)| payload.len())
                    .sum::<usize>()
            })
            .unwrap_or(0);
        if let Err(error) = self.require_payload_size(buffered_bytes + packet.payload.len()) {
            self.fragment_buffers.remove(&key);
            return Err(error);
        }
        let buffer = self
            .fragment_buffers
            .entry(key.clone())
            .or_insert_with(|| FragmentBuffer {
                channel_id: packet.channel_id.clone(),
                ordered: packet.ordered,
                fragment_count: packet.fragment_count,
                payloads: BTreeMap::new(),
                sequences: BTreeMap::new(),
            });
        if buffer.fragment_count != packet.fragment_count || buffer.ordered != packet.ordered {
            return Err(anyhow!(
                "RUDP fragment metadata changed within a fragment set"
            ));
        }
        buffer
            .payloads
            .insert(packet.fragment_index, packet.payload.clone());
        buffer
            .sequences
            .insert(packet.fragment_index, packet.sequence);
        if buffer.payloads.len() < packet.fragment_count as usize {
            return Ok(None);
        }

        let mut payload = Vec::new();
        let mut sequences = Vec::new();
        for index in 0..packet.fragment_count {
            let Some(chunk) = buffer.payloads.get(&index) else {
                return Ok(None);
            };
            let Some(sequence) = buffer.sequences.get(&index) else {
                return Ok(None);
            };
            payload.extend_from_slice(chunk);
            sequences.push(*sequence);
        }
        let channel_id = buffer.channel_id.clone();
        let ordered = buffer.ordered;
        self.fragment_buffers.remove(&key);
        Ok(Some((
            CultNetRudpDeliveredFrame {
                channel_id,
                payload,
                sequence: *sequences.iter().min().unwrap(),
            },
            ordered,
            sequences.iter().max().unwrap() + 1,
        )))
    }

    fn require_payload_size(&self, payload_bytes: usize) -> Result<()> {
        if self
            .max_payload_bytes
            .is_some_and(|max_payload_bytes| payload_bytes > max_payload_bytes)
        {
            return Err(anyhow!("RUDP payload exceeds max_payload_bytes"));
        }
        Ok(())
    }

    fn deliver_ordered(
        &mut self,
        frame: CultNetRudpDeliveredFrame,
        next_sequence_after_frame: u32,
        expected_sequence_if_uninitialized: u32,
    ) -> Vec<CultNetRudpDeliveredFrame> {
        let channel_id = frame.channel_id.clone();
        let mut next = if let Some(next) = self
            .ordered_next_sequence_by_channel
            .get(&channel_id)
            .copied()
        {
            next
        } else {
            self.ordered_next_sequence_by_channel.insert(
                channel_id.clone(),
                expected_sequence_if_uninitialized.min(frame.sequence),
            );
            expected_sequence_if_uninitialized.min(frame.sequence)
        };

        while frame.sequence > next
            && self.received_sequences.contains(&next)
            && !self
                .ordered_buffers
                .get(&channel_id)
                .is_some_and(|buffer| buffer.contains_key(&next))
        {
            next = next.saturating_add(1);
            self.ordered_next_sequence_by_channel
                .insert(channel_id.clone(), next);
        }

        if frame.sequence < next {
            return Vec::new();
        }

        if frame.sequence > next {
            self.ordered_buffers.entry(channel_id).or_default().insert(
                frame.sequence,
                PendingOrderedFrame {
                    frame,
                    next_sequence: next_sequence_after_frame,
                },
            );
            return Vec::new();
        }

        self.ordered_next_sequence_by_channel
            .insert(channel_id.clone(), next_sequence_after_frame);
        let mut delivered = vec![frame];
        delivered.extend(self.drain_ordered(&channel_id));
        delivered
    }

    fn drain_ordered(&mut self, channel_id: &str) -> Vec<CultNetRudpDeliveredFrame> {
        let mut delivered = Vec::new();
        loop {
            let Some(next) = self
                .ordered_next_sequence_by_channel
                .get(channel_id)
                .copied()
            else {
                break;
            };
            let Some(buffer) = self.ordered_buffers.get_mut(channel_id) else {
                break;
            };
            let Some(pending) = buffer.remove(&next) else {
                break;
            };
            delivered.push(pending.frame);
            self.ordered_next_sequence_by_channel
                .insert(channel_id.to_string(), pending.next_sequence);
            self.skip_received_non_channel_sequences(channel_id);
        }
        delivered
    }

    fn skip_received_non_channel_sequences(&mut self, channel_id: &str) {
        let Some(mut next) = self
            .ordered_next_sequence_by_channel
            .get(channel_id)
            .copied()
        else {
            return;
        };
        while self.received_sequences.contains(&next)
            && !self
                .ordered_buffers
                .get(channel_id)
                .is_some_and(|buffer| buffer.contains_key(&next))
        {
            next = next.saturating_add(1);
            self.ordered_next_sequence_by_channel
                .insert(channel_id.to_string(), next);
        }
    }

    fn allocate_fragment_id(&mut self) -> u16 {
        let fragment_id = self.next_fragment_id;
        self.next_fragment_id = self.next_fragment_id.saturating_add(1);
        if self.next_fragment_id == 0 {
            self.next_fragment_id = 1;
        }
        fragment_id
    }

    fn require_connection(&self, packet: &CultNetRudpPacket) -> Result<()> {
        if packet.connection_id != self.connection_id {
            return Err(anyhow!(
                "RUDP packet connection id {} does not match {}",
                packet.connection_id,
                self.connection_id
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CultNetRudpSocketMode {
    Client,
    Server,
}

pub struct CultNetRudpSocketTransportOptions {
    pub runtime_id: String,
    pub socket: UdpSocket,
    pub mode: CultNetRudpSocketMode,
    pub remote_addr: Option<SocketAddr>,
    pub connection_id: u32,
    pub initial_sequence: u32,
    pub resend_delay_ms: u64,
    pub transport_id: Option<String>,
    pub max_payload_bytes: Option<u32>,
    pub max_fragment_bytes: Option<u32>,
    pub max_pending_reliable_packets: Option<u32>,
    pub reconnect_policy: Option<CultNetReconnectPolicy>,
}

impl CultNetRudpSocketTransportOptions {
    pub fn client(
        runtime_id: impl Into<String>,
        socket: UdpSocket,
        remote_addr: SocketAddr,
        connection_id: u32,
    ) -> Self {
        Self {
            runtime_id: runtime_id.into(),
            socket,
            mode: CultNetRudpSocketMode::Client,
            remote_addr: Some(remote_addr),
            connection_id,
            initial_sequence: 1,
            resend_delay_ms: 250,
            transport_id: None,
            max_payload_bytes: None,
            max_fragment_bytes: None,
            max_pending_reliable_packets: None,
            reconnect_policy: None,
        }
    }

    pub fn server(runtime_id: impl Into<String>, socket: UdpSocket, connection_id: u32) -> Self {
        Self {
            runtime_id: runtime_id.into(),
            socket,
            mode: CultNetRudpSocketMode::Server,
            remote_addr: None,
            connection_id,
            initial_sequence: 1,
            resend_delay_ms: 250,
            transport_id: None,
            max_payload_bytes: None,
            max_fragment_bytes: None,
            max_pending_reliable_packets: None,
            reconnect_policy: None,
        }
    }
}

pub struct CultNetRudpSocketTransportConnection {
    socket: UdpSocket,
    session: CultNetRudpSession,
    mode: CultNetRudpSocketMode,
    remote_addr: Option<SocketAddr>,
    pub profile: CultNetTransportProfile,
    stats: CultNetTransportStats,
    delivered_frames: VecDeque<CultNetTransportFrame>,
    max_fragment_bytes: Option<usize>,
    disconnect_reason: Option<Vec<u8>>,
    pong_payloads: VecDeque<Vec<u8>>,
}

pub struct CultNetRudpServerHubOptions {
    pub runtime_id: String,
    pub socket: UdpSocket,
    pub connection_id: u32,
    pub initial_sequence: u32,
    pub resend_delay_ms: u64,
    pub transport_id: Option<String>,
    pub max_payload_bytes: Option<u32>,
    pub max_fragment_bytes: Option<u32>,
    pub max_pending_reliable_packets: Option<u32>,
    pub max_peers: usize,
}

impl CultNetRudpServerHubOptions {
    pub fn new(runtime_id: impl Into<String>, socket: UdpSocket, connection_id: u32) -> Self {
        Self {
            runtime_id: runtime_id.into(),
            socket,
            connection_id,
            initial_sequence: 1,
            resend_delay_ms: 250,
            transport_id: None,
            max_payload_bytes: None,
            max_fragment_bytes: None,
            max_pending_reliable_packets: None,
            max_peers: 256,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetRudpServerSessionContext {
    pub remote_addr: SocketAddr,
    pub connection_id: u32,
    /// Server-minted generation used to fence work from an older session at
    /// the same UDP endpoint.
    pub session_generation: u64,
    /// Exact bytes supplied by the peer's most recently accepted Connect packet.
    /// CultNet does not interpret these bytes; higher layers may use them as
    /// transport evidence when making their own authorization decision.
    pub connect_payload: Vec<u8>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CultNetRudpServerEvent {
    Connected {
        session: CultNetRudpServerSessionContext,
    },
    Frame {
        session: CultNetRudpServerSessionContext,
        frame: CultNetTransportFrame,
    },
    Pong {
        session: CultNetRudpServerSessionContext,
        payload: Vec<u8>,
    },
    Disconnected {
        session: CultNetRudpServerSessionContext,
        reason: Vec<u8>,
    },
}

struct CultNetRudpServerPeer {
    session: CultNetRudpSession,
    context: CultNetRudpServerSessionContext,
}

/// One UDP listener with an independent reliable session for every remote peer.
///
/// The hub owns packet/session mechanics only. Registration, leases, commands,
/// and all other application policy belong to the service using the hub.
pub struct CultNetRudpServerHub {
    socket: UdpSocket,
    connection_id: u32,
    initial_sequence: u32,
    resend_delay_ms: u64,
    max_pending_reliable_packets: Option<usize>,
    max_payload_bytes: usize,
    max_fragment_bytes: Option<usize>,
    max_peers: usize,
    next_session_generation: u64,
    peers: BTreeMap<SocketAddr, CultNetRudpServerPeer>,
    pending_events: VecDeque<CultNetRudpServerEvent>,
    pub profile: CultNetTransportProfile,
    stats: CultNetTransportStats,
}

impl CultNetRudpServerHub {
    pub fn new(options: CultNetRudpServerHubOptions) -> Result<Self> {
        let local_addr = options.socket.local_addr()?;
        if options.max_peers == 0 {
            return Err(anyhow!(
                "RUDP server hub max_peers must be greater than zero"
            ));
        }
        let max_payload_bytes = options
            .max_payload_bytes
            .or(Some(CULTNET_RUDP_DEFAULT_MAX_PAYLOAD_BYTES as u32));
        let profile = create_rudp_transport_profile(
            options.runtime_id,
            RudpTransportProfileOptions {
                transport_id: options.transport_id,
                host: Some(local_addr.ip().to_string()),
                port: Some(local_addr.port()),
                max_payload_bytes,
                max_fragment_bytes: options.max_fragment_bytes,
                max_pending_reliable_packets: options.max_pending_reliable_packets,
                reconnect_policy: None,
            },
        );
        Ok(Self {
            socket: options.socket,
            connection_id: options.connection_id,
            initial_sequence: options.initial_sequence,
            resend_delay_ms: options.resend_delay_ms,
            max_pending_reliable_packets: options
                .max_pending_reliable_packets
                .map(|value| value as usize),
            max_payload_bytes: max_payload_bytes.expect("RUDP hub has an effective payload limit")
                as usize,
            max_fragment_bytes: options.max_fragment_bytes.map(|value| value as usize),
            max_peers: options.max_peers,
            next_session_generation: 1,
            peers: BTreeMap::new(),
            pending_events: VecDeque::new(),
            profile,
            stats: CultNetTransportStats::default(),
        })
    }

    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.socket.local_addr()?)
    }

    pub fn stats(&self) -> CultNetTransportStats {
        self.stats.clone()
    }

    pub fn sessions(&self) -> Vec<CultNetRudpServerSessionContext> {
        self.peers
            .values()
            .map(|peer| peer.context.clone())
            .collect()
    }

    pub fn session(&self, remote_addr: SocketAddr) -> Option<&CultNetRudpServerSessionContext> {
        self.peers.get(&remote_addr).map(|peer| &peer.context)
    }

    pub fn send(
        &mut self,
        session: &CultNetRudpServerSessionContext,
        channel_id: &str,
        payload: Vec<u8>,
    ) -> Result<()> {
        let remote_addr = session.remote_addr;
        let packets = {
            let peer = self
                .peers
                .get_mut(&remote_addr)
                .ok_or_else(|| anyhow!("Unknown RUDP server peer {remote_addr}"))?;
            if peer.context.session_generation != session.session_generation {
                return Err(anyhow!(
                    "RUDP server session generation {} is no longer active",
                    session.session_generation
                ));
            }
            peer.session.send_many(
                channel_id,
                payload,
                channel_send_options(channel_id, now_ms()),
                self.max_fragment_bytes,
            )?
        };
        for packet in packets {
            self.send_packet(remote_addr, &packet)?;
        }
        self.stats.frames_sent += 1;
        Ok(())
    }

    pub fn send_schema_message(
        &mut self,
        session: &CultNetRudpServerSessionContext,
        message: &CultNetMessage,
    ) -> Result<()> {
        let payload = encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?;
        self.send(session, "schema", payload)
    }

    pub fn disconnect(
        &mut self,
        session: &CultNetRudpServerSessionContext,
        reason: Vec<u8>,
    ) -> Result<bool> {
        let Some(peer) = self.peers.get(&session.remote_addr) else {
            return Ok(false);
        };
        if peer.context.session_generation != session.session_generation {
            return Ok(false);
        }
        let mut peer = self
            .peers
            .remove(&session.remote_addr)
            .expect("validated RUDP peer exists");
        let packet = peer.session.create_disconnect(reason);
        self.send_packet(session.remote_addr, &packet)?;
        Ok(true)
    }

    pub fn receive_event_once(&mut self) -> Result<Option<CultNetRudpServerEvent>> {
        if let Some(event) = self.pending_events.pop_front() {
            return Ok(Some(event));
        }

        let mut wire = vec![0_u8; 65_535];
        let (received, remote_addr) = match self.socket.recv_from(&mut wire) {
            Ok(value) => value,
            Err(error)
                if error.kind() == ErrorKind::WouldBlock
                    || error.kind() == ErrorKind::TimedOut
                    || error.kind() == ErrorKind::ConnectionReset =>
            {
                return Ok(None);
            }
            Err(error) => return Err(error.into()),
        };
        wire.truncate(received);
        self.stats.bytes_received += received as u64;
        let packet = decode_rudp_packet(&wire)?;
        if packet.connection_id != self.connection_id {
            return Ok(None);
        }

        if packet.packet_type == CultNetRudpPacketType::Connect {
            if let Some(peer) = self.peers.get_mut(&remote_addr)
                && peer.context.connect_payload == packet.payload
            {
                let _ = peer.session.receive(&packet, now_ms())?;
                let reply = peer
                    .session
                    .pending_accept_for_resend(now_ms())
                    .unwrap_or_else(|| peer.session.create_ack());
                self.send_packet(remote_addr, &reply)?;
                return Ok(None);
            }
            if !self.peers.contains_key(&remote_addr) && self.peers.len() >= self.max_peers {
                return Err(anyhow!("RUDP server hub peer limit reached"));
            }
            let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
                connection_id: self.connection_id,
                initial_sequence: self.initial_sequence,
                resend_delay_ms: self.resend_delay_ms,
                max_pending_reliable_packets: self.max_pending_reliable_packets,
            });
            session.set_max_payload_bytes(Some(self.max_payload_bytes));
            let generation = self.next_session_generation;
            self.next_session_generation = self
                .next_session_generation
                .checked_add(1)
                .ok_or_else(|| anyhow!("RUDP server session generation exhausted"))?;
            let context = CultNetRudpServerSessionContext {
                remote_addr,
                connection_id: packet.connection_id,
                session_generation: generation,
                connect_payload: packet.payload.clone(),
            };
            let accept = session.accept_connect(&packet, now_ms(), Vec::new())?;
            self.send_packet(remote_addr, &accept)?;
            let replaced = self.peers.insert(
                remote_addr,
                CultNetRudpServerPeer {
                    session,
                    context: context.clone(),
                },
            );
            if let Some(replaced) = replaced {
                self.pending_events
                    .push_back(CultNetRudpServerEvent::Disconnected {
                        session: replaced.context,
                        reason: b"replaced by a new Connect generation".to_vec(),
                    });
            }
            self.pending_events
                .push_back(CultNetRudpServerEvent::Connected { session: context });
            return Ok(self.pending_events.pop_front());
        }

        let Some(peer) = self.peers.get_mut(&remote_addr) else {
            return Ok(None);
        };
        let result = peer.session.receive(&packet, now_ms())?;
        let context = peer.context.clone();
        let ack = if packet.reliable {
            Some(peer.session.create_ack_for_received(packet.sequence))
        } else {
            None
        };
        if let Some(reply) = result.reply {
            self.send_packet(remote_addr, &reply)?;
        }
        for ready in result.ready_to_send {
            self.send_packet(remote_addr, &ready)?;
        }
        if let Some(ack) = ack {
            self.send_packet(remote_addr, &ack)?;
        }
        if result.pong {
            self.pending_events.push_back(CultNetRudpServerEvent::Pong {
                session: context.clone(),
                payload: result.pong_payload,
            });
        }
        for delivered in result.delivered {
            self.stats.frames_received += 1;
            self.pending_events
                .push_back(CultNetRudpServerEvent::Frame {
                    session: context.clone(),
                    frame: CultNetTransportFrame {
                        channel_id: delivered.channel_id,
                        payload: delivered.payload,
                    },
                });
        }
        if result.disconnected {
            self.pending_events
                .push_back(CultNetRudpServerEvent::Disconnected {
                    session: context,
                    reason: result.disconnect_reason,
                });
            self.peers.remove(&remote_addr);
        }
        Ok(self.pending_events.pop_front())
    }

    pub fn poll_resends(&mut self) -> Result<()> {
        let now = now_ms();
        let mut packets = Vec::new();
        for (remote_addr, peer) in &mut self.peers {
            packets.extend(
                peer.session
                    .due_resends(now)
                    .into_iter()
                    .map(|packet| (*remote_addr, packet)),
            );
        }
        for (remote_addr, packet) in packets {
            self.send_packet(remote_addr, &packet)?;
        }
        Ok(())
    }

    pub fn remove_timed_out_sessions(
        &mut self,
        timeout_ms: u64,
    ) -> Vec<CultNetRudpServerSessionContext> {
        let now = now_ms();
        let timed_out = self
            .peers
            .iter_mut()
            .filter_map(|(remote_addr, peer)| {
                peer.session
                    .check_timeout(now, timeout_ms)
                    .then_some((*remote_addr, peer.context.clone()))
            })
            .collect::<Vec<_>>();
        for (remote_addr, _) in &timed_out {
            self.peers.remove(remote_addr);
        }
        timed_out.into_iter().map(|(_, context)| context).collect()
    }

    fn send_packet(&mut self, remote_addr: SocketAddr, packet: &CultNetRudpPacket) -> Result<()> {
        let wire = encode_rudp_packet(packet)?;
        let sent = self.socket.send_to(&wire, remote_addr)?;
        self.stats.bytes_sent += sent as u64;
        Ok(())
    }
}

impl CultNetRudpSocketTransportConnection {
    pub fn new(options: CultNetRudpSocketTransportOptions) -> Result<Self> {
        let local_addr = options.socket.local_addr()?;
        let max_payload_bytes = options
            .max_payload_bytes
            .or(Some(CULTNET_RUDP_DEFAULT_MAX_PAYLOAD_BYTES as u32));
        let profile = create_rudp_transport_profile(
            options.runtime_id,
            RudpTransportProfileOptions {
                transport_id: options.transport_id,
                host: Some(local_addr.ip().to_string()),
                port: Some(local_addr.port()),
                max_payload_bytes,
                max_fragment_bytes: options.max_fragment_bytes,
                max_pending_reliable_packets: options.max_pending_reliable_packets,
                reconnect_policy: options.reconnect_policy,
            },
        );
        let max_pending_reliable_packets = options
            .max_pending_reliable_packets
            .map(|value| value as usize);
        let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
            connection_id: options.connection_id,
            initial_sequence: options.initial_sequence,
            resend_delay_ms: options.resend_delay_ms,
            max_pending_reliable_packets,
        });
        session.set_max_payload_bytes(max_payload_bytes.map(|value| value as usize));
        Ok(Self {
            socket: options.socket,
            session,
            mode: options.mode,
            remote_addr: options.remote_addr,
            profile,
            stats: CultNetTransportStats::default(),
            delivered_frames: VecDeque::new(),
            max_fragment_bytes: options.max_fragment_bytes.map(|value| value as usize),
            disconnect_reason: None,
            pong_payloads: VecDeque::new(),
        })
    }

    pub fn connected(&self) -> bool {
        self.session.connected()
    }

    pub fn stats(&self) -> CultNetTransportStats {
        self.stats.clone()
    }

    pub fn disconnect_reason(&self) -> Option<&[u8]> {
        self.disconnect_reason.as_deref()
    }

    pub fn pop_pong_payload(&mut self) -> Option<Vec<u8>> {
        self.pong_payloads.pop_front()
    }

    pub fn connect(&mut self, payload: Vec<u8>) -> Result<()> {
        if self.mode != CultNetRudpSocketMode::Client {
            return Err(anyhow!(
                "Only a client RUDP socket transport can initiate connect"
            ));
        }
        let packet = self.session.create_connect(now_ms(), payload)?;
        self.send_packet(&packet)
    }

    pub fn send(&mut self, channel_id: &str, payload: Vec<u8>) -> Result<()> {
        let options = channel_send_options(channel_id, now_ms());
        let packets =
            self.session
                .send_many(channel_id, payload, options, self.max_fragment_bytes)?;
        self.send_data_packets(&packets)?;
        self.stats.frames_sent += 1;
        Ok(())
    }

    /// Sends one frame on a reliable channel whose packets cannot expire and
    /// returns the transport receipt used to observe its exact acknowledgement.
    pub fn send_reliable(
        &mut self,
        channel_id: &str,
        payload: Vec<u8>,
    ) -> Result<CultNetRudpReliableSendReceipt> {
        let options = channel_send_options(channel_id, now_ms());
        // This lineage has no reliable-expiry concept, so every reliable channel is
        // already non-expiring. Restore the expiry half of this guard if one is added.
        if !options.reliable {
            return Err(anyhow!(
                "RUDP acknowledgement receipts require a non-expiring reliable channel"
            ));
        }
        let packets =
            self.session
                .send_many(channel_id, payload, options, self.max_fragment_bytes)?;
        self.send_data_packets(&packets)?;
        let receipt = self.session.non_expiring_reliable_receipt(&packets)?;
        self.stats.frames_sent += 1;
        Ok(receipt)
    }

    pub fn reliable_send_status(
        &self,
        receipt: &CultNetRudpReliableSendReceipt,
    ) -> CultNetRudpReliableSendStatus {
        self.session.reliable_send_status(receipt)
    }

    fn send_data_packets(&mut self, packets: &[CultNetRudpPacket]) -> Result<()> {
        let fragmented = packets.len() > 1;
        for (index, packet) in packets.iter().enumerate() {
            self.send_packet(packet)?;
            if fragmented && (index + 1) % 16 == 0 {
                thread::sleep(Duration::from_millis(1));
            }
        }
        Ok(())
    }

    pub fn send_schema_message(&mut self, message: &CultNetMessage) -> Result<()> {
        let payload = encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?;
        self.send("schema", payload)
    }

    pub fn receive_schema_message_once(&mut self) -> Result<Option<CultNetMessage>> {
        let Some(frame) = self.receive_once()? else {
            return Ok(None);
        };
        if frame.channel_id != "schema" {
            return Err(anyhow!(
                "Expected RUDP schema frame, received channel {}",
                frame.channel_id
            ));
        }
        Ok(Some(decode_cultnet_message_from_slice(
            &frame.payload,
            CultNetWireContract::CultNetSchemaV0,
        )?))
    }

    pub fn disconnect(&mut self, reason: Vec<u8>) -> Result<()> {
        let packet = self.session.create_disconnect(reason);
        self.send_packet(&packet)
    }

    pub fn ping(&mut self, payload: Vec<u8>) -> Result<()> {
        let packet = self.session.create_ping(payload);
        self.send_packet(&packet)
    }

    pub fn check_timeout(&mut self, timeout_ms: u64) -> bool {
        self.session.check_timeout(now_ms(), timeout_ms)
    }

    pub fn pending_reliable_packet_count(&self) -> usize {
        self.session.pending_reliable_sequences().len()
    }

    pub fn outstanding_reliable_packet_count(&self) -> usize {
        self.session.outstanding_reliable_packet_count()
    }

    pub fn flush_reliable(&mut self, timeout: Duration) -> Result<()> {
        let original_timeout = self.socket.read_timeout()?;
        let poll_timeout = original_timeout
            .map(|configured| configured.min(Duration::from_millis(10)))
            .unwrap_or(Duration::from_millis(10));
        self.socket.set_read_timeout(Some(poll_timeout))?;

        let deadline = Instant::now() + timeout;
        let mut preserved_frames = VecDeque::new();
        let result = (|| {
            while self.outstanding_reliable_packet_count() > 0 {
                if Instant::now() >= deadline {
                    return Err(anyhow!(
                        "RUDP reliable flush timed out with {} packets outstanding",
                        self.outstanding_reliable_packet_count()
                    ));
                }
                if let Some(frame) = self.receive_once()? {
                    preserved_frames.push_back(frame);
                }
                self.poll_resends()?;
            }
            Ok(())
        })();

        preserved_frames.append(&mut self.delivered_frames);
        self.delivered_frames = preserved_frames;
        let restore = self.socket.set_read_timeout(original_timeout);
        match (result, restore) {
            (Err(error), _) => Err(error),
            (Ok(()), Err(error)) => Err(error.into()),
            (Ok(()), Ok(())) => Ok(()),
        }
    }

    pub fn receive_once(&mut self) -> Result<Option<CultNetTransportFrame>> {
        if let Some(frame) = self.delivered_frames.pop_front() {
            return Ok(Some(frame));
        }

        let mut wire = vec![0_u8; 65_535];
        let (received, remote_addr) = match self.socket.recv_from(&mut wire) {
            Ok(value) => value,
            Err(error)
                if error.kind() == ErrorKind::WouldBlock
                    || error.kind() == ErrorKind::TimedOut
                    || error.kind() == ErrorKind::ConnectionReset =>
            {
                return Ok(None);
            }
            Err(error) => return Err(error.into()),
        };
        wire.truncate(received);
        self.stats.bytes_received += received as u64;

        let packet = decode_rudp_packet(&wire)?;
        if let Some(expected) = self.remote_addr {
            if expected != remote_addr {
                if self.mode == CultNetRudpSocketMode::Server
                    && packet.packet_type == CultNetRudpPacketType::Connect
                {
                    self.remote_addr = Some(remote_addr);
                } else {
                    return Ok(None);
                }
            }
        } else {
            if self.mode == CultNetRudpSocketMode::Server
                && packet.packet_type != CultNetRudpPacketType::Connect
            {
                return Ok(None);
            }
            self.remote_addr = Some(remote_addr);
        }
        if self.mode == CultNetRudpSocketMode::Server
            && packet.packet_type == CultNetRudpPacketType::Connect
        {
            self.session.reset_peer_state();
            let accept = self.session.accept_connect(&packet, now_ms(), Vec::new())?;
            self.send_packet(&accept)?;
            return Ok(None);
        }

        let result = self.session.receive(&packet, now_ms())?;
        if let Some(reply) = result.reply {
            self.send_packet(&reply)?;
        }
        for ready in result.ready_to_send {
            self.send_packet(&ready)?;
        }
        if result.pong {
            self.pong_payloads.push_back(result.pong_payload);
        }
        if result.disconnected {
            self.disconnect_reason = Some(result.disconnect_reason);
            return Ok(None);
        }

        for frame in result.delivered {
            self.delivered_frames.push_back(CultNetTransportFrame {
                channel_id: frame.channel_id,
                payload: frame.payload,
            });
            self.stats.frames_received += 1;
        }
        let frame = self.delivered_frames.pop_front();
        if packet.reliable || packet.packet_type == CultNetRudpPacketType::Accept || frame.is_some()
        {
            let ack = self.session.create_ack_for_received(packet.sequence);
            self.send_packet(&ack)?;
        }
        Ok(frame)
    }

    pub fn poll_resends(&mut self) -> Result<()> {
        for packet in self.session.due_resends(now_ms()) {
            self.send_packet(&packet)?;
        }
        Ok(())
    }

    fn send_packet(&mut self, packet: &CultNetRudpPacket) -> Result<()> {
        let Some(remote_addr) = self.remote_addr else {
            return Err(anyhow!(
                "RUDP socket transport does not have a remote endpoint"
            ));
        };
        let wire = encode_rudp_packet(packet)?;
        let sent = self.socket.send_to(&wire, remote_addr)?;
        self.stats.bytes_sent += sent as u64;
        Ok(())
    }
}

pub struct CultNetRudpReconnectLoop<F>
where
    F: FnMut() -> Result<CultNetRudpSocketTransportConnection>,
{
    pub reconnect_controller: CultNetReconnectController,
    create_transport: F,
    connect_payload: Vec<u8>,
    transport: Option<CultNetRudpSocketTransportConnection>,
    stopped: bool,
}

impl<F> CultNetRudpReconnectLoop<F>
where
    F: FnMut() -> Result<CultNetRudpSocketTransportConnection>,
{
    pub fn new(
        reconnect_policy: CultNetReconnectPolicy,
        connect_payload: Vec<u8>,
        create_transport: F,
    ) -> Self {
        Self {
            reconnect_controller: CultNetReconnectController::new(reconnect_policy),
            create_transport,
            connect_payload,
            transport: None,
            stopped: true,
        }
    }

    pub fn with_default_policy(connect_payload: Vec<u8>, create_transport: F) -> Self {
        Self::new(
            create_reconnect_policy(Default::default()),
            connect_payload,
            create_transport,
        )
    }

    pub fn transport(&self) -> Option<&CultNetRudpSocketTransportConnection> {
        self.transport.as_ref()
    }

    pub fn transport_mut(&mut self) -> Option<&mut CultNetRudpSocketTransportConnection> {
        self.transport.as_mut()
    }

    pub fn start(&mut self) -> Result<&mut CultNetRudpSocketTransportConnection> {
        self.stopped = false;
        self.reconnect_controller.reset();
        self.open_transport()
    }

    pub fn stop(&mut self) {
        self.stopped = true;
        self.transport = None;
        self.reconnect_controller.reset();
    }

    pub fn mark_connected(&mut self) {
        self.reconnect_controller.reset();
    }

    pub fn handle_closed(
        &mut self,
        now_ms: u64,
        jitter_ms: u64,
    ) -> Option<CultNetReconnectDecision> {
        self.transport = None;
        if self.stopped {
            return None;
        }
        Some(self.reconnect_controller.record_failure(now_ms, jitter_ms))
    }

    pub fn reconnect_if_due(&mut self, now_ms: u64) -> Result<bool> {
        if self.stopped || !self.reconnect_controller.can_attempt(now_ms) {
            return Ok(false);
        }
        self.open_transport()?;
        Ok(true)
    }

    fn open_transport(&mut self) -> Result<&mut CultNetRudpSocketTransportConnection> {
        let mut transport = (self.create_transport)()?;
        transport.connect(self.connect_payload.clone())?;
        self.transport = Some(transport);
        Ok(self
            .transport
            .as_mut()
            .expect("RUDP reconnect loop opened a transport"))
    }
}

#[derive(Clone, Debug, Default)]
pub struct RudpTransportProfileOptions {
    pub transport_id: Option<String>,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub max_payload_bytes: Option<u32>,
    pub max_fragment_bytes: Option<u32>,
    pub max_pending_reliable_packets: Option<u32>,
    pub reconnect_policy: Option<CultNetReconnectPolicy>,
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
            reconnect_policy: Some(
                options
                    .reconnect_policy
                    .unwrap_or_else(|| create_reconnect_policy(Default::default())),
            ),
            channels: vec![
                CultNetTransportChannel {
                    channel_id: "schema".to_string(),
                    delivery: CultNetTransportDelivery::Reliable,
                    ordering: CultNetTransportOrdering::Ordered,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                    max_pending_reliable_packets: options.max_pending_reliable_packets,
                },
                CultNetTransportChannel {
                    channel_id: "latest".to_string(),
                    delivery: CultNetTransportDelivery::Unreliable,
                    ordering: CultNetTransportOrdering::Sequenced,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                    max_pending_reliable_packets: options.max_pending_reliable_packets,
                },
                CultNetTransportChannel {
                    channel_id: "realtime".to_string(),
                    delivery: CultNetTransportDelivery::Unreliable,
                    ordering: CultNetTransportOrdering::Unordered,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                    max_pending_reliable_packets: options.max_pending_reliable_packets,
                },
                CultNetTransportChannel {
                    channel_id: "media".to_string(),
                    delivery: CultNetTransportDelivery::Reliable,
                    ordering: CultNetTransportOrdering::Unordered,
                    max_payload_bytes: options.max_payload_bytes,
                    max_fragment_bytes: options.max_fragment_bytes,
                    max_pending_reliable_packets: options.max_pending_reliable_packets,
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

fn channel_send_options(channel_id: &str, now_ms: u64) -> CultNetRudpSendOptions {
    match channel_id {
        "schema" => CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
            sequenced: false,
            now_ms,
        },
        "latest" => CultNetRudpSendOptions {
            reliable: false,
            ordered: false,
            sequenced: true,
            now_ms,
        },
        "media" => CultNetRudpSendOptions {
            reliable: true,
            ordered: false,
            sequenced: false,
            now_ms,
        },
        _ => CultNetRudpSendOptions {
            reliable: false,
            ordered: false,
            sequenced: false,
            now_ms,
        },
    }
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as u64)
        .unwrap_or(0)
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
