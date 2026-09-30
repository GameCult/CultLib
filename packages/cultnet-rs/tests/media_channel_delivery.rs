//! Media delivery is the caller's choice, and the advertised profile is where
//! that choice lives.
//!
//! Before this, `media` was hardcoded `Reliable` in two places — the profile
//! builder and the per-channel send options — so no caller could say otherwise.
//! A producer asking for an unreliable media lane got a reliable one that
//! retransmits under loss, which adds load exactly when the link has least to
//! give. Muninn asked for that lane by passing no expiry, meaning "unreliable,
//! so expiry is moot", and got reliable-with-no-expiry instead: retransmit
//! forever.
//!
//! The default stays `Reliable` so every existing consumer is untouched.

use std::net::UdpSocket;

use cultnet_rs::{
    CultNetRudpSocketTransportConnection, CultNetRudpSocketTransportOptions,
    CultNetTransportDelivery,
};

fn media_channel_delivery(
    media_delivery: Option<CultNetTransportDelivery>,
) -> (CultNetTransportDelivery, Option<u64>) {
    let socket = UdpSocket::bind("127.0.0.1:0").expect("binds");
    let endpoint = "127.0.0.1:5204".parse().expect("parses");
    let mut options = CultNetRudpSocketTransportOptions::client("test-media", socket, endpoint, 7);
    options.media_delivery = media_delivery;
    options.media_reliable_expire_after_ms = None;

    let transport = CultNetRudpSocketTransportConnection::new(options).expect("opens");
    let channel = transport
        .profile
        .transports
        .first()
        .expect("a transport")
        .channels
        .iter()
        .find(|channel| channel.channel_id == "media")
        .expect("a media channel");
    (channel.delivery, channel.reliable_expire_after_ms)
}

#[test]
fn media_defaults_to_reliable_so_existing_consumers_do_not_move() {
    let (delivery, _) = media_channel_delivery(None);
    assert_eq!(delivery, CultNetTransportDelivery::Reliable);
}

#[test]
fn a_caller_can_ask_for_an_unreliable_media_lane() {
    let (delivery, expiry) = media_channel_delivery(Some(CultNetTransportDelivery::Unreliable));
    assert_eq!(delivery, CultNetTransportDelivery::Unreliable);
    assert_eq!(
        expiry, None,
        "an unreliable channel has nothing to expire; the pairing should stay honest"
    );
}

#[test]
fn asking_for_reliable_explicitly_matches_the_default() {
    let (explicit, _) = media_channel_delivery(Some(CultNetTransportDelivery::Reliable));
    let (defaulted, _) = media_channel_delivery(None);
    assert_eq!(explicit, defaulted);
}

/// The channels whose semantics are fixed by design must not drift with this.
#[test]
fn the_other_channels_keep_their_fixed_delivery() {
    let socket = UdpSocket::bind("127.0.0.1:0").expect("binds");
    let endpoint = "127.0.0.1:5204".parse().expect("parses");
    let mut options = CultNetRudpSocketTransportOptions::client("test-media", socket, endpoint, 8);
    options.media_delivery = Some(CultNetTransportDelivery::Unreliable);

    let transport = CultNetRudpSocketTransportConnection::new(options).expect("opens");
    let channels = &transport
        .profile
        .transports
        .first()
        .expect("a transport")
        .channels;

    let delivery_of = |id: &str| {
        channels
            .iter()
            .find(|channel| channel.channel_id == id)
            .unwrap_or_else(|| panic!("channel {id}"))
            .delivery
    };

    assert_eq!(delivery_of("schema"), CultNetTransportDelivery::Reliable);
    assert_eq!(delivery_of("realtime"), CultNetTransportDelivery::Unreliable);
}

// ---------------------------------------------------------------------------
// What the media channel puts on the wire
// ---------------------------------------------------------------------------

use std::thread::sleep;
use std::time::Duration;

use cultnet_rs::{
    CultNetRudpPacket, CultNetRudpSession, CultNetRudpSessionOptions, decode_rudp_packet,
    encode_rudp_packet,
};

/// A connected client transport and the raw socket standing in for its peer.
struct Link {
    client: CultNetRudpSocketTransportConnection,
    peer: UdpSocket,
}

impl Link {
    fn open(delivery: Option<CultNetTransportDelivery>, expire_after_ms: Option<u64>) -> Self {
        let peer = UdpSocket::bind("127.0.0.1:0").expect("binds");
        peer.set_read_timeout(Some(Duration::from_millis(500))).unwrap();
        let socket = UdpSocket::bind("127.0.0.1:0").expect("binds");
        socket.set_read_timeout(Some(Duration::from_millis(300))).unwrap();
        let mut options = CultNetRudpSocketTransportOptions::client(
            "test-media",
            socket,
            peer.local_addr().unwrap(),
            7,
        );
        options.media_delivery = delivery;
        options.media_reliable_expire_after_ms = expire_after_ms;
        options.resend_delay_ms = 10;
        let mut client = CultNetRudpSocketTransportConnection::new(options).expect("opens");

        client.connect(Vec::new()).expect("sends connect");
        let mut wire = vec![0_u8; 2_048];
        let (received, client_addr) = peer.recv_from(&mut wire).expect("the connect arrives");
        let connect = decode_rudp_packet(&wire[..received]).expect("decodes");
        let mut server = CultNetRudpSession::new(CultNetRudpSessionOptions {
            connection_id: 7,
            ..CultNetRudpSessionOptions::default()
        });
        let accept = server.accept_connect(&connect, 0, Vec::new()).expect("accepts");
        peer.send_to(&encode_rudp_packet(&accept).unwrap(), client_addr).unwrap();
        client.receive_once().expect("takes the accept");
        assert!(client.connected());
        let mut link = Self { client, peer };
        link.datagrams(Duration::from_millis(100));
        link
    }

    /// Everything the peer receives within `window`.
    fn datagrams(&mut self, window: Duration) -> Vec<CultNetRudpPacket> {
        self.peer.set_read_timeout(Some(window)).unwrap();
        let mut packets = Vec::new();
        let mut wire = vec![0_u8; 2_048];
        while let Ok((received, _)) = self.peer.recv_from(&mut wire) {
            packets.push(decode_rudp_packet(&wire[..received]).expect("decodes"));
        }
        packets
    }

    fn send_media(&mut self, window: Duration) -> Vec<CultNetRudpPacket> {
        self.client.send("media", vec![1, 2, 3]).expect("sends");
        self.datagrams(window)
    }
}

fn media_only(packets: Vec<CultNetRudpPacket>) -> Vec<CultNetRudpPacket> {
    packets.into_iter().filter(|packet| packet.channel_id == "media").collect()
}

/// The channel the profile describes as reliable is sent reliable, unordered
/// and unsequenced; asking for an unreliable lane sends it unreliable.
#[test]
fn the_media_channel_is_sent_with_the_delivery_the_profile_advertises() {
    let mut reliable = Link::open(None, None);
    let sent = media_only(reliable.send_media(Duration::from_millis(100)));
    assert_eq!(sent.len(), 1);
    assert!(sent[0].reliable, "the default media lane is reliable");
    assert!(!sent[0].ordered && !sent[0].sequenced, "media is neither ordered nor sequenced");

    let mut unreliable = Link::open(Some(CultNetTransportDelivery::Unreliable), None);
    let sent = media_only(unreliable.send_media(Duration::from_millis(100)));
    assert_eq!(sent.len(), 1);
    assert!(!sent[0].reliable, "an unreliable media lane is not retransmitted");
    assert!(!sent[0].ordered && !sent[0].sequenced);
}

/// A reliable media packet stops being retransmitted once its expiry passes,
/// and keeps being retransmitted when it has none.
#[test]
fn the_media_channels_expiry_is_applied_to_its_packets() {
    let mut expiring = Link::open(None, Some(20));
    assert_eq!(media_only(expiring.send_media(Duration::from_millis(50))).len(), 1);
    sleep(Duration::from_millis(150));
    expiring.client.poll_resends().unwrap();
    assert!(
        media_only(expiring.datagrams(Duration::from_millis(150))).is_empty(),
        "an expired media packet is dropped, not retransmitted"
    );

    let mut patient = Link::open(None, None);
    assert_eq!(media_only(patient.send_media(Duration::from_millis(50))).len(), 1);
    sleep(Duration::from_millis(150));
    patient.client.poll_resends().unwrap();
    assert!(
        !media_only(patient.datagrams(Duration::from_millis(150))).is_empty(),
        "without an expiry the unacknowledged packet is retransmitted"
    );
}
