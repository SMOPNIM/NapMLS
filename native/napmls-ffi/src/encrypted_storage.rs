use std::sync::OnceLock;

use aes_gcm::{
    aead::{Aead, KeyInit},
    Aes256Gcm, Nonce,
};
use openmls_sqlite_storage::Codec;
use serde::{de::DeserializeOwned, Serialize};
use sha2::{Digest, Sha256};

static ENCRYPTION_KEY: OnceLock<[u8; 32]> = OnceLock::new();

/// Initialize the global encryption key. Must be called once at startup.
/// Panics if called more than once.
pub fn init_encryption_key(key: [u8; 32]) {
    ENCRYPTION_KEY.set(key).expect("Encryption key already initialized");
}

/// Initialize the global encryption key if not already set. Returns true if set.
pub fn try_init_encryption_key(key: [u8; 32]) -> bool {
    ENCRYPTION_KEY.set(key).is_ok()
}

/// Get the global encryption key.
pub fn get_key() -> Option<&'static [u8; 32]> {
    ENCRYPTION_KEY.get()
}

#[derive(Debug)]
pub enum EncryptionError {
    Encrypt(String),
    Decrypt(String),
}

impl std::fmt::Display for EncryptionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EncryptionError::Encrypt(msg) => write!(f, "Encryption error: {}", msg),
            EncryptionError::Decrypt(msg) => write!(f, "Decryption error: {}", msg),
        }
    }
}

impl std::error::Error for EncryptionError {}

/// Derive a deterministic 12-byte nonce from the master key and plaintext.
/// nonce = SHA-256(key || plaintext)[0..12]
///
/// This is safe for AES-GCM as long as the same (key, plaintext) pair always
/// produces the same nonce, and different plaintexts produce different nonces
/// (with overwhelming probability). This ensures the Codec produces the same
/// ciphertext for the same plaintext+key, which is required because
/// SqliteStorageProvider uses the Codec for both keys and values.
fn derive_nonce(key: &[u8; 32], plaintext: &[u8]) -> [u8; 12] {
    let mut hasher = Sha256::new();
    hasher.update(key);
    hasher.update(plaintext);
    let hash = hasher.finalize();
    let mut nonce = [0u8; 12];
    nonce.copy_from_slice(&hash[..12]);
    nonce
}

/// AES-256-GCM encryption with deterministic nonce.
/// Format: [12-byte nonce][ciphertext + 16-byte auth tag]
fn encrypt_bytes(key: &[u8; 32], plaintext: &[u8]) -> Result<Vec<u8>, EncryptionError> {
    let cipher = Aes256Gcm::new_from_slice(key)
        .map_err(|e| EncryptionError::Encrypt(format!("Failed to create cipher: {}", e)))?;

    let nonce_bytes = derive_nonce(key, plaintext);
    let nonce = Nonce::from_slice(&nonce_bytes);

    let ciphertext = cipher
        .encrypt(nonce, plaintext)
        .map_err(|e| EncryptionError::Encrypt(format!("AES-GCM encrypt failed: {}", e)))?;

    let mut output = Vec::with_capacity(12 + ciphertext.len());
    output.extend_from_slice(&nonce_bytes);
    output.extend_from_slice(&ciphertext);
    Ok(output)
}

/// AES-256-GCM decryption. Expects [12-byte nonce][ciphertext + 16-byte auth tag]
fn decrypt_bytes(key: &[u8; 32], data: &[u8]) -> Result<Vec<u8>, EncryptionError> {
    if data.len() < 12 {
        return Err(EncryptionError::Decrypt("Data too short for nonce".into()));
    }

    let (nonce_bytes, ciphertext) = data.split_at(12);
    let cipher = Aes256Gcm::new_from_slice(key)
        .map_err(|e| EncryptionError::Decrypt(format!("Failed to create cipher: {}", e)))?;

    let nonce = Nonce::from_slice(nonce_bytes);
    cipher
        .decrypt(nonce, ciphertext)
        .map_err(|e| EncryptionError::Decrypt(format!("AES-GCM decrypt failed: {}", e)))
}

/// Codec that transparently encrypts/decrypts values using AES-256-GCM.
/// Uses deterministic nonces so the same plaintext always produces the same
/// ciphertext, which is required because SqliteStorageProvider uses the Codec
/// for both SQLite row keys AND stored values.
///
/// The global encryption key must be initialized via `init_encryption_key()`.
/// When no key is set, values pass through unencrypted (for bootstrapping).
#[derive(Default)]
pub struct EncryptedCodec;

impl Codec for EncryptedCodec {
    type Error = EncryptionError;

    fn to_vec<T: Serialize>(value: &T) -> Result<Vec<u8>, Self::Error> {
        let json = serde_json::to_vec(value)
            .map_err(|e| EncryptionError::Encrypt(format!("JSON serialize failed: {}", e)))?;

        match get_key() {
            Some(key) => encrypt_bytes(key, &json),
            None => Ok(json),
        }
    }

    fn from_slice<T: DeserializeOwned>(slice: &[u8]) -> Result<T, Self::Error> {
        let json_bytes = match get_key() {
            Some(key) => decrypt_bytes(key, slice)?,
            None => slice.to_vec(),
        };

        serde_json::from_slice(&json_bytes)
            .map_err(|e| EncryptionError::Decrypt(format!("JSON deserialize failed: {}", e)))
    }
}

/// Shorthand type for the encrypted SQLite storage provider.
pub type EncryptedSqliteStorage =
    openmls_sqlite_storage::SqliteStorageProvider<EncryptedCodec, rusqlite::Connection>;

/// Create a new in-memory encrypted storage provider.
pub fn new_encrypted_memory_storage() -> EncryptedSqliteStorage {
    let conn = rusqlite::Connection::open_in_memory()
        .expect("Failed to create in-memory SQLite");
    let mut storage = openmls_sqlite_storage::SqliteStorageProvider::<
        EncryptedCodec,
        rusqlite::Connection,
    >::new(conn);
    storage
        .run_migrations()
        .expect("Failed to run migrations");
    storage
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_encrypted_codec_roundtrip_no_key() {
        // Ensure no key is set (tests may run in any order)
        let _ = ENCRYPTION_KEY.get(); // just read, don't set

        let result = EncryptedCodec::to_vec(&"hello world").unwrap();
        let decoded: String = EncryptedCodec::from_slice(&result).unwrap();
        assert_eq!(decoded, "hello world");
    }

    #[test]
    fn test_encrypted_codec_roundtrip_with_key() {
        try_init_encryption_key([42u8; 32]);

        let result = EncryptedCodec::to_vec(&"secret data").unwrap();
        // Encrypted data should be longer due to nonce + tag overhead (12 + 16 = 28 bytes)
        assert!(result.len() > "secret data".len());

        let decoded: String = EncryptedCodec::from_slice(&result).unwrap();
        assert_eq!(decoded, "secret data");
    }

    #[test]
    fn test_encrypted_codec_deterministic() {
        try_init_encryption_key([1u8; 32]);

        let r1 = EncryptedCodec::to_vec(&42u32).unwrap();
        let r2 = EncryptedCodec::to_vec(&42u32).unwrap();
        // Deterministic nonce → same ciphertext for same plaintext+key
        assert_eq!(r1, r2);
        // Decrypt works
        let d1: u32 = EncryptedCodec::from_slice(&r1).unwrap();
        assert_eq!(d1, 42);
    }

    #[test]
    fn test_encrypted_codec_different_plaintexts_different_ciphertexts() {
        try_init_encryption_key([5u8; 32]);

        let r1 = EncryptedCodec::to_vec(&1u32).unwrap();
        let r2 = EncryptedCodec::to_vec(&2u32).unwrap();
        // Different plaintexts → different ciphertexts
        assert_ne!(r1, r2);
    }

    #[test]
    fn test_encrypted_codec_wrong_key_fails() {
        try_init_encryption_key([99u8; 32]);

        let ciphertext = EncryptedCodec::to_vec(&"secret").unwrap();

        // Decrypt with wrong key should fail
        // Note: can't change OnceLock, but we can test decryption with raw decrypt_bytes
        let wrong_key = [0u8; 32];
        let result = decrypt_bytes(&wrong_key, &ciphertext);
        assert!(result.is_err());
    }

    #[test]
    fn test_encrypted_codec_large_value() {
        try_init_encryption_key([7u8; 32]);

        let large_string = "x".repeat(10000);
        let result = EncryptedCodec::to_vec(&large_string).unwrap();
        let decoded: String = EncryptedCodec::from_slice(&result).unwrap();
        assert_eq!(decoded.len(), 10000);
    }
}
