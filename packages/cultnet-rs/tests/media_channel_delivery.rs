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
