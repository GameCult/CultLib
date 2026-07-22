use std::net::UdpSocket;
use std::time::Duration;

use anyhow::{Result, anyhow};
use cultnet_rs::*;

const CONNECTION_ID: u32 = 0x4355_4c54;

fn socket() -> Result<UdpSocket> {
    let socket = UdpSocket::bind("127.0.0.1:0")?;
    socket.set_read_timeout(Some(Duration::from_millis(20)))?;
    Ok(socket)
}

fn client(remote: std::net::SocketAddr) -> Result<CultNetRudpSocketTransportConnection> {
    let mut options =
        CultNetRudpSocketTransportOptions::client("provider", socket()?, remote, CONNECTION_ID);
    options.max_fragment_bytes = Some(2_048);
    CultNetRudpSocketTransportConnection::new(options)
}

fn connect(
    hub: &mut CultNetRudpServerHub,
    client: &mut CultNetRudpSocketTransportConnection,
    evidence: &[u8],
) -> Result<CultNetRudpServerSessionContext> {
    client.connect(evidence.to_vec())?;
    let mut connected = None;
    for _ in 0..20 {
        if let Some(CultNetRudpServerEvent::Connected { session }) = hub.receive_event_once()? {
            connected = Some(session);
            break;
        }
    }
    let session = connected.ok_or_else(|| anyhow!("hub did not receive Connect"))?;
    let _ = client.receive_once()?;
    assert!(client.connected());
    Ok(session)
}

fn receive_frame(hub: &mut CultNetRudpServerHub) -> Result<CultNetRudpServerEvent> {
    for _ in 0..20 {
        if let Some(event @ CultNetRudpServerEvent::Frame { .. }) = hub.receive_event_once()? {
            return Ok(event);
        }
    }
    Err(anyhow!("hub did not receive a frame"))
}

fn receive_message(client: &mut CultNetRudpSocketTransportConnection) -> Result<CultNetMessage> {
    for _ in 0..20 {
        if let Some(message) = client.receive_schema_message_once()? {
            return Ok(message);
        }
    }
    Err(anyhow!("client did not receive a schema message"))
}

#[test]
fn hub_keeps_independent_peer_sessions_and_exposes_connect_evidence() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "odin",
        server_socket,
        CONNECTION_ID,
    ))?;
    let mut first = client(server_addr)?;
    let mut second = client(server_addr)?;

    let first_session = connect(&mut hub, &mut first, b"session-token-one")?;
    let second_session = connect(&mut hub, &mut second, b"session-token-two")?;
    assert_ne!(first_session.remote_addr, second_session.remote_addr);
    assert_eq!(first_session.connect_payload, b"session-token-one");
    assert_eq!(second_session.connect_payload, b"session-token-two");
    assert_eq!(hub.sessions().len(), 2);

    let first_request = create_provider_session_request(
        "register-first",
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
        &CultMeshProviderRegistrationWire {
            provider_id: "first".to_string(),
            service_instance_id: "first-1".to_string(),
            endpoint_id: "first-public".to_string(),
            verse_id: "public".to_string(),
            requested_lease_duration_ms: 30_000,
            authority_lease_id: None,
        },
        Some("first-1".to_string()),
        Some("odin".to_string()),
    )?;
    let second_request = create_provider_session_request(
        "register-second",
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
        &CultMeshProviderRegistrationWire {
            provider_id: "second".to_string(),
            service_instance_id: "second-1".to_string(),
            endpoint_id: "second-public".to_string(),
            verse_id: "public".to_string(),
            requested_lease_duration_ms: 30_000,
            authority_lease_id: None,
        },
        Some("second-1".to_string()),
        Some("odin".to_string()),
    )?;
    first.send_schema_message(&first_request)?;
    second.send_schema_message(&second_request)?;

    for _ in 0..2 {
        let CultNetRudpServerEvent::Frame { session, frame } = receive_frame(&mut hub)? else {
            unreachable!();
        };
        assert_eq!(frame.channel_id, "schema");
        let request = decode_cultnet_message_from_slice(
            &frame.payload,
            CultNetWireContract::CultNetSchemaV0,
        )?;
        let CultNetMessage::OperationRequest { message_id, .. } = &request else {
            return Err(anyhow!("expected operation request"));
        };
        let provider_id = if message_id == "register-first" {
            "first"
        } else {
            "second"
        };
        let lease = CultMeshProviderLeaseWire {
            provider_id: provider_id.to_string(),
            service_instance_id: format!("{provider_id}-1"),
            endpoint_id: format!("{provider_id}-public"),
            verse_id: "public".to_string(),
            lease_id: format!("lease-{provider_id}"),
            valid_from_utc: "2026-07-14T12:00:00Z".to_string(),
            expires_at_utc: "2026-07-14T12:00:30Z".to_string(),
        };
        let response = create_provider_session_response(
            &request,
            CultMeshProviderOperationStatus::Ok,
            CULTMESH_PROVIDER_LEASE_SCHEMA,
            &lease,
            Some("odin".to_string()),
        )?;
        hub.send_schema_message(&session, &response)?;
    }

    let first_response = receive_message(&mut first)?;
    let second_response = receive_message(&mut second)?;
    let (_, first_lease) = decode_provider_session_response::<CultMeshProviderLeaseWire>(
        &first_response,
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_LEASE_SCHEMA,
    )?;
    let (_, second_lease) = decode_provider_session_response::<CultMeshProviderLeaseWire>(
        &second_response,
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_LEASE_SCHEMA,
    )?;
    assert_eq!(first_lease.provider_id, "first");
    assert_eq!(second_lease.provider_id, "second");
    assert_eq!(hub.session(first_session.remote_addr), Some(&first_session));
    assert_eq!(
        hub.session(second_session.remote_addr),
        Some(&second_session)
    );
    Ok(())
}

#[test]
fn hub_fences_replaced_generations_and_does_not_replace_connect_retransmits() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "odin",
        server_socket,
        CONNECTION_ID,
    ))?;
    let mut provider = client(server_addr)?;
    let original_evidence =
        encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
            client_session_id: "provider-client-1".to_string(),
            session_token: Some("shared-session-token".to_string()),
        })?;
    let replacement_evidence =
        encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
            client_session_id: "provider-client-2".to_string(),
            session_token: Some("shared-session-token".to_string()),
        })?;
    let original = connect(&mut hub, &mut provider, &original_evidence)?;

    provider.connect(original_evidence.clone())?;
    for _ in 0..3 {
        assert!(hub.receive_event_once()?.is_none());
    }
    assert_eq!(hub.session(original.remote_addr), Some(&original));

    provider.connect(replacement_evidence.clone())?;
    let mut disconnected = None;
    for _ in 0..20 {
        if let Some(event @ CultNetRudpServerEvent::Disconnected { .. }) =
            hub.receive_event_once()?
        {
            disconnected = Some(event);
            break;
        }
    }
    let disconnected =
        disconnected.ok_or_else(|| anyhow!("replacement did not disconnect old generation"))?;
    let CultNetRudpServerEvent::Disconnected { session, .. } = disconnected else {
        return Err(anyhow!("expected old generation Disconnected event"));
    };
    assert_eq!(session, original);
    let connected = hub
        .receive_event_once()?
        .ok_or_else(|| anyhow!("replacement did not connect new generation"))?;
    let CultNetRudpServerEvent::Connected {
        session: replacement,
    } = connected
    else {
        return Err(anyhow!("expected replacement Connected event"));
    };
    assert_ne!(replacement.session_generation, original.session_generation);
    let original_decoded = decode_provider_connect_evidence(&original.connect_payload)?;
    let replacement_decoded = decode_provider_connect_evidence(&replacement.connect_payload)?;
    assert_eq!(
        original_decoded.session_token,
        replacement_decoded.session_token
    );
    assert_ne!(
        original_decoded.client_session_id,
        replacement_decoded.client_session_id
    );
    assert!(
        hub.send(&original, "schema", vec![0x80])
            .unwrap_err()
            .to_string()
            .contains("no longer active")
    );
    assert_eq!(hub.session(replacement.remote_addr), Some(&replacement));
    Ok(())
}

#[test]
fn identical_connect_retransmit_reuses_pending_accept_after_loss() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "odin",
        server_socket,
        CONNECTION_ID,
    ))?;
    let client_socket = socket()?;
    let mut client_session = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let evidence = encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
        client_session_id: "provider-client-lost-accept".to_string(),
        session_token: Some("shared-session-token".to_string()),
    })?;
    let connect_packet = client_session.create_connect(0, evidence)?;
    let connect_wire = encode_rudp_packet(&connect_packet)?;

    client_socket.send_to(&connect_wire, server_addr)?;
    let CultNetRudpServerEvent::Connected { session } = hub
        .receive_event_once()?
        .ok_or_else(|| anyhow!("hub did not accept first Connect"))?
    else {
        return Err(anyhow!("hub did not emit Connected"));
    };
    let mut buffer = vec![0_u8; 65_535];
    let (first_len, _) = client_socket.recv_from(&mut buffer)?;
    let lost_accept = decode_rudp_packet(&buffer[..first_len])?;
    assert_eq!(lost_accept.packet_type, CultNetRudpPacketType::Accept);

    client_socket.send_to(&connect_wire, server_addr)?;
    assert!(hub.receive_event_once()?.is_none());
    let (resent_len, _) = client_socket.recv_from(&mut buffer)?;
    let resent_accept = decode_rudp_packet(&buffer[..resent_len])?;
    assert_eq!(resent_accept, lost_accept);
    assert_eq!(hub.session(session.remote_addr), Some(&session));

    client_session.receive(&resent_accept, 1)?;
    assert!(client_session.connected());
    Ok(())
}

#[test]
fn exact_reliable_acks_clear_more_than_one_ack_window_of_fragments() -> Result<()> {
    let mut sender = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let mut receiver = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let connect_packet = sender.create_connect(0, Vec::new())?;
    let accept = receiver.accept_connect(&connect_packet, 0, Vec::new())?;
    sender.receive(&accept, 0)?;
    receiver.receive(&sender.create_ack_for(accept.sequence), 0)?;

    let packets = sender.send_many(
        "schema",
        vec![0x5a; 40],
        CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
            sequenced: false,
            now_ms: 1,
        },
        Some(1),
    )?;
    assert_eq!(packets.len(), 40);
    let mut delivered = Vec::new();
    for packet in &packets {
        let result = receiver.receive(packet, 2)?;
        delivered.extend(result.delivered);
        sender.receive(&receiver.create_ack_for(packet.sequence), 3)?;
    }
    assert_eq!(delivered.len(), 1);
    assert_eq!(delivered[0].payload, vec![0x5a; 40]);
    assert!(sender.pending_reliable_sequences().is_empty());
    Ok(())
}

#[test]
fn session_and_hub_reject_configured_memory_bounds() -> Result<()> {
    let mut receiver = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    receiver.set_max_payload_bytes(Some(4));
    receiver.set_max_pending_fragment_sets(1)?;
    let mut sender = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let connect_packet = sender.create_connect(0, Vec::new())?;
    let accept = receiver.accept_connect(&connect_packet, 0, Vec::new())?;
    sender.receive(&accept, 0)?;

    let oversized = sender.send_many(
        "schema",
        vec![1, 2, 3, 4, 5],
        CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
            sequenced: false,
            now_ms: 1,
        },
        Some(2),
    )?;
    receiver.receive(&oversized[0], 1)?;
    receiver.receive(&oversized[1], 1)?;
    assert!(receiver.receive(&oversized[2], 1).is_err());

    let fragment_options = CultNetRudpSendOptions {
        reliable: true,
        ordered: true,
        sequenced: false,
        now_ms: 2,
    };
    let first_set = sender.send_many(
        "schema",
        vec![1, 2, 3, 4],
        fragment_options.clone(),
        Some(2),
    )?;
    let second_set = sender.send_many("schema", vec![5, 6, 7, 8], fragment_options, Some(2))?;
    receiver.receive(&first_set[0], 2)?;
    assert!(receiver.receive(&second_set[0], 2).is_err());

    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut options = CultNetRudpServerHubOptions::new("odin", server_socket, CONNECTION_ID);
    options.max_peers = 1;
    let mut hub = CultNetRudpServerHub::new(options)?;
    let mut first = client(server_addr)?;
    let mut second = client(server_addr)?;
    connect(&mut hub, &mut first, b"first")?;
    second.connect(b"second".to_vec())?;
    for _ in 0..20 {
        match hub.receive_event_once() {
            Err(error) => {
                assert!(error.to_string().contains("peer limit"));
                return Ok(());
            }
            Ok(_) => continue,
        }
    }
    Err(anyhow!("hub did not enforce peer limit"))
}

#[test]
fn replay_history_is_bounded_and_idle_hub_sessions_expire() -> Result<()> {
    let mut sender = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let mut receiver = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    });
    let connect_packet = sender.create_connect(0, Vec::new())?;
    let accept = receiver.accept_connect(&connect_packet, 0, Vec::new())?;
    sender.receive(&accept, 0)?;
    let mut oldest = None;
    for sequence in 0..4_100 {
        let packet = sender.send(
            "realtime",
            vec![(sequence % 251) as u8],
            CultNetRudpSendOptions {
                reliable: false,
                ordered: false,
                sequenced: false,
                now_ms: sequence,
            },
        )?;
        if oldest.is_none() {
            oldest = Some(packet.clone());
        }
        assert_eq!(receiver.receive(&packet, sequence)?.delivered.len(), 1);
    }
    assert!(
        receiver
            .receive(&oldest.expect("oldest packet"), 4_101)?
            .delivered
            .is_empty()
    );

    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "odin",
        server_socket,
        CONNECTION_ID,
    ))?;
    let mut provider = client(server_addr)?;
    let context = connect(&mut hub, &mut provider, b"expiring-session")?;
    std::thread::sleep(Duration::from_millis(2));
    assert_eq!(hub.remove_timed_out_sessions(0), vec![context]);
    assert!(hub.sessions().is_empty());
    Ok(())
}
