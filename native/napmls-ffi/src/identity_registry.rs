//! Identity registry: maps username → (public_key, signature_scheme) in SQLite.
//!
//! This is a separate table in the same database used by OpenMLS storage.
//! It allows loading an existing identity by username after process restart.

use openmls::prelude::SignatureScheme;
use openmls_basic_credential::SignatureKeyPair;
use openmls_traits::storage::CURRENT_VERSION;
use rusqlite::{params, Connection};
use sha2::{Digest, Sha256};

/// Row stored in the `napmls_identities` table.
pub struct IdentityRecord {
    pub username: String,
    pub public_key: Vec<u8>,
    pub signature_scheme: u16,
}

/// Ensure the identity registry table exists.
pub fn ensure_table(conn: &Connection) -> Result<(), rusqlite::Error> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS napmls_identities (
            username TEXT PRIMARY KEY,
            public_key BLOB NOT NULL,
            signature_scheme INTEGER NOT NULL
        )"
    )
}

/// Register an identity in the registry.
pub fn register_identity(
    conn: &Connection,
    username: &str,
    signature_keys: &SignatureKeyPair,
) -> Result<(), rusqlite::Error> {
    let pub_key = signature_keys.public().to_vec();
    let scheme = signature_keys.signature_scheme() as u16;

    conn.execute(
        "INSERT OR REPLACE INTO napmls_identities (username, public_key, signature_scheme)
         VALUES (?1, ?2, ?3)",
        params![username, pub_key, scheme],
    )?;
    Ok(())
}

/// Load an identity record by username.
pub fn load_identity_record(
    conn: &Connection,
    username: &str,
) -> Result<Option<IdentityRecord>, rusqlite::Error> {
    let mut stmt = conn.prepare(
        "SELECT public_key, signature_scheme FROM napmls_identities WHERE username = ?1"
    )?;

    let mut rows = stmt.query_map(params![username], |row| {
        Ok(IdentityRecord {
            username: username.to_string(),
            public_key: row.get(0)?,
            signature_scheme: row.get(1)?,
        })
    })?;

    match rows.next() {
        Some(row) => Ok(Some(row?)),
        None => Ok(None),
    }
}

/// Reconstruct a SignatureKeyPair from a stored record using OpenMLS storage.
/// The key pair must already exist in the OpenMLS storage (it was stored during creation).
pub fn reconstruct_keypair(
    storage: &impl openmls_traits::storage::StorageProvider<CURRENT_VERSION>,
    record: &IdentityRecord,
) -> Result<SignatureKeyPair, String> {
    let scheme = SignatureScheme::try_from(record.signature_scheme)
        .map_err(|e| format!("Invalid signature scheme: {}", e))?;
    let public_key = openmls::prelude::HpkePublicKey::from(&record.public_key[..]);

    SignatureKeyPair::read(storage, public_key.as_ref(), scheme)
        .ok_or_else(|| "Signature key pair not found in storage".to_string())
}

/// Compute fingerprint (safety code) from public key bytes.
/// Returns 8 bytes: SHA-256(public_key)[0..8].
pub fn compute_fingerprint(public_key: &[u8]) -> [u8; 8] {
    let mut hasher = Sha256::new();
    hasher.update(public_key);
    let hash = hasher.finalize();
    let mut fp = [0u8; 8];
    fp.copy_from_slice(&hash[..8]);
    fp
}

/// Format fingerprint bytes as "NAPMLS-XXXX-XXXX-XXXX-XXXX".
pub fn format_safety_code(fingerprint: &[u8; 8]) -> String {
    let hex = hex::encode_upper(fingerprint);
    format!(
        "NAPMLS-{}-{}-{}-{}",
        &hex[0..4], &hex[4..8], &hex[8..12], &hex[12..16]
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::encrypted_storage::new_encrypted_memory_storage;
    use openmls::prelude::*;
    use openmls_basic_credential::SignatureKeyPair;

    fn setup() -> (rusqlite::Connection, crate::ffi::NapMlsProvider) {
        // Shared in-memory DB for both registry and OpenMLS storage
        let conn = rusqlite::Connection::open_in_memory().unwrap();
        ensure_table(&conn).unwrap();

        // Create provider with the same connection
        // NOTE: We can't share the connection directly because SqliteStorageProvider owns it.
        // For the test, we use separate connections to the same in-memory DB.
        // In production, both will use the same file-backed DB.
        let storage = new_encrypted_memory_storage();
        let provider = crate::ffi::NapMlsProvider::from_storage(storage);

        (conn, provider)
    }

    #[test]
    fn test_registry_roundtrip() {
        let conn = rusqlite::Connection::open_in_memory().unwrap();
        ensure_table(&conn).unwrap();

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
        let keys = SignatureKeyPair::new(ciphersuite.signature_algorithm()).unwrap();

        register_identity(&conn, "alice", &keys).unwrap();

        let loaded = load_identity_record(&conn, "alice").unwrap();
        assert!(loaded.is_some());
        let rec = loaded.unwrap();
        assert_eq!(rec.username, "alice");
        assert_eq!(rec.public_key, keys.public());
        assert_eq!(rec.signature_scheme, ciphersuite.signature_algorithm() as u16);
    }

    #[test]
    fn test_registry_not_found() {
        let conn = rusqlite::Connection::open_in_memory().unwrap();
        ensure_table(&conn).unwrap();

        let loaded = load_identity_record(&conn, "nonexistent").unwrap();
        assert!(loaded.is_none());
    }

    #[test]
    fn test_registry_overwrite() {
        let conn = rusqlite::Connection::open_in_memory().unwrap();
        ensure_table(&conn).unwrap();

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
        let keys1 = SignatureKeyPair::new(ciphersuite.signature_algorithm()).unwrap();
        let keys2 = SignatureKeyPair::new(ciphersuite.signature_algorithm()).unwrap();

        register_identity(&conn, "alice", &keys1).unwrap();
        register_identity(&conn, "alice", &keys2).unwrap();

        let loaded = load_identity_record(&conn, "alice").unwrap().unwrap();
        assert_eq!(loaded.public_key, keys2.public());
    }

    #[test]
    fn test_fingerprint_format() {
        let fp = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        let code = format_safety_code(&fp);
        assert_eq!(code, "NAPMLS-DEAD-BEEF-CAFE-BABE");
    }
}
