//! FFI layer for NapMLS
//!
//! # Handle Ownership Model
//!
//! All handles (provider, group, identity) are heap-allocated via `Box::leak`
//! and returned as raw pointers. The caller MUST free them via the corresponding
//! `_free` function. Using a handle after freeing is undefined behavior.
//!
//! # Memory Model
//!
//! - `NapMlsBytes`: allocated by Rust, freed by caller via `napmls_free_bytes`
//! - `NapMlsError.message`: allocated by Rust (CString), freed by caller via `napmls_free_error`
//! - All FFI functions catch panics via `catch_unwind` to prevent unwinding across FFI boundary

use std::ffi::CString;
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use openmls::prelude::*;
use openmls::prelude::tls_codec::{Deserialize as _, Serialize as _};
use openmls_basic_credential::SignatureKeyPair;
use openmls_rust_crypto::RustCrypto;
use openmls_sqlite_storage::SqliteStorageProvider;

use crate::encrypted_storage::{EncryptedCodec, try_init_encryption_key};

// ===== Provider (implements OpenMlsProvider) =====

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

// ===== Handle Types =====

/// Group handle: holds the MlsGroup + join config needed for processing messages
pub struct NapMlsGroupHandle {
    pub group: MlsGroup,
    pub join_config: MlsGroupJoinConfig,
}

/// Identity handle: holds signature key pair + credential
pub struct NapMlsIdentityHandle {
    pub signature_keys: SignatureKeyPair,
    pub credential_with_key: CredentialWithKey,
}

// ===== FFI Data Types =====

/// Byte buffer returned from Rust. Must be freed via `napmls_free_bytes`.
#[repr(C)]
pub struct NapMlsBytes {
    pub ptr: *mut u8,
    pub len: usize,
}

/// Error returned from Rust. Must be freed via `napmls_free_error`.
#[repr(C)]
pub struct NapMlsError {
    pub code: i32,
    pub message: *mut c_char,
}

// ===== Error Codes =====

const NAPMLS_OK: i32 = 0;
const NAPMLS_ERR_INTERNAL: i32 = -1;
const NAPMLS_ERR_NULL_POINTER: i32 = -2;
const NAPMLS_ERR_DESERIALIZATION: i32 = -3;
const NAPMLS_ERR_MLS_PROTOCOL: i32 = -4;
const NAPMLS_ERR_STORAGE: i32 = -5;
const NAPMLS_ERR_NOT_FOUND: i32 = -6;

// ===== Helper Functions =====

/// Write an error to the output parameter. Safe to call with null ptr.
unsafe fn write_error(out_error: *mut NapMlsError, code: i32, msg: &str) {
    if out_error.is_null() {
        return;
    }
    let c_msg = CString::new(msg).unwrap_or_else(|_| CString::new("error message contains null byte").unwrap());
    (*out_error).code = code;
    (*out_error).message = c_msg.into_raw();
}

/// Write bytes to the output parameter. Safe to call with null ptr.
unsafe fn write_bytes(out: *mut NapMlsBytes, data: Vec<u8>) {
    if out.is_null() {
        return;
    }
    let len = data.len();
    let ptr = Box::into_raw(data.into_boxed_slice()) as *mut u8;
    (*out).ptr = ptr;
    (*out).len = len;
}

/// Helper: run a closure that may panic, returning Result<T, i32>
fn ffi_catch<F, T>(f: F) -> Result<T, i32>
where
    F: FnOnce() -> Result<T, i32> + std::panic::UnwindSafe,
{
    catch_unwind(f).unwrap_or(Err(NAPMLS_ERR_INTERNAL))
}

// ===== FFI Functions =====

/// Initialize the global encryption key. Call once at startup.
/// Returns NAPMLS_OK on success. Idempotent (returns OK if already initialized).
#[no_mangle]
pub extern "C" fn napmls_init(encryption_key: *const u8, key_len: usize) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if encryption_key.is_null() || key_len != 32 {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let mut key = [0u8; 32];
        unsafe { ptr::copy_nonoverlapping(encryption_key, key.as_mut_ptr(), 32); }
        let _ = try_init_encryption_key(key);
        Ok(NAPMLS_OK)
    }));
    result.unwrap_or(NAPMLS_ERR_INTERNAL)
}

/// Create a new provider. Returns null on error.
/// The caller must free the returned handle via `napmls_provider_free`.
#[no_mangle]
pub extern "C" fn napmls_provider_new(out_error: *mut NapMlsError) -> *mut NapMlsProvider {
    let result = ffi_catch(AssertUnwindSafe(|| {
        Ok(Box::into_raw(Box::new(NapMlsProvider::new())))
    }));
    match result {
        Ok(ptr) => ptr,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to create provider"); }
            ptr::null_mut()
        }
    }
}

/// Free a provider handle.
#[no_mangle]
pub extern "C" fn napmls_provider_free(provider: *mut NapMlsProvider) {
    if !provider.is_null() {
        unsafe { drop(Box::from_raw(provider)); }
    }
}

/// Create an identity (signature key pair + credential).
/// Returns NAPMLS_OK on success. The identity handle is written to `out_identity`.
#[no_mangle]
pub extern "C" fn napmls_create_identity(
    provider: *const NapMlsProvider,
    name: *const u8,
    name_len: usize,
    out_identity: *mut *mut NapMlsIdentityHandle,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || name.is_null() || out_identity.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let name_slice = unsafe { std::slice::from_raw_parts(name, name_len) };

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
        let credential = BasicCredential::new(name_slice.to_vec());
        let signature_keys = SignatureKeyPair::new(ciphersuite.signature_algorithm())
            .map_err(|_| NAPMLS_ERR_INTERNAL)?;
        signature_keys.store(provider.storage())
            .map_err(|_| NAPMLS_ERR_STORAGE)?;

        let credential_with_key = CredentialWithKey {
            credential: credential.into(),
            signature_key: signature_keys.public().into(),
        };

        let handle = Box::new(NapMlsIdentityHandle {
            signature_keys,
            credential_with_key,
        });
        unsafe { *out_identity = Box::into_raw(handle); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to create identity"); }
            code
        }
    }
}

/// Free an identity handle.
#[no_mangle]
pub extern "C" fn napmls_identity_free(identity: *mut NapMlsIdentityHandle) {
    if !identity.is_null() {
        unsafe { drop(Box::from_raw(identity)); }
    }
}

/// Create a new MLS group. Returns NAPMLS_OK on success.
/// The group handle is written to `out_group`.
#[no_mangle]
pub extern "C" fn napmls_create_group(
    provider: *const NapMlsProvider,
    identity: *const NapMlsIdentityHandle,
    out_group: *mut *mut NapMlsGroupHandle,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || identity.is_null() || out_group.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let identity = unsafe { &*identity };

        let config = MlsGroupCreateConfig::builder()
            .wire_format_policy(PURE_CIPHERTEXT_WIRE_FORMAT_POLICY)
            .use_ratchet_tree_extension(true)
            .build();

        let group = MlsGroup::new(
            provider,
            &identity.signature_keys,
            &config,
            identity.credential_with_key.clone(),
        )
        .map_err(|e| {
            eprintln!("MlsGroup::new failed: {:?}", e);
            NAPMLS_ERR_MLS_PROTOCOL
        })?;

        let join_config = MlsGroupJoinConfig::builder()
            .wire_format_policy(PURE_CIPHERTEXT_WIRE_FORMAT_POLICY)
            .use_ratchet_tree_extension(true)
            .build();

        let handle = Box::new(NapMlsGroupHandle { group, join_config });
        unsafe { *out_group = Box::into_raw(handle); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to create group"); }
            code
        }
    }
}

/// Free a group handle.
#[no_mangle]
pub extern "C" fn napmls_group_free(group: *mut NapMlsGroupHandle) {
    if !group.is_null() {
        unsafe { drop(Box::from_raw(group)); }
    }
}

/// Generate a key package for a given identity. Returns serialized KeyPackage bytes.
/// The caller can pass these to `napmls_add_members`.
#[no_mangle]
pub extern "C" fn napmls_generate_key_package(
    provider: *const NapMlsProvider,
    identity: *const NapMlsIdentityHandle,
    out_key_package: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || identity.is_null() || out_key_package.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let identity = unsafe { &*identity };

        let ciphersuite = Ciphersuite::MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519;
        let kpb = KeyPackage::builder()
            .build(
                ciphersuite,
                provider,
                &identity.signature_keys,
                identity.credential_with_key.clone(),
            )
            .map_err(|e| {
                eprintln!("KeyPackage::build failed: {:?}", e);
                NAPMLS_ERR_MLS_PROTOCOL
            })?;

        let kp = kpb.into_key_package();
        let kp_bytes = kp.tls_serialize_detached()
            .map_err(|_| NAPMLS_ERR_INTERNAL)?;

        unsafe { write_bytes(out_key_package, kp_bytes); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to generate key package"); }
            code
        }
    }
}

/// Add a member to the group. The key_package_data is a serialized KeyPackage.
/// Internally performs add_members + merge_pending_commit.
/// On success, the welcome message is written to `out_welcome`.
#[no_mangle]
pub extern "C" fn napmls_add_members(
    provider: *const NapMlsProvider,
    group: *mut NapMlsGroupHandle,
    identity: *const NapMlsIdentityHandle,
    key_package_data: *const u8,
    key_package_len: usize,
    out_welcome: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || group.is_null() || identity.is_null()
            || key_package_data.is_null() || out_welcome.is_null()
        {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let group = unsafe { &mut *group };
        let identity = unsafe { &*identity };

        let kp_bytes = unsafe { std::slice::from_raw_parts(key_package_data, key_package_len) };
        let key_package_in = KeyPackageIn::tls_deserialize(&mut &kp_bytes[..] as &mut &[u8])
            .map_err(|e| {
                eprintln!("KeyPackage deserialization failed: {:?}", e);
                NAPMLS_ERR_DESERIALIZATION
            })?;

        let key_package = key_package_in.validate(provider.crypto(), ProtocolVersion::Mls10)
            .map_err(|e| {
                eprintln!("KeyPackage validation failed: {:?}", e);
                NAPMLS_ERR_DESERIALIZATION
            })?;

        let (_commit, welcome_msg, _group_info) = group.group.add_members(
            provider,
            &identity.signature_keys,
            &[key_package],
        )
        .map_err(|e| {
            eprintln!("add_members failed: {:?}", e);
            NAPMLS_ERR_MLS_PROTOCOL
        })?;

        // Merge the pending commit so the group is ready for application messages
        group.group.merge_pending_commit(provider)
            .map_err(|e| {
                eprintln!("merge_pending_commit failed: {:?}", e);
                NAPMLS_ERR_MLS_PROTOCOL
            })?;

        // Serialize the welcome
        let welcome_bytes = welcome_msg.to_bytes()
            .map_err(|e| {
                eprintln!("Welcome serialization failed: {:?}", e);
                NAPMLS_ERR_INTERNAL
            })?;

        unsafe { write_bytes(out_welcome, welcome_bytes); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to add member"); }
            code
        }
    }
}

/// Process a Welcome message and join the group.
/// The welcome_data is a serialized Welcome (MlsMessageOut bytes).
/// On success, the group handle is written to `out_group`.
#[no_mangle]
pub extern "C" fn napmls_process_welcome(
    provider: *const NapMlsProvider,
    welcome_data: *const u8,
    welcome_len: usize,
    out_group: *mut *mut NapMlsGroupHandle,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || welcome_data.is_null() || out_group.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };

        let w_bytes = unsafe { std::slice::from_raw_parts(welcome_data, welcome_len) };

        // Deserialize as MlsMessageIn, then extract Welcome
        let mls_msg = MlsMessageIn::tls_deserialize(&mut &w_bytes[..])
            .map_err(|e| {
                eprintln!("Welcome deserialization failed: {:?}", e);
                NAPMLS_ERR_DESERIALIZATION
            })?;

        let welcome = match mls_msg.extract() {
            MlsMessageBodyIn::Welcome(w) => w,
            _ => {
                eprintln!("Data is not a Welcome message");
                return Err(NAPMLS_ERR_DESERIALIZATION);
            }
        };

        let join_config = MlsGroupJoinConfig::builder()
            .wire_format_policy(PURE_CIPHERTEXT_WIRE_FORMAT_POLICY)
            .use_ratchet_tree_extension(true)
            .build();

        let staged_welcome = StagedWelcome::new_from_welcome(
            provider,
            &join_config,
            welcome,
            None,
        )
        .map_err(|e| {
            eprintln!("StagedWelcome::new_from_welcome failed: {:?}", e);
            NAPMLS_ERR_MLS_PROTOCOL
        })?;

        let mls_group = staged_welcome.into_group(provider)
            .map_err(|e| {
                eprintln!("into_group failed: {:?}", e);
                NAPMLS_ERR_MLS_PROTOCOL
            })?;

        let handle = Box::new(NapMlsGroupHandle { group: mls_group, join_config });
        unsafe { *out_group = Box::into_raw(handle); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to process welcome"); }
            code
        }
    }
}

/// Encrypt an application message. Returns the ciphertext in `out_ciphertext`.
#[no_mangle]
pub extern "C" fn napmls_encrypt(
    provider: *const NapMlsProvider,
    group: *mut NapMlsGroupHandle,
    identity: *const NapMlsIdentityHandle,
    plaintext: *const u8,
    plaintext_len: usize,
    out_ciphertext: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || group.is_null() || identity.is_null()
            || plaintext.is_null() || out_ciphertext.is_null()
        {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let group = unsafe { &mut *group };
        let identity = unsafe { &*identity };

        let msg_bytes = unsafe { std::slice::from_raw_parts(plaintext, plaintext_len) };

        let mls_msg = group.group.create_message(
            provider,
            &identity.signature_keys,
            msg_bytes,
        )
        .map_err(|e| {
            eprintln!("create_message failed: {:?}", e);
            NAPMLS_ERR_MLS_PROTOCOL
        })?;

        let cipher_bytes = mls_msg.to_bytes()
            .map_err(|e| {
                eprintln!("Ciphertext serialization failed: {:?}", e);
                NAPMLS_ERR_INTERNAL
            })?;

        unsafe { write_bytes(out_ciphertext, cipher_bytes); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to encrypt"); }
            code
        }
    }
}

/// Decrypt an application message. Returns the plaintext in `out_plaintext`.
#[no_mangle]
pub extern "C" fn napmls_decrypt(
    provider: *const NapMlsProvider,
    group: *mut NapMlsGroupHandle,
    ciphertext: *const u8,
    ciphertext_len: usize,
    out_plaintext: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || group.is_null() || ciphertext.is_null()
            || out_plaintext.is_null()
        {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let group = unsafe { &mut *group };

        let c_bytes = unsafe { std::slice::from_raw_parts(ciphertext, ciphertext_len) };

        // Deserialize as MlsMessageIn
        let mls_msg_in = MlsMessageIn::tls_deserialize(&mut &c_bytes[..])
            .map_err(|e| {
                eprintln!("Message deserialization failed: {:?}", e);
                NAPMLS_ERR_DESERIALIZATION
            })?;

        // Convert to ProtocolMessage
        let protocol_msg = ProtocolMessage::try_from(mls_msg_in)
            .map_err(|e| {
                eprintln!("Not a protocol message: {:?}", e);
                NAPMLS_ERR_DESERIALIZATION
            })?;

        let processed = group.group.process_message(provider, protocol_msg)
            .map_err(|e| {
                eprintln!("process_message failed: {:?}", e);
                NAPMLS_ERR_MLS_PROTOCOL
            })?;

        let app_msg = match processed.into_content() {
            ProcessedMessageContent::ApplicationMessage(msg) => msg,
            other => {
                eprintln!("Expected application message, got: {:?}", other);
                return Err(NAPMLS_ERR_MLS_PROTOCOL);
            }
        };

        let plain_bytes = app_msg.into_bytes();
        unsafe { write_bytes(out_plaintext, plain_bytes); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to decrypt"); }
            code
        }
    }
}

/// Remove a member from the group. The removed_leaf_index is the LeafNodeIndex
/// of the member to remove. Internally performs remove_members + merge_pending_commit.
#[no_mangle]
pub extern "C" fn napmls_remove_members(
    provider: *const NapMlsProvider,
    group: *mut NapMlsGroupHandle,
    identity: *const NapMlsIdentityHandle,
    removed_leaf_index: u32,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if provider.is_null() || group.is_null() || identity.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let provider = unsafe { &*provider };
        let group = unsafe { &mut *group };
        let identity = unsafe { &*identity };

        let leaf_index = LeafNodeIndex::new(removed_leaf_index);

        let (_commit, _welcome, _group_info) = group.group.remove_members(
            provider,
            &identity.signature_keys,
            &[leaf_index],
        )
        .map_err(|e| {
            eprintln!("remove_members failed: {:?}", e);
            NAPMLS_ERR_MLS_PROTOCOL
        })?;

        group.group.merge_pending_commit(provider)
            .map_err(|e| {
                eprintln!("merge_pending_commit failed: {:?}", e);
                NAPMLS_ERR_MLS_PROTOCOL
            })?;

        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to remove member"); }
            code
        }
    }
}

/// Get the number of members in the group.
#[no_mangle]
pub extern "C" fn napmls_group_member_count(
    group: *const NapMlsGroupHandle,
    out_count: *mut u32,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if group.is_null() || out_count.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let group = unsafe { &*group };

        let count = group.group.members().count() as u32;
        unsafe { *out_count = count; }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to get member count"); }
            code
        }
    }
}

/// Get the LeafNodeIndex of a member by their identity credential.
/// Returns the index via out_leaf_index. Returns NAPMLS_ERR_NOT_FOUND if not found.
#[no_mangle]
pub extern "C" fn napmls_find_member_by_name(
    group: *const NapMlsGroupHandle,
    name: *const u8,
    name_len: usize,
    out_leaf_index: *mut u32,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if group.is_null() || name.is_null() || out_leaf_index.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let group = unsafe { &*group };
        let name_slice = unsafe { std::slice::from_raw_parts(name, name_len) };

        for member in group.group.members() {
            if member.credential.credential_type() == CredentialType::Basic {
                let identity_bytes = member.credential.serialized_content();
                if identity_bytes == name_slice {
                    unsafe { *out_leaf_index = member.index.u32(); }
                    return Ok(NAPMLS_OK);
                }
            }
        }
        Err(NAPMLS_ERR_NOT_FOUND)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Member not found"); }
            code
        }
    }
}

/// Get the identity fingerprint (safety code) from an identity's signing public key.
/// Returns 8 bytes: SHA-256(signing_public_key)[0..8].
/// The caller should format as: NAPMLS-XXXX-XXXX-XXXX-XXXX.
#[no_mangle]
pub extern "C" fn napmls_identity_fingerprint(
    identity: *const NapMlsIdentityHandle,
    out_fingerprint: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    use sha2::{Sha256, Digest};

    let result = ffi_catch(AssertUnwindSafe(|| {
        if identity.is_null() || out_fingerprint.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let identity = unsafe { &*identity };

        let pub_key = identity.signature_keys.public();
        let mut hasher = Sha256::new();
        hasher.update(pub_key);
        let hash = hasher.finalize();

        // First 8 bytes of SHA-256 as the fingerprint
        let fingerprint = hash[..8].to_vec();

        unsafe { write_bytes(out_fingerprint, fingerprint); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to compute fingerprint"); }
            code
        }
    }
}

/// Get the current epoch of a group. Returns u64 via out_epoch.
#[no_mangle]
pub extern "C" fn napmls_group_epoch(
    group: *const NapMlsGroupHandle,
    out_epoch: *mut u64,
    out_error: *mut NapMlsError,
) -> i32 {
    let result = ffi_catch(AssertUnwindSafe(|| {
        if group.is_null() || out_epoch.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let group = unsafe { &*group };
        unsafe { *out_epoch = group.group.epoch().as_u64(); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to get epoch"); }
            code
        }
    }
}

/// Get the list of members as a JSON byte array.
/// Format: [{"index":0,"name":"Alice","signature_key":"base64"},...]
/// The caller frees via napmls_free_bytes.
#[no_mangle]
pub extern "C" fn napmls_group_members(
    group: *const NapMlsGroupHandle,
    out_members: *mut NapMlsBytes,
    out_error: *mut NapMlsError,
) -> i32 {
    use base64::Engine;

    let result = ffi_catch(AssertUnwindSafe(|| {
        if group.is_null() || out_members.is_null() {
            return Err(NAPMLS_ERR_NULL_POINTER);
        }
        let group = unsafe { &*group };

        let mut members_json = Vec::new();
        members_json.push(b'[');

        let mut first = true;
        for member in group.group.members() {
            if !first {
                members_json.push(b',');
            }
            first = false;

            // serialized_content() returns just the identity bytes for BasicCredential
            // credential_type() returns the CredentialType enum value
            let name = if member.credential.credential_type() == CredentialType::Basic {
                let content = member.credential.serialized_content();
                std::str::from_utf8(content).unwrap_or("<invalid utf8>")
            } else {
                "<non-basic>"
            };

            // Base64 encode signature key
            let sig_key_b64 = base64::engine::general_purpose::STANDARD.encode(&member.signature_key);

            // Write JSON object
            let entry = format!(
                r#"{{"index":{},"name":"{}","signature_key":"{}"}}"#,
                member.index.u32(), name, sig_key_b64,
            );
            members_json.extend_from_slice(entry.as_bytes());
        }

        members_json.push(b']');

        unsafe { write_bytes(out_members, members_json); }
        Ok(NAPMLS_OK)
    }));

    match result {
        Ok(code) => code,
        Err(code) => {
            unsafe { write_error(out_error, code, "Failed to get group members"); }
            code
        }
    }
}

/// Free a byte buffer allocated by Rust.
/// # Safety
/// The caller MUST NOT use the bytes after calling this function.
#[no_mangle]
pub extern "C" fn napmls_free_bytes(bytes: NapMlsBytes) {
    if !bytes.ptr.is_null() && bytes.len > 0 {
        unsafe {
            drop(Box::from_raw(std::slice::from_raw_parts_mut(bytes.ptr, bytes.len)));
        }
    }
}

/// Free an error allocated by Rust.
/// # Safety
/// The caller MUST NOT use the error after calling this function.
#[no_mangle]
pub extern "C" fn napmls_free_error(error: NapMlsError) {
    if !error.message.is_null() {
        unsafe {
            drop(CString::from_raw(error.message));
        }
    }
}

// ===== Tests =====

#[cfg(test)]
mod ffi_tests {
    use super::*;
    use std::ptr;

    fn init_test() {
        let _ = try_init_encryption_key([0xAB; 32]);
    }

    #[test]
    fn test_ffi_full_lifecycle() {
        init_test();

        // 1. Create separate providers for Alice and Bob
        let alice_provider = napmls_provider_new(ptr::null_mut());
        assert!(!alice_provider.is_null());
        let bob_provider = napmls_provider_new(ptr::null_mut());
        assert!(!bob_provider.is_null());

        // 2. Create Alice and Bob identities (each on their own provider)
        let mut alice_identity: *mut NapMlsIdentityHandle = ptr::null_mut();
        let mut bob_identity: *mut NapMlsIdentityHandle = ptr::null_mut();

        let rc = napmls_create_identity(
            alice_provider, b"Alice".as_ptr(), 5,
            &mut alice_identity, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to create Alice identity");

        let rc = napmls_create_identity(
            bob_provider, b"Bob".as_ptr(), 3,
            &mut bob_identity, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to create Bob identity");

        // 3. Alice creates a group
        let mut alice_group: *mut NapMlsGroupHandle = ptr::null_mut();
        let rc = napmls_create_group(
            alice_provider, alice_identity,
            &mut alice_group, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to create group");
        assert!(!alice_group.is_null());

        // 4. Generate Bob's key package (on Bob's provider so it's stored in Bob's key store)
        let mut kp_bytes = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_generate_key_package(
            bob_provider, bob_identity,
            &mut kp_bytes, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to generate key package");
        assert!(kp_bytes.len > 0, "Key package bytes should not be empty");

        // 5. Alice adds Bob (key package bytes are opaque data, Alice doesn't need Bob's provider)
        let mut welcome_bytes = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_add_members(
            alice_provider, alice_group, alice_identity,
            kp_bytes.ptr, kp_bytes.len,
            &mut welcome_bytes, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to add Bob");
        assert!(!welcome_bytes.ptr.is_null(), "Welcome should not be null");
        napmls_free_bytes(kp_bytes);

        // 6. Bob processes Welcome
        let mut bob_group: *mut NapMlsGroupHandle = ptr::null_mut();
        let rc = napmls_process_welcome(
            bob_provider,
            welcome_bytes.ptr, welcome_bytes.len,
            &mut bob_group, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to process welcome");
        assert!(!bob_group.is_null());
        napmls_free_bytes(welcome_bytes);

        // 7. Alice encrypts a message
        let mut cipher_bytes = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let plaintext = b"Hello Bob from Alice!";
        let rc = napmls_encrypt(
            alice_provider, alice_group, alice_identity,
            plaintext.as_ptr(), plaintext.len(),
            &mut cipher_bytes, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to encrypt");
        assert!(cipher_bytes.len > 0, "Ciphertext should not be empty");

        // 8. Bob decrypts
        let mut plain_bytes = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_decrypt(
            bob_provider, bob_group,
            cipher_bytes.ptr, cipher_bytes.len,
            &mut plain_bytes, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to decrypt");

        let decrypted = unsafe { std::slice::from_raw_parts(plain_bytes.ptr, plain_bytes.len) };
        assert_eq!(decrypted, b"Hello Bob from Alice!");

        // 9. Bob replies
        napmls_free_bytes(cipher_bytes);
        napmls_free_bytes(plain_bytes);

        let mut reply_cipher = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let reply_plaintext = b"Hi Alice, Bob here!";
        let rc = napmls_encrypt(
            bob_provider, bob_group, bob_identity,
            reply_plaintext.as_ptr(), reply_plaintext.len(),
            &mut reply_cipher, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Bob failed to encrypt reply");

        let mut reply_plain = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_decrypt(
            alice_provider, alice_group,
            reply_cipher.ptr, reply_cipher.len,
            &mut reply_plain, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Alice failed to decrypt reply");

        let decrypted_reply = unsafe { std::slice::from_raw_parts(reply_plain.ptr, reply_plain.len) };
        assert_eq!(decrypted_reply, b"Hi Alice, Bob here!");

        // 10. Alice removes Bob
        napmls_free_bytes(reply_cipher);
        napmls_free_bytes(reply_plain);

        let rc = napmls_remove_members(
            alice_provider, alice_group, alice_identity,
            1, // Bob is leaf index 1 (Alice is 0)
            ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Failed to remove Bob");

        // 11. Bob should fail to decrypt after removal
        let mut late_msg = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let post_remove = b"Bob can't read this";
        let rc = napmls_encrypt(
            alice_provider, alice_group, alice_identity,
            post_remove.as_ptr(), post_remove.len(),
            &mut late_msg, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK, "Alice should still be able to send");

        let mut late_plain = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_decrypt(
            bob_provider, bob_group,
            late_msg.ptr, late_msg.len,
            &mut late_plain, ptr::null_mut(),
        );
        assert_ne!(rc, NAPMLS_OK, "Bob should fail to decrypt after removal");

        // 12. Cleanup
        napmls_free_bytes(late_msg);
        napmls_group_free(alice_group);
        napmls_group_free(bob_group);
        napmls_identity_free(alice_identity);
        napmls_identity_free(bob_identity);
        napmls_provider_free(alice_provider);
        napmls_provider_free(bob_provider);

        println!("✅ FFI full lifecycle test passed!");
        println!("   create → add → welcome → encrypt → decrypt → reply → remove → evicted");
    }

    #[test]
    fn test_ffi_error_null_pointer() {
        let mut err = NapMlsError { code: 0, message: ptr::null_mut() };
        let rc = napmls_create_identity(
            ptr::null(), b"test".as_ptr(), 4,
            ptr::null_mut(), &mut err,
        );
        assert_eq!(rc, NAPMLS_ERR_NULL_POINTER);
    }

    #[test]
    fn test_ffi_error_free_null() {
        // Freeing null handles should be safe (no-op)
        napmls_provider_free(ptr::null_mut());
        napmls_identity_free(ptr::null_mut());
        napmls_group_free(ptr::null_mut());
        napmls_free_bytes(NapMlsBytes { ptr: ptr::null_mut(), len: 0 });
        napmls_free_error(NapMlsError { code: 0, message: ptr::null_mut() });
    }

    #[test]
    fn test_ffi_identity_fingerprint() {
        init_test();

        let provider = napmls_provider_new(ptr::null_mut());
        let mut identity: *mut NapMlsIdentityHandle = ptr::null_mut();

        let rc = napmls_create_identity(
            provider, b"Alice".as_ptr(), 5,
            &mut identity, ptr::null_mut(),
        );
        assert_eq!(rc, NAPMLS_OK);

        let mut fp = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_identity_fingerprint(identity, &mut fp, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);
        assert_eq!(fp.len, 8, "Fingerprint should be 8 bytes");

        let fp_data = unsafe { std::slice::from_raw_parts(fp.ptr, fp.len) };
        println!("✅ Fingerprint: {:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}",
            fp_data[0], fp_data[1], fp_data[2], fp_data[3],
            fp_data[4], fp_data[5], fp_data[6], fp_data[7]);

        // Same identity should produce same fingerprint
        let mut fp2 = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_identity_fingerprint(identity, &mut fp2, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);
        let fp2_data = unsafe { std::slice::from_raw_parts(fp2.ptr, fp2.len) };
        assert_eq!(fp_data, fp2_data, "Same identity should produce same fingerprint");

        napmls_free_bytes(fp);
        napmls_free_bytes(fp2);
        napmls_identity_free(identity);
        napmls_provider_free(provider);
    }

    #[test]
    fn test_ffi_group_epoch() {
        init_test();

        let provider = napmls_provider_new(ptr::null_mut());
        let mut identity: *mut NapMlsIdentityHandle = ptr::null_mut();
        napmls_create_identity(provider, b"Alice".as_ptr(), 5, &mut identity, ptr::null_mut());

        let mut group: *mut NapMlsGroupHandle = ptr::null_mut();
        let rc = napmls_create_group(provider, identity, &mut group, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);

        let mut epoch: u64 = 0;
        let rc = napmls_group_epoch(group, &mut epoch, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);
        assert_eq!(epoch, 0, "New group should start at epoch 0");
        println!("✅ Group epoch: {}", epoch);

        napmls_group_free(group);
        napmls_identity_free(identity);
        napmls_provider_free(provider);
    }

    #[test]
    fn test_ffi_group_members() {
        init_test();

        let alice_provider = napmls_provider_new(ptr::null_mut());
        let bob_provider = napmls_provider_new(ptr::null_mut());

        let mut alice_identity: *mut NapMlsIdentityHandle = ptr::null_mut();
        let mut bob_identity: *mut NapMlsIdentityHandle = ptr::null_mut();
        napmls_create_identity(alice_provider, b"Alice".as_ptr(), 5, &mut alice_identity, ptr::null_mut());
        napmls_create_identity(bob_provider, b"Bob".as_ptr(), 3, &mut bob_identity, ptr::null_mut());

        let mut alice_group: *mut NapMlsGroupHandle = ptr::null_mut();
        napmls_create_group(alice_provider, alice_identity, &mut alice_group, ptr::null_mut());

        // Initially 1 member (Alice)
        let mut members = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_group_members(alice_group, &mut members, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);
        let members_json = unsafe {
            let data = std::slice::from_raw_parts(members.ptr, members.len);
            std::str::from_utf8(data).expect("Invalid UTF-8 in members JSON")
        };
        println!("✅ Members before add: {}", members_json);
        assert!(members_json.contains("Alice"));
        assert!(!members_json.contains("Bob"));
        napmls_free_bytes(members);

        // Add Bob
        let mut kp = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        napmls_generate_key_package(bob_provider, bob_identity, &mut kp, ptr::null_mut());
        let mut welcome = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        napmls_add_members(alice_provider, alice_group, alice_identity, kp.ptr, kp.len, &mut welcome, ptr::null_mut());
        napmls_free_bytes(kp);
        napmls_free_bytes(welcome);

        // Now 2 members
        let mut members = NapMlsBytes { ptr: ptr::null_mut(), len: 0 };
        let rc = napmls_group_members(alice_group, &mut members, ptr::null_mut());
        assert_eq!(rc, NAPMLS_OK);
        let members_json = unsafe {
            let data = std::slice::from_raw_parts(members.ptr, members.len);
            std::str::from_utf8(data).expect("Invalid UTF-8 in members JSON")
        };
        println!("✅ Members after add: {}", members_json);
        assert!(members_json.contains("Alice"));
        assert!(members_json.contains("Bob"));
        napmls_free_bytes(members);

        napmls_group_free(alice_group);
        napmls_identity_free(alice_identity);
        napmls_identity_free(bob_identity);
        napmls_provider_free(alice_provider);
        napmls_provider_free(bob_provider);
    }
}
