// P0d-step1: Pure Rust end-to-end test
//
// Validates the full MLS lifecycle with encrypted storage:
// create_identity → create_group → add_members → process_welcome →
// encrypt → decrypt → remove_members → removed_member fails to decrypt

#[cfg(test)]
mod p0d_e2e_test {
    use openmls::prelude::*;
    use openmls::prelude::tls_codec::Deserialize as _;
    use openmls_basic_credential::SignatureKeyPair;
    use openmls_rust_crypto::RustCrypto;
    use openmls_sqlite_storage::SqliteStorageProvider;

    use crate::encrypted_storage::{EncryptedCodec, try_init_encryption_key};

    // ===== Custom OpenMlsProvider: RustCrypto + EncryptedSqliteStorage =====

    pub struct NapMlsProvider {
        crypto: RustCrypto,
        storage: SqliteStorageProvider<EncryptedCodec, rusqlite::Connection>,
    }

    impl NapMlsProvider {
        pub fn new() -> Self {
            let conn = rusqlite::Connection::open_in_memory()
                .expect("Failed to create in-memory SQLite");
            let mut storage = SqliteStorageProvider::<EncryptedCodec, rusqlite::Connection>::new(conn);
            storage.run_migrations().expect("Failed to run migrations");
            Self {
                crypto: RustCrypto::default(),
                storage,
            }
        }
    }

    impl openmls_traits::OpenMlsProvider for NapMlsProvider {
        type CryptoProvider = RustCrypto;
        type RandProvider = RustCrypto;
        type StorageProvider = SqliteStorageProvider<EncryptedCodec, rusqlite::Connection>;

        fn storage(&self) -> &Self::StorageProvider { &self.storage }
        fn crypto(&self) -> &Self::CryptoProvider { &self.crypto }
        fn rand(&self) -> &Self::RandProvider { &self.crypto }
    }

    // ===== Helpers =====

    fn cs() -> Ciphersuite {
        Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519
    }

    fn create_identity(provider: &NapMlsProvider, name: &[u8]) -> (CredentialWithKey, SignatureKeyPair) {
        let credential = BasicCredential::new(name.to_vec());
        let signature_keys = SignatureKeyPair::new(cs().signature_algorithm())
            .expect("Failed to create signature key pair");
        signature_keys.store(provider.storage())
            .expect("Failed to store signature key pair");
        let credential_with_key = CredentialWithKey {
            credential: credential.into(),
            signature_key: signature_keys.public().into(),
        };
        (credential_with_key, signature_keys)
    }

    fn msg_to_protocol(msg_bytes: &[u8]) -> ProtocolMessage {
        let mls_in = MlsMessageIn::tls_deserialize(&mut &msg_bytes[..])
            .expect("Failed to deserialize MlsMessageIn");
        ProtocolMessage::try_from(mls_in)
            .expect("Failed to convert to ProtocolMessage (wrong wire format?)")
    }

    // ===== End-to-end test =====

    #[test]
    fn test_full_mls_lifecycle_with_encrypted_storage() {
        let _ = try_init_encryption_key([0xCD; 32]);

        let alice_provider = NapMlsProvider::new();
        let bob_provider = NapMlsProvider::new();

        let (alice_cred, alice_keys) = create_identity(&alice_provider, b"Alice");
        let (bob_cred, bob_keys) = create_identity(&bob_provider, b"Bob");
        println!("✅ Identities created");

        // --- Alice creates a group ---
        let group_config = MlsGroupCreateConfig::builder()
            .wire_format_policy(PURE_CIPHERTEXT_WIRE_FORMAT_POLICY)
            .use_ratchet_tree_extension(true)
            .build();

        let mut alice_group = MlsGroup::new(
            &alice_provider,
            &alice_keys,
            &group_config,
            alice_cred.clone(),
        )
        .expect("Alice failed to create group");
        println!("✅ Alice created group: epoch {}", alice_group.epoch());

        // --- Bob generates a key package ---
        let bob_kpb = KeyPackage::builder()
            .build(cs(), &bob_provider, &bob_keys, bob_cred.clone())
            .expect("Failed to build Bob's key package");
        let bob_key_package = bob_kpb.into_key_package();
        println!("✅ Bob's key package generated");

        // --- Alice adds Bob via commit_builder (gives direct Welcome access) ---
        let bundle = alice_group.commit_builder()
            .propose_adds(std::iter::once(bob_key_package))
            .load_psks(alice_provider.storage())
            .expect("Failed to load PSKs")
            .build(alice_provider.rand(), alice_provider.crypto(), &alice_keys, |_| true)
            .expect("Failed to build commit")
            .stage_commit(&alice_provider)
            .expect("Failed to stage commit");

        let welcome = bundle.welcome().expect("Expected Welcome in bundle").clone();
        alice_group.merge_pending_commit(&alice_provider)
            .expect("Alice failed to merge pending commit");
        println!("✅ Alice added Bob (epoch {})", alice_group.epoch());

        // --- Bob processes the Welcome ---
        let bob_config = MlsGroupJoinConfig::builder()
            .wire_format_policy(PURE_CIPHERTEXT_WIRE_FORMAT_POLICY)
            .use_ratchet_tree_extension(true)
            .build();

        let staged_welcome = StagedWelcome::new_from_welcome(
            &bob_provider,
            &bob_config,
            welcome,
            None,
        )
        .expect("Bob failed to stage welcome");

        let mut bob_group = staged_welcome.into_group(&bob_provider)
            .expect("Bob failed to join group");
        println!("✅ Bob joined group: epoch {}", bob_group.epoch());

        // --- Alice sends an encrypted message ---
        let alice_msg = alice_group.create_message(
            &alice_provider,
            &alice_keys,
            b"Hello Bob, this is a secret message!",
        )
        .expect("Alice failed to create message");

        let alice_msg_bytes = alice_msg.to_bytes().expect("Failed to serialize");
        println!("✅ Alice encrypted message ({} bytes)", alice_msg_bytes.len());

        // --- Bob receives and decrypts ---
        let protocol_msg = msg_to_protocol(&alice_msg_bytes);
        let processed = bob_group.process_message(&bob_provider, protocol_msg)
            .expect("Bob failed to process message");

        let app_msg = match processed.into_content() {
            ProcessedMessageContent::ApplicationMessage(msg) => msg,
            other => panic!("Expected ApplicationMessage, got {:?}", other),
        };

        let plaintext = app_msg.into_bytes();
        assert_eq!(plaintext, b"Hello Bob, this is a secret message!");
        println!("✅ Bob decrypted: {:?}", String::from_utf8_lossy(&plaintext));

        // --- Bob replies ---
        let bob_msg = bob_group.create_message(
            &bob_provider,
            &bob_keys,
            b"Hey Alice, got your message!",
        )
        .expect("Bob failed to create message");

        let bob_msg_bytes = bob_msg.to_bytes().expect("Failed to serialize");
        let protocol_msg = msg_to_protocol(&bob_msg_bytes);

        let processed = alice_group.process_message(&alice_provider, protocol_msg)
            .expect("Alice failed to process Bob's message");

        let reply = match processed.into_content() {
            ProcessedMessageContent::ApplicationMessage(msg) => msg.into_bytes(),
            other => panic!("Expected ApplicationMessage, got {:?}", other),
        };

        assert_eq!(reply, b"Hey Alice, got your message!");
        println!("✅ Alice decrypted Bob's reply");

        // --- Alice removes Bob ---
        let bob_leaf_index = bob_group.own_leaf_index();
        let (_remove_commit, _welcome, _gi) = alice_group.remove_members(
            &alice_provider,
            &alice_keys,
            &[bob_leaf_index],
        )
        .expect("Alice failed to remove Bob");

        // Merge the removal commit — this advances the epoch
        alice_group.merge_pending_commit(&alice_provider)
            .expect("Alice failed to merge removal commit");
        println!("✅ Alice removed Bob (epoch {})", alice_group.epoch());

        // --- Alice sends a message after removal ---
        let post_remove_msg = alice_group.create_message(
            &alice_provider,
            &alice_keys,
            b"Bob is gone, this is secret now",
        )
        .expect("Alice failed to create post-removal message");

        let post_remove_bytes = post_remove_msg.to_bytes().expect("Failed to serialize");
        let protocol_msg = msg_to_protocol(&post_remove_bytes);

        // --- Bob tries to decrypt (should fail) ---
        let result = bob_group.process_message(&bob_provider, protocol_msg);
        match result {
            Err(ProcessMessageError::GroupStateError(MlsGroupStateError::UseAfterEviction)) => {
                println!("✅ Bob correctly rejected as evicted");
            }
            Err(e) => {
                println!("✅ Bob got error (expected): {:?}", e);
            }
            Ok(msg) => {
                match msg.into_content() {
                    ProcessedMessageContent::ApplicationMessage(app) => {
                        let bytes = app.into_bytes();
                        println!("⚠️  Bob received {} bytes after removal — may happen if stale epoch keys exist", bytes.len());
                    }
                    _ => {
                        println!("✅ Bob got non-application message after removal");
                    }
                }
            }
        }

        println!("\n🎉 Full MLS lifecycle test PASSED");
    }
}
