use anyhow::Result;
use cultnet_rs::*;

const REGISTRATION_GOLDEN: &str = "hqpwcm92aWRlcklkqGFldGhlcmlhsXNlcnZpY2VJbnN0YW5jZUlkq2FldGhlcmlhLTQyqmVuZHBvaW50SWSvYWV0aGVyaWEtcHVibGljp3ZlcnNlSWSmcHVibGljuHJlcXVlc3RlZExlYXNlRHVyYXRpb25Nc811MLBhdXRob3JpdHlMZWFzZUlkq2F1dGhvcml0eS03";
const CONNECT_EVIDENCE_GOLDEN: &str =
    "gq9jbGllbnRTZXNzaW9uSWSyYWV0aGVyaWEtY2xpZW50LTQyrHNlc3Npb25Ub2tlbrJvZGluLXNlc3Npb24tdG9rZW4=";
const TOKENLESS_CONNECT_EVIDENCE_GOLDEN: &str =
    "gq9jbGllbnRTZXNzaW9uSWSwYW5vbnltb3VzLWNsaWVudKxzZXNzaW9uVG9rZW7A";

fn registration() -> CultMeshProviderRegistrationWire {
    CultMeshProviderRegistrationWire {
        provider_id: "aetheria".to_string(),
        service_instance_id: "aetheria-42".to_string(),
        endpoint_id: "aetheria-public".to_string(),
        verse_id: "public".to_string(),
        requested_lease_duration_ms: 30_000,
        authority_lease_id: Some("authority-7".to_string()),
    }
}

#[test]
fn registration_matches_csharp_and_typescript_golden_payload() -> Result<()> {
    let encoded = encode_provider_session_payload(&registration())?;
    assert_eq!(encoded, REGISTRATION_GOLDEN);
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderRegistrationWire>(&encoded)?,
        registration()
    );
    Ok(())
}

#[test]
fn connect_evidence_is_a_validated_named_messagepack_map() -> Result<()> {
    let evidence = CultMeshProviderConnectEvidenceWire {
        client_session_id: "aetheria-client-42".to_string(),
        session_token: Some("odin-session-token".to_string()),
    };
    let encoded = encode_provider_connect_evidence(&evidence)?;
    assert_eq!(
        base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &encoded),
        CONNECT_EVIDENCE_GOLDEN
    );
    assert_eq!(decode_provider_connect_evidence(&encoded)?, evidence);
    let value: rmpv::Value = rmp_serde::from_slice(&encoded)?;
    let map = value.as_map().expect("Connect evidence is a named map");
    assert_eq!(
        map.iter()
            .find_map(|(key, value)| (key.as_str() == Some("clientSessionId")).then_some(value))
            .and_then(rmpv::Value::as_str),
        Some("aetheria-client-42")
    );
    assert_eq!(
        map.iter()
            .find_map(|(key, value)| (key.as_str() == Some("sessionToken")).then_some(value))
            .and_then(rmpv::Value::as_str),
        Some("odin-session-token")
    );

    assert!(
        encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
            client_session_id: String::new(),
            session_token: None,
        })
        .is_err()
    );
    assert!(
        encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
            client_session_id: "client".to_string(),
            session_token: Some(" ".to_string()),
        })
        .is_err()
    );
    assert!(decode_provider_connect_evidence(&[]).is_err());
    let tokenless = CultMeshProviderConnectEvidenceWire {
        client_session_id: "anonymous-client".to_string(),
        session_token: None,
    };
    let tokenless_encoded = encode_provider_connect_evidence(&tokenless)?;
    assert_eq!(
        base64::Engine::encode(
            &base64::engine::general_purpose::STANDARD,
            &tokenless_encoded
        ),
        TOKENLESS_CONNECT_EVIDENCE_GOLDEN
    );
    assert_eq!(
        decode_provider_connect_evidence(&tokenless_encoded)?,
        tokenless
    );
    Ok(())
}

#[test]
fn registration_round_trips_inside_canonical_operation_envelope() -> Result<()> {
    let request = create_provider_session_request(
        "register-1",
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
        &registration(),
        Some("aetheria-42".to_string()),
        Some("odin".to_string()),
    )?;
    let bytes = encode_cultnet_message_to_vec(&request, CultNetWireContract::CultNetSchemaV0)?;
    let decoded_request =
        decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0)?;
    let decoded: CultMeshProviderRegistrationWire = decode_provider_session_request(
        &decoded_request,
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
    )?;
    assert_eq!(decoded, registration());

    let lease = CultMeshProviderLeaseWire {
        provider_id: "aetheria".to_string(),
        service_instance_id: "aetheria-42".to_string(),
        endpoint_id: "aetheria-public".to_string(),
        verse_id: "public".to_string(),
        lease_id: "lease-2".to_string(),
        valid_from_utc: "2026-07-14T12:00:00Z".to_string(),
        expires_at_utc: "2026-07-14T12:00:10Z".to_string(),
    };
    let response = create_provider_session_response(
        &decoded_request,
        CultMeshProviderOperationStatus::Ok,
        CULTMESH_PROVIDER_LEASE_SCHEMA,
        &lease,
        Some("odin".to_string()),
    )?;
    let (status, decoded_lease) = decode_provider_session_response::<CultMeshProviderLeaseWire>(
        &response,
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_LEASE_SCHEMA,
    )?;
    assert_eq!(status, CultMeshProviderOperationStatus::Ok);
    assert_eq!(decoded_lease, lease);
    Ok(())
}

#[test]
fn publication_and_command_preserve_typed_messagepack_values() -> Result<()> {
    let publication = CultMeshProviderPublicationPutWire {
        lease_id: "lease-1".to_string(),
        publication_id: "surface:pilot".to_string(),
        document: CultNetRawDocumentRecord {
            schema_id: "gamecult.eve.surface.v1".to_string(),
            record_key: "eve:surface:pilot".to_string(),
            stored_at: String::new(),
            payload_encoding: CultNetRawPayloadEncoding::Messagepack,
            payload: vec![0x81, 0xa2, 0x69, 0x64, 0x01],
            source_runtime_id: Some("aetheria-42".to_string()),
            source_agent_id: None,
            source_role: None,
            tags: None,
        },
    };
    let publication_encoded = encode_provider_session_payload(&publication)?;
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderPublicationPutWire>(
            &publication_encoded
        )?,
        publication
    );

    let command = CultMeshProviderCommandWire {
        command_id: "cmd-7".to_string(),
        command_kind: "pilot.thrust".to_string(),
        provider_id: "aetheria".to_string(),
        service_instance_id: "aetheria-42".to_string(),
        payload: rmpv::Value::Map(vec![(rmpv::Value::from("axis"), rmpv::Value::F64(0.75))]),
    };
    let command_bytes = base64::Engine::decode(
        &base64::engine::general_purpose::STANDARD,
        encode_provider_session_payload(&command)?,
    )?;
    let decoded_command = decode_provider_command_document(&CultNetRawDocumentRecord {
        schema_id: CULTMESH_PROVIDER_COMMAND_SCHEMA.to_string(),
        record_key: "provider-command:aetheria:aetheria-42:cmd-7".to_string(),
        stored_at: String::new(),
        payload_encoding: CultNetRawPayloadEncoding::Messagepack,
        payload: command_bytes,
        source_runtime_id: Some("odin".to_string()),
        source_agent_id: None,
        source_role: Some("provider-session-broker".to_string()),
        tags: None,
    })?;
    assert_eq!(decoded_command, command);
    Ok(())
}

#[test]
fn lifecycle_validation_rejects_split_or_malformed_authority() {
    let mismatched = create_provider_session_request(
        "bad-schema",
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_PUBLICATION_PUT_SCHEMA,
        &registration(),
        None,
        None,
    );
    assert!(mismatched.is_err());

    let malformed_lease = CultMeshProviderLeaseWire {
        provider_id: "provider".to_string(),
        service_instance_id: "instance".to_string(),
        endpoint_id: "endpoint".to_string(),
        verse_id: "verse".to_string(),
        lease_id: "lease".to_string(),
        valid_from_utc: "2026-07-14T12:00:10Z".to_string(),
        expires_at_utc: "2026-07-14T12:00:00Z".to_string(),
    };
    assert!(encode_provider_session_payload(&malformed_lease).is_err());

    let malformed_request = CultNetMessage::OperationRequest {
        message_id: String::new(),
        service_id: CULTMESH_PROVIDER_SESSION_SERVICE_ID.to_string(),
        operation: String::new(),
        payload_schema: CULTMESH_PROVIDER_REGISTRATION_SCHEMA.to_string(),
        payload_encoding: CULTMESH_PROVIDER_SESSION_PAYLOAD_ENCODING.to_string(),
        payload: REGISTRATION_GOLDEN.to_string(),
        source_runtime_id: None,
        target_runtime_id: None,
    };
    assert!(
        create_provider_session_response(
            &malformed_request,
            CultMeshProviderOperationStatus::Invalid,
            CULTMESH_PROVIDER_MUTATION_ACCEPTANCE_SCHEMA,
            &CultMeshProviderMutationAcceptanceWire {
                accepted_at_utc: "2026-07-14T12:00:00Z".to_string(),
                lease_id: None,
                publication_id: None,
                command_id: None,
                receipt_id: None,
            },
            None,
        )
        .is_err()
    );
}

#[test]
fn remaining_lifecycle_payloads_round_trip_as_named_maps() -> Result<()> {
    let renewal = CultMeshProviderLeaseRenewalWire {
        lease_id: "lease-1".to_string(),
        requested_lease_duration_ms: 30_000,
    };
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderLeaseRenewalWire>(
            &encode_provider_session_payload(&renewal)?
        )?,
        renewal
    );

    let deletion = CultMeshProviderPublicationDeleteWire {
        lease_id: "lease-1".to_string(),
        publication_id: "surface:pilot".to_string(),
        schema_id: "gamecult.eve.surface.v1".to_string(),
        record_key: "eve:surface:pilot".to_string(),
    };
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderPublicationDeleteWire>(
            &encode_provider_session_payload(&deletion)?
        )?,
        deletion
    );

    let receipt = CultMeshProviderReceiptPutWire {
        lease_id: "lease-1".to_string(),
        receipt: CultMeshProviderCommandReceiptWire {
            receipt_id: "receipt-7".to_string(),
            command_id: "cmd-7".to_string(),
            command_kind: "pilot.thrust".to_string(),
            provider_id: "aetheria".to_string(),
            service_instance_id: "aetheria-42".to_string(),
            state: CultMeshProviderReceiptStateWire::Applied,
            completed_at_utc: "2026-07-14T12:01:02.345Z".to_string(),
            result: Some(rmpv::Value::Map(vec![(
                rmpv::Value::from("accepted"),
                rmpv::Value::Boolean(true),
            )])),
            error: None,
        },
    };
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderReceiptPutWire>(
            &encode_provider_session_payload(&receipt)?
        )?,
        receipt
    );

    let withdrawal = CultMeshProviderWithdrawalWire {
        lease_id: "lease-1".to_string(),
    };
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderWithdrawalWire>(
            &encode_provider_session_payload(&withdrawal)?
        )?,
        withdrawal
    );

    let acceptance = CultMeshProviderMutationAcceptanceWire {
        accepted_at_utc: "2026-07-14T12:01:03Z".to_string(),
        lease_id: Some("lease-1".to_string()),
        publication_id: Some("surface:pilot".to_string()),
        command_id: None,
        receipt_id: None,
    };
    assert_eq!(
        decode_provider_session_payload::<CultMeshProviderMutationAcceptanceWire>(
            &encode_provider_session_payload(&acceptance)?
        )?,
        acceptance
    );
    Ok(())
}

#[test]
fn non_ok_response_uses_correlated_empty_map_and_diagnostics() -> Result<()> {
    let request = create_provider_session_request(
        "register-denied",
        CULTMESH_PROVIDER_REGISTER_OPERATION,
        CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
        &registration(),
        Some("aetheria-42".to_string()),
        Some("odin".to_string()),
    )?;
    let response = create_provider_session_error_response(
        &request,
        CultMeshProviderOperationStatus::Denied,
        vec!["registration authority rejected the session".to_string()],
        Some("odin".to_string()),
    )?;
    let CultNetMessage::OperationResponse {
        message_id,
        operation,
        status,
        payload_schema,
        payload,
        diagnostics,
        ..
    } = response
    else {
        panic!("expected operation response");
    };
    assert_eq!(message_id, "register-denied");
    assert_eq!(operation, CULTMESH_PROVIDER_REGISTER_OPERATION);
    assert_eq!(status, "denied");
    assert_eq!(payload_schema, CULTMESH_PROVIDER_MUTATION_ACCEPTANCE_SCHEMA);
    assert_eq!(diagnostics.len(), 1);
    let bytes = base64::Engine::decode(&base64::engine::general_purpose::STANDARD, payload)?;
    assert_eq!(
        rmp_serde::from_slice::<rmpv::Value>(&bytes)?,
        rmpv::Value::Map(Vec::new())
    );
    assert!(
        create_provider_session_error_response(
            &request,
            CultMeshProviderOperationStatus::Ok,
            vec!["not an error".to_string()],
            None,
        )
        .is_err()
    );
    Ok(())
}
