# NapMLS

基于 MLS（消息层安全，RFC 9420）的 QQ 群加密子群聊天。

> ⚠️ **安全警告（v0.1.0-alpha）**：当前版本使用**固定测试密钥**
> （`napmls-test-password` 硬编码）加密本地数据库。数据库**无实际安全保护**，
> 请勿存储敏感信息。本版本是研究原型，不是产品。仅提供 CLI，UI 仍在开发中。

## 功能

在 QQ 群内建立端到端加密的子群。成员通过带外安全码手动验证。未受信成员在 QQ 群中只能看到 `[MLS:...]` 密文。

## 架构

```
native/napmls-ffi/   Rust: openmls 0.9 + AES-GCM 加密存储 → napmls_ffi.dll
src/NapMLS.Core/     C# 纯逻辑: MessageChunker, CommitBuffer, MessageBus, SqliteStorage + Services(MlsService, MlsTransportBridge)
src/NapMLS.NapCat/   C# WebSocket 服务端（NapCat OneBot 11 反向 WebSocket）
src/NapMLS.Cli/      C# 单文件 REPL：身份/信任成员/建群/收发（真机已验证）
src/NapMLS.UI/       C# Avalonia 12.1 + CommunityToolkit.Mvvm 8.4 (MVVM, 开发中)
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
dotnet test tests/NapMLS.IntegrationTests/   # 32 集成测试（需要 napmls_ffi.dll）
```

## CLI 快速上手（v0.1.0-alpha，Windows x64）

前置：自行安装 NapCatQQ 并登录；两个 QQ **互为好友**；同在一个 QQ 群里。

```powershell
# 1. 启动两端（端口/token/用户名/数据目录按需改）
NapMLS.Cli.exe --data-dir C:\NapMLS-A --port 8092 --token token-a --username Alice
NapMLS.Cli.exe --data-dir C:\NapMLS-B --port 8083 --token token-b --username Bob

# 2. NapCatQQ 配反向 WebSocket 指向上面的 ws://127.0.0.1:端口，token 一致；
#    看到 [NET] NapCat CONNECTED 后继续

# 3. 互换安全码（/code 显示），添加信任成员（自动交换密钥包）
/peer add <对方QQ号> NAPMLS-XXXX-XXXX-XXXX-XXXX <昵称>

# 4. 建群并发消息（<QQ群号> 用两人都在的现成群，MLS 子群寄生其上，不新建 QQ 群）
/group create <QQ群号> my-group <对方QQ号>
hello
```

## 故障排除（实测踩坑记录）

| 症状 | 原因 | 修复 |
|---|---|---|
| NapCat 显示 ECONNREFUSED / 连不上 | CLI 未启动，或端口不匹配 | 先启 CLI，确认 `[NET] listening on ws://…`，再看 NapCat 的反向 WS URL |
| `send_private_msg` 返回 `retcode=1200` | 两 QQ 号非好友，QQ 拦截陌生人私聊 | 先加好友，手动发一条普通私聊验证通道后再跑 |
| `401` / 连接被拒 | Token 不一致 | 比对 CLI 的 `--token` 和 NapCat 配置的 token，一字不差 |
| `[NET] NapCat CONNECTED` 始终不出现 | URL 或 host 不匹配 | 确认 NapCat 反向 WS 指向 CLI 的 `--host:--port`；CLI 只放行 127.0.0.1，必须同机 |
| CLI 启动即崩 `HttpListenerException` | 端口被占用（常见：QQ/NapCat 的其他服务占了同端口） | 换 CLI 端口并同步改 NapCat URL；用 `Get-NetTCPConnection -LocalPort <端口>` 查占用者 |
| 发消息 `retcode=0` 但对方收不到 | QQ 风控吞消息 | 群聊通常比陌生私聊宽松；确认两号同群，或换号重测 |
| 收到的 `[MLS:…]` 无法解密 | epoch 或 GROUP_HASH 两端不对齐 | 对照两端日志的 `hash=` / `epoch=` 是否一致 |

最小验证序列（确认环境 OK）：`/code` 显示安全码 → `/status` 显示身份 → NapCat CONNECTED →
`/peer add` 后对方收到 KeyPackage。任一步失败就停在上表里找对应行，不要往下走。

## 技术栈

- **Rust**: openmls 0.9, aes-gcm 0.10, sha2 0.10, rusqlite 0.37 (bundled)
- **C#**: .NET 10.0, Avalonia UI 12.1, CommunityToolkit.Mvvm 8.4
- **协议**: MLS (RFC 9420) via OpenMLS, OneBot 11 via NapCat WebSocket
- **存储**: SQLite（Rust 侧 MLS 状态 + C# 侧信任关系）

## 许可证

MIT
