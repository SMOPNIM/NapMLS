# NapMLS

基于 MLS（消息层安全，RFC 9420）的 QQ 群加密子群聊天。

## 功能

在 QQ 群内建立端到端加密的子群。成员通过带外安全码手动验证。未受信成员在 QQ 群中只能看到 `[MLS:...]` 密文。

## 架构

```
native/napmls-ffi/   Rust: openmls 0.9 + AES-GCM 加密存储 → napmls_ffi.dll
src/NapMLS.Core/     C# 纯逻辑: MessageChunker, CommitBuffer, MessageBus, SqliteStorage
src/NapMLS.NapCat/   C# WebSocket 服务端（NapCat OneBot 11 反向 WebSocket）
src/NapMLS.UI/       C# Avalonia 12.1 + CommunityToolkit.Mvvm 8.4 (MVVM)
tests/               xunit (CoreTest, IntegrationTests, UI.Tests)
```

## 工作原理

1. 每个用户生成 MLS 身份（签名密钥对）
2. 从公钥派生安全码：`NAPMLS-XXXX-XXXX-XXXX-XXXX`
3. 用户通过带外渠道（电话、面对面等）互相验证安全码
4. 受信成员通过 QQ 消息交换 KeyPackage
5. 子群创建者向选定成员发送 Welcome
6. 子群内所有消息均经 MLS 加密
7. QQ 群中只显示 `[MLS:MSG:...]` 密文；NapMLS UI 显示明文

## 构建

```bash
# Rust FFI
cd native/napmls-ffi && cargo build --release

# .NET
dotnet build NapMLS.slnx

# 测试
dotnet test tests/NapMLS.CoreTest/           # 38 单元测试
dotnet test tests/NapMLS.UI.Tests/           # 16 单元测试
dotnet test tests/NapMLS.IntegrationTests/   # 18 集成测试（需要 napmls_ffi.dll）
```

## 技术栈

- **Rust**: openmls 0.9, aes-gcm 0.10, sha2 0.10, rusqlite 0.37 (bundled)
- **C#**: .NET 10.0, Avalonia UI 12.1, CommunityToolkit.Mvvm 8.4
- **协议**: MLS (RFC 9420) via OpenMLS, OneBot 11 via NapCat WebSocket
- **存储**: SQLite（Rust 侧 MLS 状态 + C# 侧信任关系）

## 许可证

MIT
