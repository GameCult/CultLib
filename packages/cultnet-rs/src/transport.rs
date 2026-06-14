use anyhow::Result;
use anyhow::anyhow;
use std::io::Read;
use std::io::Write;

use crate::CultNetTransportChannel;
use crate::CultNetTransportDelivery;
use crate::CultNetTransportDescriptor;
use crate::CultNetTransportOrdering;
use crate::CultNetTransportProfile;
use crate::CultNetTransportProtocol;
use crate::FRAME_HEADER_BYTES;
use crate::encode_frame;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CultNetTransportStats {
    pub bytes_received: u64,
    pub bytes_sent: u64,
    pub frames_received: u64,
    pub frames_sent: u64,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultNetTransportFrame {
    pub channel_id: String,
    pub payload: Vec<u8>,
}

#[derive(Clone, Debug, Default)]
pub struct TcpFramedTransportProfileOptions {
    pub transport_id: Option<String>,
    pub host: Option<String>,
    pub port: Option<u16>,
    pub max_payload_bytes: Option<u32>,
    pub max_fragment_bytes: Option<u32>,
}

pub fn create_tcp_framed_transport_profile(
    runtime_id: impl Into<String>,
    options: TcpFramedTransportProfileOptions,
) -> CultNetTransportProfile {
    CultNetTransportProfile {
        schema_version: "cultnet.transport_profile.v0".to_string(),
        runtime_id: runtime_id.into(),
        transports: vec![CultNetTransportDescriptor {
            transport_id: options
                .transport_id
                .unwrap_or_else(|| "tcp-framed".to_string()),
            protocol: CultNetTransportProtocol::TcpFramed,
            host: options.host,
            port: options.port,
            path: None,
            discovery_group: None,
            wire_contracts: Some(vec!["cultnet.schema.v0".to_string()]),
            channels: vec![CultNetTransportChannel {
                channel_id: "schema".to_string(),
                delivery: CultNetTransportDelivery::Reliable,
                ordering: CultNetTransportOrdering::Ordered,
                max_payload_bytes: options.max_payload_bytes,
                max_fragment_bytes: options.max_fragment_bytes,
            }],
        }],
    }
}

pub struct TcpFramedTransportConnection<TStream> {
    stream: TStream,
    pub profile: CultNetTransportProfile,
    stats: CultNetTransportStats,
}

impl<TStream> TcpFramedTransportConnection<TStream> {
    pub fn new(stream: TStream, profile: CultNetTransportProfile) -> Self {
        Self {
            stream,
            profile,
            stats: CultNetTransportStats::default(),
        }
    }

    pub fn stats(&self) -> CultNetTransportStats {
        self.stats.clone()
    }

    pub fn into_inner(self) -> TStream {
        self.stream
    }
}

impl<TStream> TcpFramedTransportConnection<TStream>
where
    TStream: Write,
{
    pub fn send(&mut self, channel_id: &str, payload: &[u8]) -> Result<()> {
        if channel_id != "schema" {
            return Err(anyhow!(
                "tcp_framed transport only supports the schema channel, got {channel_id:?}"
            ));
        }

        let frame = encode_frame(payload)?;
        self.stream.write_all(&frame)?;
        self.stream.flush()?;
        self.stats.bytes_sent += frame.len() as u64;
        self.stats.frames_sent += 1;
        Ok(())
    }
}

impl<TStream> TcpFramedTransportConnection<TStream>
where
    TStream: Read,
{
    pub fn receive(&mut self) -> Result<CultNetTransportFrame> {
        let mut header = [0_u8; FRAME_HEADER_BYTES];
        self.stream.read_exact(&mut header)?;
        let payload_len = u32::from_be_bytes(header) as usize;
        let mut payload = vec![0_u8; payload_len];
        self.stream.read_exact(&mut payload)?;
        self.stats.bytes_received += (FRAME_HEADER_BYTES + payload_len) as u64;
        self.stats.frames_received += 1;
        Ok(CultNetTransportFrame {
            channel_id: "schema".to_string(),
            payload,
        })
    }
}
