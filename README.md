# NapMLS

MLS (Messaging Layer Security, RFC 9420) encrypted subgroup chat for QQ groups.

## What it does

NapMLS adds end-to-end encrypted subgroups within QQ groups. Members are manually verified via out-of-band safety codes. Non-trusted members see only `[MLS:...]` ciphertext in the QQ group.

## Architecture

```
native/napmls-ffi/   Rust: openmls 0.9 + AES-GCM encrypted storage → napmls_ffi.dll
src/NapMLS.Core/     C# pure-logic: MessageChunker, CommitBuffer, MessageBus, SqliteStorage
src/NapMLS.NapCat/   C# WebSocket server (NapCat OneBot 11 reverse WebSocket)
src/NapMLS.UI/       C# Avalonia 12.1 + CommunityToolkit.Mvvm 8.4 (MVVM)
tests/               xunit (CoreTest, IntegrationTests, UI.Tests)
```

## How it works

1. Each user generates an MLS identity (signing key pair)
2. Safety code derived from public key: `NAPMLS-XXXX-XXXX-XXXX-XXXX`
3. Users verify each other's safety codes out-of-band (phone, in-person, etc.)
4. Trusted members exchange KeyPackages via QQ messages
5. A subgroup creator sends Welcome to selected members
6. All messages in the subgroup are MLS-encrypted
7. QQ group sees only `[MLS:MSG:...]` ciphertext; NapMLS UI shows plaintext

## Build

```bash
# Rust FFI
cd native/napmls-ffi && cargo build --release

# .NET
dotnet build NapMLS.slnx

# Tests
dotnet test tests/NapMLS.CoreTest/           # 38 unit tests
dotnet test tests/NapMLS.UI.Tests/           # 16 unit tests
dotnet test tests/NapMLS.IntegrationTests/   # 18 integration tests (requires napmls_ffi.dll)
```

## Tech stack

- **Rust**: openmls 0.9, aes-gcm 0.10, sha2 0.10, rusqlite 0.37 (bundled)
- **C#**: .NET 10.0, Avalonia UI 12.1, CommunityToolkit.Mvvm 8.4
- **Protocol**: MLS (RFC 9420) via OpenMLS, OneBot 11 via NapCat WebSocket
- **Storage**: SQLite (Rust-side MLS state + C#-side trust relationships)

## License

MIT
