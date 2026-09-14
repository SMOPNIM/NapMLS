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

/// Ensure both registry tables exist.
pub fn ensure_table(conn: &Connection) -> Result<(), rusqlite::Error> {
    ensure_identity_table(conn)?;
    ensure_group_table(conn)?;
    Ok(())
}

/// Ensure the identity registry table exists.
fn ensure_identity_table(conn: &Connection) -> Result<(), rusqlite::Error> {
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

// ===== Group Registry =====

/// Row stored in the `napmls_groups` table.
pub struct GroupRecord {
    pub group_id: Vec<u8>,
    pub name: String,
    pub qq_group_id: Option<String>,
    pub created_at: i64,
    pub last_epoch: u64,
}

/// Ensure the group registry table exists.
pub fn ensure_group_table(conn: &Connection) -> Result<(), rusqlite::Error> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS napmls_groups (
            group_id BLOB PRIMARY KEY,
            name TEXT NOT NULL,
            qq_group_id TEXT,
            created_at INTEGER NOT NULL,
            last_epoch INTEGER NOT NULL DEFAULT 0
        )"
    )
}

/// Register a group in the registry.
pub fn register_group(
    conn: &Connection,
    group_id: &[u8],
    name: &str,
    qq_group_id: Option<&str>,
    epoch: u64,
) -> Result<(), rusqlite::Error> {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64;

    conn.execute(
        "INSERT OR REPLACE INTO napmls_groups (group_id, name, qq_group_id, created_at, last_epoch)
         VALUES (?1, ?2, ?3, ?4, ?5)",
        params![group_id, name, qq_group_id, now, epoch],
    )?;
    Ok(())
}

/// Update the epoch for a group.
pub fn update_group_epoch(
    conn: &Connection,
    group_id: &[u8],
    epoch: u64,
) -> Result<(), rusqlite::Error> {
    conn.execute(
        "UPDATE napmls_groups SET last_epoch = ?1 WHERE group_id = ?2",
        params![epoch, group_id],
    )?;
    Ok(())
}

/// List all groups in the registry.
pub fn list_groups(conn: &Connection) -> Result<Vec<GroupRecord>, rusqlite::Error> {
    let mut stmt = conn.prepare(
        "SELECT group_id, name, qq_group_id, created_at, last_epoch FROM napmls_groups ORDER BY created_at DESC"
    )?;

    let rows = stmt.query_map([], |row| {
        Ok(GroupRecord {
            group_id: row.get(0)?,
            name: row.get(1)?,
            qq_group_id: row.get(2)?,
            created_at: row.get(3)?,
            last_epoch: row.get(4)?,
        })
    })?;

    rows.collect()
}

/// Load a single group record by group_id.
pub fn load_group_record(
    conn: &Connection,
    group_id: &[u8],
) -> Result<Option<GroupRecord>, rusqlite::Error> {
    let mut stmt = conn.prepare(
        "SELECT group_id, name, qq_group_id, created_at, last_epoch FROM napmls_groups WHERE group_id = ?1"
    )?;

    let mut rows = stmt.query_map(params![group_id], |row| {
        Ok(GroupRecord {
            group_id: row.get(0)?,
            name: row.get(1)?,
            qq_group_id: row.get(2)?,
            created_at: row.get(3)?,
            last_epoch: row.get(4)?,
        })
    })?;

    match rows.next() {
        Some(row) => Ok(Some(row?)),
        None => Ok(None),
    }
}

/// Delete a group from the registry.
pub fn delete_group(
    conn: &Connection,
    group_id: &[u8],
) -> Result<(), rusqlite::Error> {
    conn.execute(
        "DELETE FROM napmls_groups WHERE group_id = ?1",
        params![group_id],
    )?;
    Ok(())
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
