# NapMLS — Agent Instructions

## What this is

Group encrypted chat: OpenMLS (Rust FFI) + NapCatQQ (WebSocket) + C# & Avalonia UI.

## Architecture

```
native/napmls-ffi/   Rust: openmls 0.9 + AES-GCM encrypted storage → builds napmls_ffi.dll
src/NapMLS.Core/     C# pure-logic: MessageChunker, CommitBuffer, MessageBus, IMessageTransport
src/NapMLS.NapCat/   C# WebSocket server (NapCat OneBot 11)
src/NapMLS.UI/       C# Avalonia 12.1 + CommunityToolkit.Mvvm 8.4 (MVVM)
tests/               xunit (CoreTest, IntegrationTests, UI.Tests) + FFITest (Exe, not test project)
```

Target: .NET 10.0, Rust edition 2021.

## Build commands

```bash
# Rust FFI — MUST build before .NET integration tests work
cd native/napmls-ffi && cargo build --release
# Output: native/napmls-ffi/target/release/napmls_ffi.dll

# .NET build
dotnet build NapMLS.slnx

# Run tests (each project independently)
dotnet test tests/NapMLS.CoreTest/        # 25 unit tests — no DLL dependency
dotnet test tests/NapMLS.UI.Tests/        # 16 unit tests — no DLL dependency
dotnet test tests/NapMLS.IntegrationTests/ # requires napmls_ffi.dll (built by cargo)
```

## Key gotchas

- **FFI DLL must exist before integration tests**: `NapMLS.IntegrationTests.csproj` copies `native/napmls-ffi/target/release/napmls_ffi.dll` via `<Content>`. If the DLL is missing, the test project won't build.
- **FFITest is NOT a test project**: It's an `Exe` (console app), not xunit. Run it manually if needed.
- **`napmls_init` is OnceLock**: the 32-byte encryption key can only be set once per process. Tests call it with `[0xAB; 32]`. Production needs Argon2id derivation (not yet wired).
- **Avalonia 12.1 clipboard**: `IClipboard.SetTextAsync` doesn't exist directly. Use `SetDataAsync(DataTransfer)` with `DataTransferItem.CreateText()` instead.
- **DockPanel in Avalonia 12**: use `DockPanel.Dock="Top"` as an attached property on child elements, not `Dock="Dock.Top"` on the element itself.
- **CommunityToolkit.Mvvm source generators**: `[ObservableProperty]` and `[RelayCommand]` require the containing class to be `partial`.
- **xunit versions differ**: CoreTest uses 2.9.0, UI.Tests uses 2.9.3. Don't unify unless you verify availability.
- **FFI multi-KP add_members**: wire format is `[count:4][len1:4][kp1..][len2:4][kp2..]...` (4-byte LE length prefix per KeyPackage). Single KP works (count=1).
- **OwnPrivateMessage**: MLS returns null on self-decrypt. Senders display plaintext locally without decrypt path.
- **Message chunking format**: `[MLS:TYPE:GROUP_HASH:EPOCH:SEQN/TOTAL]base64` where GROUP_HASH = `SHA-256(group_id)[0..8]` hex (16 chars).

## File conventions

- ViewModels use `[ObservableProperty]` (not plain properties) for data binding
- FFI wrapper in integration tests: `tests/NapMLS.IntegrationTests/MlsClient.cs` (P/Invoke declarations)
- Identity mapping: `tests/NapMLS.IntegrationTests/IdentityMap.cs` (QQ ↔ MLS)
- Config persistence: `src/NapMLS.UI/Services/AppConfigService.cs` → `config.json` in app dir (no secrets)
- Safety code format: `NAPMLS-XXXX-XXXX-XXXX-XXXX` (8 bytes hex from SHA-256(signing_key)[0..8])

## Design constraints (non-negotiable)

- P1: MLS encrypted subgroups ≠ QQ groups; non-trusted members see only `[MLS:...]` ciphertext
- P2: Out-of-band identity verification via safety codes
- P3: All MLS secrets stay in Rust side; C# only holds opaque handles
- P4: External Commit disabled; only Welcome flow for adding members
- P5: Periodic Update (24h default) + immediate key rotation on member removal
- P6: NapCat ToS violation risk warning required at startup
