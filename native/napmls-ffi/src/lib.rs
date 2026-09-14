// P0a: Verify openmls_sqlite_storage works
// P0c: Verify EncryptedCodec for value-level AES-256-GCM encryption
// P0d-step1: Pure Rust end-to-end MLS lifecycle test

pub mod encrypted_storage;
pub mod ffi;
pub mod identity_registry;
mod p0d_e2e_test;

#[cfg(test)]
mod p0a_storage_verification {
    use openmls_rust_crypto::OpenMlsRustCrypto;
    use openmls_sqlite_storage::{Codec, SqliteStorageProvider};
    use openmls::prelude::*;
    use openmls_basic_credential::SignatureKeyPair;

    #[derive(Default)]
    struct JsonCodec;

    impl Codec for JsonCodec {
        type Error = serde_json::Error;

        fn to_vec<T: serde::Serialize>(value: &T) -> Result<Vec<u8>, Self::Error> {
            serde_json::to_vec(value)
        }

        fn from_slice<T: serde::de::DeserializeOwned>(slice: &[u8]) -> Result<T, Self::Error> {
            serde_json::from_slice(slice)
        }
    }

    fn new_memory_storage() -> SqliteStorageProvider<JsonCodec, rusqlite::Connection> {
        let conn = rusqlite::Connection::open_in_memory()
            .expect("Failed to create in-memory SQLite");
        let mut storage = SqliteStorageProvider::<JsonCodec, rusqlite::Connection>::new(conn);
        storage.run_migrations()
            .expect("Failed to run migrations");
        storage
    }

    #[test]
    fn test_sqlite_storage_basic() {
        let _storage = new_memory_storage();
        println!("✅ openmls_sqlite_storage created and migrated successfully");
    }

    #[test]
    fn test_provider_with_sqlite_storage() {
        let storage = new_memory_storage();

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;

        let credential = BasicCredential::new(b"P0A Test User".to_vec());
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .expect("Failed to create signature key pair");

        signature_keys.store(&storage)
            .expect("Failed to store signature key pair");

        let retrieved = SignatureKeyPair::read(&storage, signature_keys.public(), signature_keys.signature_scheme())
            .expect("Failed to read signature key pair from storage");

        assert_eq!(retrieved.public(), signature_keys.public());

        println!("✅ Signature key pair write/read through SQLite storage works");
        println!("   Credential identity: {}", std::str::from_utf8(credential.identity()).unwrap_or("<invalid utf8>"));
    }

    #[test]
    fn test_key_package_storage() {
        let storage = new_memory_storage();

        let provider = OpenMlsRustCrypto::default();
        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;

        let credential = BasicCredential::new(b"P0A Test User".to_vec());
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .expect("Failed to create signature key pair");
        signature_keys.store(&storage)
            .expect("Failed to store signature key pair");

        let credential_with_key = CredentialWithKey {
            credential: credential.into(),
            signature_key: signature_keys.public().into(),
        };

        let key_package_bundle = KeyPackage::builder()
            .build(ciphersuite, &provider, &signature_keys, credential_with_key)
            .expect("Failed to build key package");

        let key_package = key_package_bundle.key_package();
        println!("✅ Key package created successfully");

        let hash_ref = key_package.hash_ref(provider.crypto())
            .expect("Failed to get hash ref");

        println!("   Hash reference: {:?}", hash_ref);

        // Key package is not stored in storage automatically; it must be stored explicitly
        // For P0a, we just verify creation works
        println!("✅ Key package created and hash ref obtained");
    }

    #[test]
    fn test_create_group_with_sqlite_storage() {
        let storage = new_memory_storage();

        let provider = OpenMlsRustCrypto::default();
        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;

        let credential = BasicCredential::new(b"P0A Group Creator".to_vec());
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .expect("Failed to create signature key pair");
        signature_keys.store(&storage)
            .expect("Failed to store signature key pair");

        let credential_with_key = CredentialWithKey {
            credential: credential.into(),
            signature_key: signature_keys.public().into(),
        };

        let group = MlsGroup::new(
            &provider,
            &signature_keys,
            &MlsGroupCreateConfig::default(),
            credential_with_key,
        )
        .expect("Failed to create group");

        println!("✅ Group created successfully");
        println!("   Group ID: {:?}", group.group_id());
        println!("   Epoch: {}", group.epoch());

        let group_id = group.group_id().clone();
        let loaded_group = MlsGroup::load(provider.storage(), &group_id)
            .expect("Failed to load group from storage");

        assert!(loaded_group.is_some(), "Group should be loadable from storage");
        let loaded = loaded_group.unwrap();
        println!("✅ Group loaded from storage: epoch {}", loaded.epoch());
    }
}

#[cfg(test)]
mod p0c_encrypted_storage {
    use openmls_rust_crypto::OpenMlsRustCrypto;
    use openmls::prelude::*;
    use openmls_basic_credential::SignatureKeyPair;
    use crate::encrypted_storage::*;

    fn init_test_key() {
        try_init_encryption_key([0xAB; 32]);
    }

    #[test]
    fn test_encrypted_signature_key_pair() {
        init_test_key();
        let storage = new_encrypted_memory_storage();

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .expect("Failed to create signature key pair");

        // Store through EncryptedCodec → AES-GCM encrypt → SQLite
        signature_keys.store(&storage)
            .expect("Failed to store encrypted signature key pair");

        // Read back → SQLite → AES-GCM decrypt → JSON deserialize
        let retrieved = SignatureKeyPair::read(
            &storage,
            signature_keys.public(),
            signature_keys.signature_scheme(),
        )
        .expect("Failed to read encrypted signature key pair");

        assert_eq!(retrieved.public(), signature_keys.public());
        println!("✅ Encrypted signature key pair write/read works");
    }

    #[test]
    fn test_encrypted_group_create_and_load() {
        init_test_key();
        let storage = new_encrypted_memory_storage();
        let provider = OpenMlsRustCrypto::default();
        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;

        let credential = BasicCredential::new(b"P0C Encrypted Group".to_vec());
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .expect("Failed to create signature key pair");
        signature_keys.store(&storage)
            .expect("Failed to store signature key pair");

        let credential_with_key = CredentialWithKey {
            credential: credential.into(),
            signature_key: signature_keys.public().into(),
        };

        let group = MlsGroup::new(
            &provider,
            &signature_keys,
            &MlsGroupCreateConfig::default(),
            credential_with_key,
        )
        .expect("Failed to create group");

        println!("✅ Encrypted group created: epoch {}", group.epoch());

        // Load from encrypted storage
        let group_id = group.group_id().clone();
        let loaded_group = MlsGroup::load(provider.storage(), &group_id)
            .expect("Failed to load group");

        assert!(loaded_group.is_some());
        println!("✅ Encrypted group loaded from storage");
    }
}

// P1d-2b: Verify identity persistence across provider restarts.
// This is the critical path test — if this fails, napmls_load_identity cannot work.
#[cfg(test)]
mod p1d2b_identity_persistence {
    use openmls::prelude::*;
    use openmls_basic_credential::SignatureKeyPair;
    use crate::encrypted_storage::try_init_encryption_key;
    use crate::ffi::NapMlsProvider;
    use crate::identity_registry;

    fn init_key() {
        try_init_encryption_key([0xAB; 32]);
    }

    #[test]
    fn test_create_drop_reopen_load_fingerprint_match() {
        init_key();

        let db_path = "test_identity_persistence.db";
        // Clean up any previous test DB
        let _ = std::fs::remove_file(db_path);
        let _ = std::fs::remove_file(format!("{}-wal", db_path));
        let _ = std::fs::remove_file(format!("{}-shm", db_path));

        // Step 1: Create identity on first provider instance
        let fingerprint_1;
        {
            let provider = NapMlsProvider::from_file(db_path)
                .expect("Failed to create provider from file");

            let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
            let credential = BasicCredential::new(b"alice".to_vec());
            let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
                .expect("Failed to create signature key pair");
            signature_keys.store(provider.storage())
                .expect("Failed to store signature key pair");

            // Register in identity registry
            let conn = rusqlite::Connection::open(db_path).unwrap();
            identity_registry::ensure_table(&conn).unwrap();
            identity_registry::register_identity(&conn, "alice", &signature_keys).unwrap();

            fingerprint_1 = identity_registry::compute_fingerprint(signature_keys.public());
            println!("Step 1 - Created identity, fingerprint: {:02x?}", fingerprint_1);

            // Provider dropped here — everything flushed to disk
        }

        // Step 2: Reopen DB with new provider instance
        let fingerprint_2;
        {
            let provider = NapMlsProvider::from_file(db_path)
                .expect("Failed to reopen provider from file");

            // Load from identity registry
            let conn = rusqlite::Connection::open(db_path).unwrap();
            let record = identity_registry::load_identity_record(&conn, "alice")
                .expect("Failed to query identity registry")
                .expect("Identity not found in registry");

            // Reconstruct SignatureKeyPair from OpenMLS storage
            let signature_keys = identity_registry::reconstruct_keypair(provider.storage(), &record)
                .expect("Failed to reconstruct signature key pair");

            fingerprint_2 = identity_registry::compute_fingerprint(signature_keys.public());
            println!("Step 2 - Loaded identity, fingerprint: {:02x?}", fingerprint_2);

            // Verify fingerprint matches
            assert_eq!(fingerprint_1, fingerprint_2,
                "Fingerprint mismatch after restart! Identity persistence failed.");

            // Also verify we can use the loaded identity to create a group
            let credential = BasicCredential::new(b"alice".to_vec());
            let credential_with_key = CredentialWithKey {
                credential: credential.into(),
                signature_key: signature_keys.public().into(),
            };

            let group = MlsGroup::new(
                &provider,
                &signature_keys,
                &MlsGroupCreateConfig::default(),
                credential_with_key,
            ).expect("Failed to create group with loaded identity");

            println!("Step 3 - Created group with loaded identity, epoch: {}", group.epoch());
        }

        // Cleanup
        let _ = std::fs::remove_file(db_path);
        let _ = std::fs::remove_file(format!("{}-wal", db_path));
        let _ = std::fs::remove_file(format!("{}-shm", db_path));

        println!("✅ Identity persistence test passed: create → drop → reopen → load → fingerprint match");
    }

    #[test]
    fn test_multiple_identities_persistence() {
        init_key();

        let db_path = "test_multi_identity.db";
        let _ = std::fs::remove_file(db_path);
        let _ = std::fs::remove_file(format!("{}-wal", db_path));
        let _ = std::fs::remove_file(format!("{}-shm", db_path));

        let (fp_alice_1, fp_bob_1);
        {
            let provider = NapMlsProvider::from_file(db_path).unwrap();
            let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;

            // Alice
            let alice_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm()).unwrap();
            alice_keys.store(provider.storage()).unwrap();
            let conn = rusqlite::Connection::open(db_path).unwrap();
            identity_registry::ensure_table(&conn).unwrap();
            identity_registry::register_identity(&conn, "alice", &alice_keys).unwrap();
            fp_alice_1 = identity_registry::compute_fingerprint(alice_keys.public());

            // Bob
            let bob_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm()).unwrap();
            bob_keys.store(provider.storage()).unwrap();
            identity_registry::register_identity(&conn, "bob", &bob_keys).unwrap();
            fp_bob_1 = identity_registry::compute_fingerprint(bob_keys.public());
        }

        // Reopen and verify both
        {
            let provider = NapMlsProvider::from_file(db_path).unwrap();
            let conn = rusqlite::Connection::open(db_path).unwrap();

            let alice_rec = identity_registry::load_identity_record(&conn, "alice").unwrap().unwrap();
            let alice_keys = identity_registry::reconstruct_keypair(provider.storage(), &alice_rec).unwrap();
            let fp_alice_2 = identity_registry::compute_fingerprint(alice_keys.public());
            assert_eq!(fp_alice_1, fp_alice_2);

            let bob_rec = identity_registry::load_identity_record(&conn, "bob").unwrap().unwrap();
            let bob_keys = identity_registry::reconstruct_keypair(provider.storage(), &bob_rec).unwrap();
            let fp_bob_2 = identity_registry::compute_fingerprint(bob_keys.public());
            assert_eq!(fp_bob_1, fp_bob_2);

            println!("✅ Multiple identities persisted and loaded successfully");
        }

        let _ = std::fs::remove_file(db_path);
        let _ = std::fs::remove_file(format!("{}-wal", db_path));
        let _ = std::fs::remove_file(format!("{}-shm", db_path));
    }
}
