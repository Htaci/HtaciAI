# 服务器管理插件 `htaci.servers` 详细设计

> 状态：**v0.1.0 已实现并部署**（工程见独立仓库 `HtaciAI.Plugins` 的 `HtaciAI.Servers`，自检 `HtaciAI.Servers.Tests` 60 项全通过）。
> 决策记录：本轮范围 = SSH 密钥认证 + 密码认证；跳板机、RDP、Docker 出范围。
> 决策记录 2（本轮修订）：凭据不追求「系统级加密」，改为存插件自己的 SQLite；
> 加密作为**可选开关**，由用户设「加密密码」现场派生密钥，不开就是明文。理由见 §3.2。
> 决策记录 3：SFTP 进入范围（`srv_upload` / `srv_download`），本地路径锁在根目录内，见 §7.5。
> 路径均相对仓库根 `D:\Project\HtaciAI`；插件源码在独立仓库 `D:\Project\HtaciAI.Plugins`。

---

## 1. 一句话

在宿主里托管「受管服务器清单 + 凭据」，把它们暴露成一组工具给模型；
模型只拿到 `target_id`，**永远拿不到私钥、密码、passphrase**，也拿不到凭据文件的位置。

## 2. 范围

**做**

| 项 | 说明 |
| --- | --- |
| 目标管理 UI | 新增标签页「服务器」：列表 / 新建 / 编辑 / 删除 / 分组 / 连接测试；未保存内容在离开前用「取消 / 不保存 / 保存」三选一确认 |
| 凭据托管 | SSH 私钥（含 passphrase）、登录密码；落插件自己的 SQLite；可选「加密存储」（加密密码 → Argon2id 派生 32 B 密钥 → AES-256-GCM），不开启则明文。UI 只显示指纹 |
| 主机密钥校验 | known_hosts 式 TOFU，只在 UI 里确认指纹；工具层遇到未知主机会拒绝连接 |
| 工具 | `srv_list_targets` / `srv_exec` / `srv_shell_open` / `srv_shell_send` / `srv_shell_read` / `srv_shell_close` |
| 审计 | 每次 `exec` / `shell_*` 落一条 JSONL，UI 可查看、可清空 |

**不做（明确排除）**

- 任意协议扩展（RDP / VNC / 数据库 / Docker API）。
- 跳板机、端口转发。
- 命令白名单/黑名单过滤。**不装安全策略**：过滤正则只会给人虚假的安全感，真正的闸门是危险等级 + 审批。
- 凭据导出、跨机器同步。
- 让模型「自己用 ssh 命令」。那会让密钥进入 `~/.ssh` 与命令行，本设计的全部价值就没了。

## 3. 安全边界（先看这节，其余设计都由它决定）

### 3.1 这套设计能防什么

| 威胁 | 是否覆盖 | 手段 |
| --- | --- | --- |
| 模型在**参数**里塞私钥、或在上下文里复述私钥 | ✅ | 工具 schema 里**结构上不存在**凭据字段；schema 是模型唯一的入参契约 |
| 模型通过工具**返回结果**读到私钥 | ✅ | 工具结果只回指纹与 `credentialId`，永不回凭据内容 |
| 插件把密钥写进 system 提示词 | ✅ | 不声明 `prompt.contribute` 能力，插件根本没有 `context.Prompts` |
| 凭据库被拷走（备份、网盘、U 盘、误发他人） | ⚠️ **取决于开关** | 开加密时：Argon2id 现场派生密钥，库里既没有密码也没有哈希也没有 key，`servers.db` 单独拷走解不开；不开加密时：**明文可读**，这是用户显式选择的结果（见 3.2） |
| 连接到假冒主机（MITM） | ✅ | 强制 host key 校验，未信任一律拒绝 |
| 模型读取凭据库内容 | ⚠️ **部分** | 见 3.2 |

### 3.2 为什么不做「系统级加密」

前一版设计用的是 DPAPI + 独立目录 + 保险库锁定，本轮**主动降级**，理由：

1. **DPAPI 挡不住本设计的主要威胁**。宿主没有沙箱，插件与 `bash` 同用户同进程族，
   模型真去读插件数据目录再调 `ProtectedData.Unprotect`，DPAPI 是透明的。
   它只保护「文件离开这台机器之后」，而模型本来就在这台机器上。

   > 现行方案用的是「Argon2id + 密码」，模型即使拿到 `servers.db` 也解不开 ——
   > 这一点比 DPAPI 更强，因为解密需要用户输入的东西，而不是「本机身份」。
   > 但见 §3.2.1：本机同用户进程始终是模型可以打的主意，别指望加密兜住它。
2. **成本与收益不对等**。为了那层挡不住模型的壳，要多付：Windows 绑定、ACL 维护、
   独立数据目录、迁移兼容、锁定态解锁流程、一堆「为什么我的密钥在奇怪的地方」的困惑。
3. **密钥文件本来就是明文的**。用户的 `~/.ssh/id_rsa` 一直是明文；插件把它复制进 SQLite 时不加密，
   只是「和用户当前的现状一致」。连自己的私钥都不做任何保护的机器上，
   插件替它加一层密码学外壳，防的是「库被单独拷走」，而不是「密钥泄露」。
4. **真正有价值的那层是「密钥不进模型上下文」**，这一层与加密开关无关，永远成立：
   工具 schema 里没有凭据字段、工具结果不回凭据、不声明 `prompt.contribute`。

所以结论：**加密是「我被拷走了别太难看」的可选保险，不是安全模型的地基。**

### 3.2 附：这套设计仍然防不住什么（必须写进产品说明）

- **不开加密 = 明文**。任何能读到 `plugin-data\htaci.servers\servers.db` 的进程/人能直接拿到私钥和密码。
  产品文案必须直说这一点，不能给「已保存，安全」这种含糊印象。
- **同用户进程可直接读取**。无论开不开加密：开了加密且已解锁时，密钥与密码原文在内存里，进程内能读到；
  未解锁时 `srv_exec` 会失败，但库仍是可读文件（只是读到的都是密文）。
- **加密强度等于用户密码强度**。`password123` 这样的密码，Argon2id 也救不回来。
  设置时给强度提示，是这一层唯一能做的额外防护。
- **工具结果里不出现任何绝对路径**（仍然保留）。UI 里显示库里路径放在「高级」折叠区，
  目的不是加密，而是别把线索摆在模型眼前。
- **不承诺「防模型」**。唯一有效的手段是「凭据不以任何形式进入模型可见的通道」，
  其余（加密、目录位置、锁定）都是缓解，写清楚比夸大更有用。

### 3.3 已知残留风险

- 用户若把私钥同时放在 `~/.ssh/id_rsa`，模型用 `bash` 就能读 —— 这是用户的既有暴露面，插件无法也
  不应「帮用户清理」。UI 在保存私钥时给一句提示即可。
- 密码认证下，密码解密后会在 `string` 里短暂存在（含 SSH.NET 内部）。无法用 `SecureString` 完全规避，
  只能在用完后尽快丢弃引用。加密密码同样只在内存里，用户自己输入的 `string` 用完即弃。
- 审计日志会记录命令前 200 字符（含可能的机密参数）。UI 清除按钮用「点一次变红、再点一次」两段式。
- 加密开启后**加密密码无法找回**。忘记就只能「重置加密」——即丢弃全部已存凭据，重新录入。
  UI 必须在设置密码时明示，并要求输两遍。

## 4. 插件身份

| 项 | 值 |
| --- | --- |
| 插件 id / 目录 | `htaci.servers` |
| name / version | 服务器 / `0.1.0` |
| entry / entryType | `HtaciAI.Servers.dll` / `HtaciAI.Servers.ServersPlugin` |
| capabilities | `ui.tab`、`ui.slot`、`ui.settings`、`tools.provide` |
| platforms | `windows`、`linux`、`macos`（不依赖 DPAPI / ACL 后三平台同一套实现；非 Windows 未实测） |
| requires / conflicts | 无 |

工程位置：`HtaciAI.Plugins` 仓库的 `HtaciAI.Servers/HtaciAI.Servers.csproj`，
契约引用沿用内置浏览器插件的写法（`ProjectReference` + `Private=false` + `ExcludeAssets=runtime`），
**绝不**把 `HtaciAI*.dll`、`Avalonia*.dll` 复制进插件目录。

## 5. 数据模型与存储

### 5.1 位置

一个 SQLite 库，放在插件自己的数据目录里（`context.GetDataDirectory()`）：

```
<数据根>\plugin-data\htaci.servers\      ← 实际由 GetDataDirectory() 决定
├─ servers.db        # 主库：目标 / 凭据 / 主机密钥 / 设置 / 审计
├─ servers.db-wal
└─ servers.db-shm
```

- 目标、凭据、主机密钥、审计都进同一个库，事务天然保证一致（改目标 + 换凭据不会各自写一半）。
- **实际路径**（v0.1.0 部署后）：`D:\App\HtaciApp\data\plugins\htaci.servers\data\servers.db`。
  用的是 `context.GetDataDirectory()`（插件目录下的 `data`），不是旧的 `plugin-data\<id>`。
- **不搞多文件、不搞独立保险库目录**：那套「把秘密藏到别处」的成本见 §3.2，
  既然接受「明文也行」，就没必要再为目录位置付复杂度。
- 访问用 `Microsoft.Data.Sqlite`（自带 `SQLitePCLRaw` + 原生 `e_sqlite3.dll`，随插件目录发布；
  原生 dll 不违反「插件目录不能有 `Avalonia*.dll` / `HtaciAI*.dll`」的限制）。
  注意原生 dll 不能在插件运行时被覆盖 —— 和浏览器插件的 `WebView2Loader.dll` 是同一类约束。
- 连接策略：**单连接 + 一个 `SemaphoreSlim(1,1)`** 串行化所有访问。
  插件的数据量（几十条目标）根本不值得连接池；串行化把「UI 线程写一半、工具线程也在写」的竞态
  直接消掉。异步方法里绝不同步 `.Result`。
- 不做 `Storage.Load<T>/Save<T>`（泛型重载会缓存类型导致插件卸不掉，契约明文警告）。

### 5.2 表结构

```sql
CREATE TABLE targets (
  id            TEXT PRIMARY KEY,           -- 'srv_7f3a1c'
  name          TEXT NOT NULL,              -- 'prod-web-01'
  grp           TEXT,                       -- 分组，'生产'
  color         TEXT,                       -- '#EF4444'
  host          TEXT NOT NULL,              -- '10.0.0.21'
  port          INTEGER NOT NULL DEFAULT 22,
  username      TEXT NOT NULL,              -- 'deploy'
  auth_kind     TEXT NOT NULL,              -- 'key' | 'password'
  credential_id TEXT,                       -- -> credentials.id
  default_cwd   TEXT,                       -- '/srv/app'
  agent_access  TEXT NOT NULL DEFAULT 'exec',   -- 'hidden' | 'list' | 'exec'
  approval      TEXT NOT NULL DEFAULT 'inherit',-- 'inherit' | 'always'
  note          TEXT,
  created_at    TEXT NOT NULL,
  last_used_at  TEXT
);

CREATE TABLE host_keys (
  host        TEXT NOT NULL,
  port        INTEGER NOT NULL,
  algorithm   TEXT NOT NULL,                -- 'ssh-ed25519'
  fingerprint TEXT NOT NULL,                -- 'SHA256:3Yk...9Qf'
  trusted_at  TEXT NOT NULL,
  PRIMARY KEY (host, port)
);

CREATE TABLE settings (                     -- 单行 k/v：加密开关、schema 版本等
  key   TEXT PRIMARY KEY,                   -- 'schema.version' / 'enc.enabled' / 'enc.mode'
  value TEXT NOT NULL
);

CREATE TABLE audit (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  at         TEXT NOT NULL,
  session_id TEXT,
  target_id  TEXT,
  tool       TEXT NOT NULL,                 -- 'srv_exec' / 'srv_shell_send'
  command    TEXT,                          -- 前 200 字符
  exit_code  INTEGER,
  ok         INTEGER NOT NULL,
  duration_ms INTEGER
);
CREATE INDEX ix_audit_at ON audit(at DESC);
```

`targets` 的三个关键字段（语义不变，列名为 `snake_case`）：

- `agent_access`：`hidden` / `list` / `exec`。
  `hidden` 的目标对模型**完全不存在**（`srv_list_targets` 不返回，猜 id 也拒绝）；
  `list` 只能出现在列表里供模型「知道有这么台机器」，任何执行工具都拒绝；
  `exec` 才可执行。默认 `exec`，改 `hidden` 是一键操作。
- `approval`：`inherit`（默认，跟宿主权限档位）/ `always`（每次调用都审批）。
  生产环境的目标建议 `always` —— 审批框里会带目标名与环境色，用户不会误批。
- `default_cwd`：`srv_exec` 未传 `cwd` 时 `cd` 到这里，省掉模型每次猜路径。

### 5.3 Credential 与加密开关

加密方案定稿：**Argon2id 从「加密密码」派生出 32 字节密钥，再用 AES-256-GCM 加密**。
派生结果**永不落库**，每次使用都由「密码 + 该行 salt」现场算出来。
库里没有任何形式的密码哈希，也没有密钥本身。

```sql
CREATE TABLE credentials (
  id           TEXT PRIMARY KEY,       -- 'cred_9a2b'
  kind         TEXT NOT NULL,          -- 'privateKey' | 'password'
  label        TEXT,                   -- 'deploy@prod'
  key_type     TEXT,                   -- 'ssh-ed25519'，仅私钥
  fingerprint  TEXT,                   -- SHA256 of public key blob，仅私钥
  has_secret   INTEGER NOT NULL,       -- passphrase 是否存在
  payload      BLOB NOT NULL,          -- 明文 或 密文
  enc          INTEGER NOT NULL,       -- 0 = 明文，1 = Argon2id + AES-256-GCM
  kdf          TEXT,                   -- 'argon2id'（enc=1 时必填）
  kdf_params   TEXT,                   -- JSON：{"t":3,"m":65536,"p":4}，每行自带，便于逐行升级
  salt         BLOB,                   -- 16 字节，enc=1 时有值（每行独立随机）
  iv           BLOB,                   -- 12 字节，enc=1 时有值
  tag          BLOB,                   -- 16 字节，enc=1 时有值
  hint         TEXT,                   -- 密码认证时存「末 2 位」之类的辨认提示
  created_at   TEXT NOT NULL
);
```

> 私钥口令（passphrase）不再单开一列，而是作为同一份 `payload` 的一部分：
> `{"key":"<私钥 PEM>","passphrase":"<口令>"}` 序列化后整体加密。
> 好处是「一条凭据 = 一个密文」，不会出现「私钥换新了但口令还是旧的」这种半边状态。

**校验密码用的 verify 令牌（独立单行表，不与任何凭据共用 key）：**

```sql
CREATE TABLE vault (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  kdf         TEXT NOT NULL,           -- 'argon2id'
  kdf_params  TEXT NOT NULL,           -- {"t":3,"m":65536,"p":4}
  salt        BLOB NOT NULL,           -- 16 字节，与任何 credential.salt 不同
  iv          BLOB NOT NULL,           -- 12 字节
  ct          BLOB NOT NULL,           -- 加密的固定明文
  tag         BLOB NOT NULL,           -- 16 字节
  created_at  TEXT NOT NULL
);
```

**两种模式：**

| | 关闭加密（默认） | 开启加密 |
| --- | --- | --- |
| `payload` | 私钥 / 密码原文（UTF-8 字节） | GCM 密文 |
| `enc` | 0 | 1 |
| 需要用户操作才能用 | 否，直接连 | 是，首次使用时输一次加密密码（记到进程结束或手动锁定） |
| 库被单独拷走 | 明文可读 | 解不开 |

**开启加密后的完整流程：**

1. 设置密码：生成随机 `salt`（16 B）→
   `key = Argon2id(password, salt, t=3, m=64 MB, p=4, out=32 B)`。
2. 写 verify 令牌：`(ct, tag) = AES-256-GCM(key, iv=随机12B, aad="vault:verify", plaintext="HTACI.SERVERS.VERIFY.v1")`。
   `salt` / `iv` / `ct` / `tag` 落库，`key` 与 `password` 都不落库。
3. 每条凭据用自己的随机 `salt`：`key_i = Argon2id(password, salt_i, ...)`，
   `(ct, tag) = AES-256-GCM(key_i, iv_i, aad="cred:<id>|<kind>", plaintext=payload)`。
   **AAD 绑定 id 与 kind**，把 A 行的密文挪到 B 行会解密失败，而不是悄悄解开。
4. 解锁：用输入密码 + `vault.salt` 派生 `key` → 解密 verify 令牌 →
   **GCM tag 校验通过且明文等于固定串**才算密码正确。不是比对哈希，而是「能解开令牌」。
   失败只报「密码错误」，**不碰任何 `credentials.payload`**。
5. 解锁成功：把密码原文以 UTF-8 `byte[]` 留在内存（`string` 用完立刻丢弃引用），
   并缓存 `(credId, salt) → key` 的派生结果，避免每次连接都跑一遍 Argon2；
   `ShutdownAsync` / 「立即锁定」时对这些 `byte[]` 一律 `CryptographicOperations.ZeroMemory`。
6. 解锁态只影响**使用**，不影响 `srv_list_targets`（清单不含秘密）。

**按行独立 salt 的好处（相比单一全局 salt）：**

- 换密码时不必全库重算一遍 Argon2（可逐行惰性重加密）。
- 两行的 key 互不相同，某一行被解开不影响其它行。
- `kdf_params` 存在行上，将来调高迭代 / 换算法可以逐行迁移，
  不同行的 `enc`、`kdf` 并存也不冲突。

**密码改错与密码变更：**

- 改密码 / 关加密 = 单事务：全部解密 → 用新参数重新加密写回 →
  更新 `vault` 与新 `salt`。**事务包住整批**，中途失败整体回滚，
  不会留下「一半新一半旧、两种密码都能开一半」的残局（这是必须坚持的一点）。
- 忘记密码：只能「重置加密」= 丢弃全部 `credentials` 行与 `vault` 行，目标置为「未配置认证」。
  UI 两段式确认，并明示「凭据不可恢复」。

**这套的真实强度（要写进 UI 说明，不能含糊）：**

- 强度 ≈ **用户密码强度**。Argon2id 的 64 MB 内存成本把离线爆破的单次代价抬高到
  「几十毫秒 + 64 MB」，暴力枚举不划算，但**挡不住 `password123` 这类字典攻击**，
  所以设置密码时给强度提示，且必须输两遍。
- 库被单独拷走：没有密码原文 → 一字节也解不开，也无法从库里的任何字段反推密码或 key。
  这正是这一层唯一要保证的事。
- 本机上同用户的程序仍可读库、可 hook 内存。加密不解决这个问题（见 §3.2）。

**实现依赖（已实测通过，不是纸面结论）：**

| 包 | 版本 | 说明 |
| --- | --- | --- |
| `Konscious.Security.Cryptography.Argon2` | 1.3.1 | 纯托管 Argon2id，依赖 `Konscious.Security.Cryptography.Blake2` 1.1.1 |
| `Microsoft.Data.Sqlite` | **10.0.9** | 必须与宿主 `Directory.Packages.props` 里的版本一致，理由见下 |
| `SSH.NET` | 2026.0.0 | 依赖 `BouncyCastle.Cryptography` 2.7.0 |
| `BouncyCastle.Cryptography` | 2.7.0 | **显式引用**（SSH.NET 也传递引入它）。用来解旧式加密 PEM，见 §6.3 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.11 | 只为拿到原生 `e_sqlite3.dll` 的路径并平铺到插件目录 |

**版本必须跟宿主走，这不是洁癖，是加载期硬约束。** 宿主的 `PluginLoadContext` 规则是
「默认上下文里已经加载过的程序集一律共用宿主那份」，SQLite 全家都在此列。
插件若引用 `Microsoft.Data.Sqlite` 10.0.14（→ `SQLitePCLRaw.core` 2.1.13），
而宿主跑的是 10.0.9（→ 2.1.11），加载时直接炸：

```
System.IO.FileNotFoundException:
File name: 'SQLitePCLRaw.core, Version=2.1.13.3146, Culture=neutral, PublicKeyToken=1488e028ca7ab535'
   at HtaciAI.Servers.ServersPlugin.InstallNativeSqlite(String pluginDirectory)
```

插件目录里放着 2.1.13 的 dll 也没用 —— ALC 根本不会去看它。`PluginManager` 把这条报成
「加载失败 FileNotFoundException」，而界面上只显示 `FileNotFoundException` 四个字，
非常难猜。所以**改 SQLite 版本前先看宿主 `Directory.Packages.props`**。

同一处还有个连带问题：`NativeLibrary.SetDllImportResolver` 对同一个程序集只能设一次。
宿主已经为它用过 SQLite 设过 resolver，插件再调就抛 `InvalidOperationException`。
代码里把这次调用包在 `try/catch` 里、失败仅记一条 `Warn` —— 宿主的 resolver 已经在
程序目录找得到 `e_sqlite3.dll`，插件不需要插手，这不该让插件起不来。
`InstallNativeSqlite` 本身失败也一律降级为警告，不阻断 `InitializeAsync`。

已用 net10.0 实测（`dotnet run -c Release`，t=3 / m=64 MB / p=4）：

- Argon2id 派生 32 B：**253 ms**，同参数同 salt 两次结果一致（确定性 OK）。
- AES-256-GCM 加解密往返 OK；把 AAD 从 `cred:cred_9a2b|privateKey` 换成
  `cred:cred_ffff|privateKey` → **抛 `CryptographicException`**，跨行搬运确实被拦下。
- SQLite 建 `credentials` / `vault` 表、`BLOB` 存取 `payload/salt/iv/tag`、`kdf_params`
  文本列往返全部 OK。
- `Renci.SshNet.PrivateKeyFile` / `PasswordConnectionInfo` 正常加载。
- 用与宿主同规则的 ALC 加载真实插件目录（宿主版 SQLite 已预热 / 未预热两条路径），
  插件侧查询分别返回 `5` 与 `42`，均正常。

**仍有降级分支**（`kdf = 'pbkdf2-sha256'`，BCL `Rfc2898DeriveBytes.Pbkdf2` 600000 次），
但**不作为默认**：`kdf` 列照样保留，将来换算法或调参数可逐行迁移。
离线分发插件时注意：`bin` 里会带上 `BouncyCastle.Cryptography.dll`、
`Konscious.Security.Cryptography.*.dll`、`SQLitePCLRaw.*.dll` 与 `e_sqlite3.dll`，
按部署脚本一并复制即可（这些不是 `HtaciAI*` / `Avalonia*`，不打进插件目录反而会崩）。
`runtimes/` 下各平台的原生库不必复制，只保留平铺在根目录的 `e_sqlite3.dll`。

**其它约定（不变）：**

- `fingerprint` 由私钥推导，UI 用 `/etc/ssh/ssh-keygen -lf` 可核对 —— 这是「确实存了那把密钥」的唯一证据。
- 密码/私钥的明文只允许出现在「解密后到建立连接」这段，用完不缓存。
- 删除目标不级联删除凭据（可能被别的目标复用），删除凭据时若有目标引用则拒绝并列出引用者。

### 5.4 为什么不调系统 `ssh.exe`

| 用系统 ssh | 用 SSH.NET |
| --- | --- |
| 私钥必须落盘且能被 `-i` 指向，模型可读路径 | 私钥在内存中加载为 `PrivateKeyFile` |
| 密码认证要 `sshpass`（Windows 无）或交互式输入 | 直接 `PasswordAuthenticationMethod` |
| 无法可靠区分 stdout/stderr、退出码与截断 | 流可控，能边读边截断 |
| 无法做统一审计与超时 | 一次调用一条审计、`CommandTimeout` 精确控制 |

依赖：`Renci.SshNet` 2026.0.0、`Microsoft.Data.Sqlite` 10.0.9（与宿主同版本，见 §5.3）、
`Konscious.Security.Cryptography.Argon2` 1.3.1
（均已从 nuget.org 装进本机缓存并实测通过，见 §5.3）。
加密只用 BCL 自带 `System.Security.Cryptography.AesGcm` 做 AES-256-GCM，**不需要 `System.Security.Cryptography.ProtectedData`**，
也不依赖任何平台专有的密钥保护 API。
原风险 R1（离线无法 restore）已排除。

## 6. UI 设计

### 6.1 入口与形态

- 主窗口标签页「服务器」（`context.Tabs.OpenTab`）。
- 新标签页加一张启动卡片（`ui.slot` + `AddLauncherCard`），点了才建标签页 ——
  **不在 `InitializeAsync` 里抢先开**，那时主窗口可能还没建好。
- 插件页设置（`ui.settings`）只放概览：目标数、凭据数、加密状态（关 / 已开启·未解锁 / 已开启·已解锁）、
  「打开服务器页」按钮、「清空审计」两段式按钮。**不放列表和编辑表单**，契约明确要求管理界面放自己的标签页。
- 加密开关与解锁也放在服务器标签页里（顶部状态条），不放在插件设置页 —— 它会弹密码输入框，
  而契约要求插件设置页不做「危险动作确认弹窗」那类交互。

### 6.2 标签页布局

```
┌──────────────────────────────────────────────────────────────────────┐
│ 服务器                                                                │
│ 在这里配置受管服务器。私钥与密码只存在插件自己的库里……                │
├───────────────┬──────────────────────────────────────────────────────┤
│ 目标列表 ⌂ + ⟳│  prod-web-01                          [测试连接] [保存]│
│               │  生产 · deploy@10.0.0.21:22                          │
│ ▾ 生产 (2)    │  ┌─ 基本信息 ───────────────────────────────────┐     │
│   ● prod-web  │  │ 名称 [prod-web-01        ]  分组 [生产     ] │     │
│   ● prod-db   │  │ 主机 [10.0.0.21   ]  端口 [22]              │     │
│ ▾ 测试 (1)    │  │ 用户 [deploy      ]  默认目录 [/srv/app ]   │     │
│   ● staging   │  └──────────────────────────────────────────────┘     │
│               │  ┌─ 认证 ───────────────────────────────────────┐     │
│               │  │ ( ) SSH 密钥                          [已配置]│     │
│               │  │     SHA256:8Zx…m1K  ed25519  ·  [替换][删除] │     │
│               │  │ ( ) 密码                              [未配置]│     │
│               │  └──────────────────────────────────────────────┘     │
│               │  ┌─ Agent 权限 ─────────────────────────────────┐     │
│               │  │ 可见性 [可执行 ▾]     审批 [每次审批 ▾]      │     │
│               │  └──────────────────────────────────────────────┘     │
│               │  ┌─ 最近连接 ───────────────────────────────────┐     │
│               │  │ 11-08 10:01 成功 · 主机密钥已信任  ed25519   │     │
│               │  └──────────────────────────────────────────────┘     │
├───────────────┴──────────────────────────────────────────────────────┤
│ 目标 3 · 凭据 2 · 活动 shell 1 · 加密：关          [加密设置] [审计 →]│
└──────────────────────────────────────────────────────────────────────┘
```

右侧详情是**就地编辑 + 显式保存**（不是弹窗）：改完点「保存」，未保存时标题旁出现 `●`。
删除用「点一次变红、再点一次」两段式（遵循 `IPluginSettingsProvider` 的同一条建议）。
审计记录点开是一个覆盖式的内嵌页（不是弹窗），含筛选与清空。

**列表头那三个图标按钮**（左侧边栏顶部，一行「目标列表」+ 三个图标）：

| 图标 | 动作 | tooltip |
| --- | --- | --- |
| ⌂ 房子 | 回到默认视图：丢草稿、重读库、选中第一条 | 首页（回到列表开头） |
| + 加号 | 新建目标 | 新建目标 |
| ⟳ 环形箭头 | 重读库 | 刷新列表 |

三条都是「对列表本身的操作」，挂在列表头上比挂在整页右上角更说明问题 ——
右上角离列表隔了半个屏幕，加目标时视线要来回跳。

图标用**宿主的 Fluent System Icons**（`PluginFonts.IconFont`，见 `docs/icon-glyphs.md`）：
`home_24_regular` U+F481、`add_24_regular` U+F10A、`arrow_clockwise_24_regular` U+F13E。
不要自绘几何路径（线条粗细、端点、视觉重心都和宿主其它界面不一路），也不要硬写
`"Segoe Fluent Icons"`（微软专有、禁止再分发）。

**「保存」的含义是整页，不是只存基本信息。** 这是踩过的坑，必须写死：

- 早前的实现里，认证区有一个独立的「保存密钥」，而顶部「保存」只提交基本信息。
  用户在私钥框里粘贴、再点顶部「保存」，界面回「已保存」，而库里 `credentials` 表**一条都没有** ——
  私钥从来没被提交过。页面上只有「保存」，用户没有理由知道还差一次点击。
- 现在**页面上只有这一个保存入口**，按顺序做：认证区待提交的密钥/密码 → 基本信息与权限。
  其中任一步校验失败，整次保存就算失败并留在原地（不允许出现「目标存了、密钥没存」）。
- 认证区的三块内容（密钥输入、密码输入、已配置摘要）**各自只创建一次**，
  切换认证方式只改 `IsVisible`。既保住用户已粘贴的内容，也避开「一个控件同时只能有一个父级」。

> **不要「重建容器再重新挂载复用控件」。**
> 曾经的写法是每次切换都 `new StackPanel().Children.Add(同一个 TextBox)`：旧 StackPanel 被
> `Content` 换掉后虽然不在可视树上，却仍占着那个 TextBox 的父级。于是
> 「SSH → 密码 → SSH → 密码」到第三、四次切换时 `Add` 一个已有父级的控件直接抛异常，
> 而这发生在 Chip 的 Click 事件里，宿主没有兜底 → **界面卡死、进程退出**。
> 同类问题也出现在 `UiKit.ChipRow`：它曾在自己的 Click 里 `Children.Clear()` 重建自己，
> 现在改成只更新样式、不动可视树。回归用例：`--authswitch`。

**有未保存内容时离开要问。** 触发点：点左侧另一条目标、点「首页」、「新建目标」、「刷新」。
判断依据是 `IsDirty`（未保存过的新目标、字段与库不一致、**或认证区里有已输入但没提交的内容**）。
弹的是就地遮罩上的三选一，不是「是否保存」的二选一 —— 二选一里的「否」是歧义，
既像「不保存」又像「取消这次操作」，点错要么丢一把刚粘贴的私钥，要么莫名其妙没切过去：

| 选项 | 行为 |
| --- | --- |
| 取消 | 什么都不做，留在当前页（等同于没点那个按钮） |
| 不保存 | 丢弃改动并继续离开。单击即生效，不再二次确认 |
| 保存 | 先整页保存，成功才离开；校验失败则留在原地让人改 |

遮罩是半透明的，用户要能看见自己正要离开的是哪一页。

### 6.3 凭据编辑流程

1. 选「SSH 密钥」→ 出现多行私钥输入框 + 「口令（可选）」密码框。
2. 点右上角**唯一**的「保存」→ `KeyInspector.Inspect` 先清洗再解析 → 失败就在页面顶部给一条红字。
3. 成功 → **立刻清空输入框**，改为显示 `keyType` + `publicFingerprint` + `[替换] [删除]`。
   指纹让用户可以拿 `ssh-keygen -lf` 核对，这是「我确实保存了那把密钥」的唯一证据。
4. 密码认证同理，但**永远不回显**：保存后就只剩「已配置 · 辨认提示」。

**粘贴脏数据必须先洗再用。** 从网页、聊天工具、邮件里复制 PEM，常会带进
零宽空格（U+200B）、BOM（U+FEFF）、不换行空格（U+00A0）、全角空格（U+3000），或者被折错行。
这些字符肉眼看不见，却会让 `Convert.FromBase64String` 抛
「The input is not a valid Base-64 string…」—— 用户拿的是一把完全正确的密钥，
配一条谁也看不懂的报错。`KeyInspector.Normalize` 的处理顺序：

1. 去掉 BOM / 零宽字符 / 软连字符，全角空格与不换行空格换成普通空格；
2. 换行统一成 LF，去掉每行首尾空白（PEM 的缩进无意义）；
3. 截掉 `-----BEGIN` 之前与 `-----END` 之后的内容（有人会连文件名、时间戳一起复制）；
4. Base64 段只保留合法字符并按 64 列重排；`Proc-Type:` / `DEK-Info:` 这类头部行原样保留；
5. 缺 `-----END` 时按 BEGIN 的标记名补一行 —— 少复制一行不该让整把密钥作废。

**入库用的是清洗后的文本**（`KeyInspection.NormalizedKey`），不是用户粘贴的原文。
底层 .NET 异常文本不再直接抛给用户：`FormatException` 与「口令不对」各有专门的中文说明。
解析失败时写一条 `Warn` 日志，只记长度与首行（BEGIN 标记），不记私钥内容。

**报错必须具体到「哪一行、差几个字符」。** 只说「无法解码」用户没法行动 ——
他手里那把密钥在自己的机器上是好的，会认定是插件坏了。所以解码前先量一次 Base64 段的形状
（`KeyInspector.DiagnoseLayout`），字符数不是 4 的倍数就直接给出定位：

> 私钥内容不完整：Base64 段共 1590 个字符，不是 4 的倍数（余 2 个字符）。
> 第 23 行有 66 个字符（其它行是 64 个），最后一行为 52 个字符。
> 这通常是复制时被终端、网页或聊天窗口折行/截断造成的。
> 请用文本编辑器（记事本、VS Code）打开私钥文件，全选后一次性重新复制。

**不做「猜着修复」。** 少几个字符的私钥可以用 RSA 数学（`n == p*q`、`e·d ≡ 1`、
`dp/dq/qinv` 一致）反推出唯一解，但那是把一个不可信的猜测变成用户以为正确的凭据。
宁可让人重拷一遍，也不静默改用户的数据。

**首选「从文件载入…」，其次才是粘贴。** 认证区有一个「从文件载入…」按钮，用
`TopLevel.StorageProvider.OpenFilePickerAsync` 直接读磁盘上的 `.pem` / `id_rsa`。
理由不是体验，是正确性：手动复制那段路上，任何中间层都可能改动内容 ——
聊天窗口、网页渲染、终端回显、远程桌面剪贴板同步。真实案例：

> 用户在聊天里贴了一把 2048 位 RSA 私钥（`BEGIN RSA PRIVATE KEY`，25 行 base64）。
> 那份文本的 Base64 段变成 1590 字符（应为 1588），其中一行被撑到 66 个字符、
> 末行只剩 52（应为 56）。于是插件报「内容不完整」。
> **但磁盘上同一个文件（`F:\ssh\test-server.pem`，1700 字节，CRLF，1588 字符）是完好的**，
> 插件用 `--keys` 直接跑它：`OK ssh-rsa SHA256:0eajZNA4bZDmNvEXOcmzdOi8zWuXvDRCMZcwJYt/qEE`。
> 用户在此之前一直以为「密钥被插件判错了」，实际是复制那一步坏的。

同一节还补了误贴识别，认得出就直接说，不让用户去猜：

| 贴进来的东西 | 提示 |
| --- | --- |
| `.pub` 公钥 / `authorized_keys` 一行 | 「这是**公钥**，不是私钥」+ 该选什么文件 |
| `PuTTY-User-Key-File-*`（.ppk） | 「用 PuTTYgen → Conversions → Export OpenSSH key 转换」 |
| `BEGIN ENCRYPTED PRIVATE KEY`（PKCS#8 加密） | 「当前不支持，用 `ssh-keygen -p -m PEM` 转换」 |
| 云厂商给的 JSON 凭据 | 「只粘贴其中私钥字段那一段」 |

**输入框不会截断大文本**（这是专门验过的）：把一把 1700 字节 / 2458 字符的 RSA PEM
填进 `TextBox`、点「保存」、从库里解出来逐字比对 —— 完全相同。所以「存进去的比粘的短」
这种怀疑可以排除，问题只会在粘贴源那头。


**支持的私钥格式**（都在 `--keys` 模式下用 `ssh-keygen` 生成的真实文件验过）：

| 格式 | 头部 | 结果 |
| --- | --- | --- |
| OpenSSH（默认） | `OPENSSH PRIVATE KEY` | ✅ 直连 |
| PKCS#1 | `RSA PRIVATE KEY` | ✅ 直连 |
| PKCS#8 | `PRIVATE KEY` | ✅ 直连 |
| 旧式加密 PEM | `RSA PRIVATE KEY` + `Proc-Type: 4,ENCRYPTED` | ⚠️ SSH.NET **不认**，见下 |

旧式加密 PEM（`ssh-keygen -m PEM` 加了口令，十几年的常见产物）SSH.NET 会抛
`Invalid private key file.`，而那句话会被译成「不是有效的 OpenSSH/PEM 私钥」——
用户的密钥明明是好的。`PrivateKeyLoader` 的处理：先试 SSH.NET 直连，失败且发现
`Proc-Type` / `DEK-Info` 时用 BouncyCastle 的 `PemReader` 带口令解开，导出成 PKCS#8 明文 PEM
再交给 SSH.NET。口令留空 → 「请在『口令』一栏填写」；口令错 → 「口令不对，解不开这把私钥」。
连接时（`SshConnector`）走的也是这个入口，否则「保存得进去、连的时候炸」。
5. 加密已开启且当前未解锁时，第 2 步会先弹一次加密密码输入（就地一个内联框，不是新窗口）：
   没有派生出的 key 就没法加密新凭据。解锁过一次后本进程内不再问。

### 6.4 测试连接与 TOFU

「测试连接」是**唯一**允许接受新主机密钥的地方，流程：

1. 连接 → 收到 host key → 查 `hostKeys`。
2. 已信任且指纹一致 → 显示「成功 · 42ms · 主机密钥已信任」。
3. 未信任 → 详情里插入一条橙色确认条：
   `主机密钥 10.0.0.21 首次出现：ssh-ed25519 SHA256:3Yk…9Qf　[信任并保存] [取消]`
4. 已信任但**指纹不一致** → **红色**警告条，只给「取消」和「我确认主机换过密钥」两个选项，
   后者要求把旧指纹手动抄进确认框（防手滑）。这条在密码泄露场景里是最后一道闸。

工具层不做 TOFU：`srv_exec` 碰到未信任主机直接失败，把指纹放进错误文案，让模型去请用户操作。
**UI 负责信任，工具负责拒绝** —— 这条分工让「自动化」不可能悄悄建立一条未校验的连接。

### 6.5 加密设置与解锁

```
┌─ 加密存储 ────────────────────────────────────────────────┐
│ 状态：已开启 · 已解锁（本次启动后有效）      [立即锁定]     │
│ 加密密码不会保存，忘记后只能重置加密，已存凭据将全部丢失。  │
│ 密钥派生：Argon2id · t=3 · 64 MB · p=4 · 每行独立盐 16 B    │
│                                    [修改密码]  [关闭加密]  │
└───────────────────────────────────────────────────────────┘
```

开关是一个 `ToggleSwitch` + 一段**常驻说明文字**（不是折叠的警告）：

> 关闭加密时，服务器密码与私钥以**明文**存放在本机数据库里。
> 任何能读这个文件的程序都能直接拿到它们。加密只是防止「库被单独拷走」，
> 挡不住本机上同用户的程序 —— 自己的私钥请自行妥善保管。

| 动作 | 交互 |
| --- | --- |
| 开启加密 | 输密码 ×2（第二遍防手误，附强度提示）→ 生成 `vault` 行与 verify 令牌 → 现有凭据逐条用自己的 salt 重新加密（单事务） |
| 解锁 | 输一次密码 → 用 `vault.salt` 派生 key → 解密 verify 令牌（tag 校验 + 明文比对）→ 通过则缓存密码与派生 key，有效到进程结束或点「立即锁定」 |
| 修改加密密码 | 输旧密码 + 新密码 ×2（旧密码先用 verify 令牌验一遍） |
| 关闭加密 | 输当前加密密码 → 明文写回 → 提示「此后为明文」 |
| 忘记密码 | 「重置加密」= 清空全部 `credentials` 行与 `vault` 行、目标置为「未配置认证」，两段式确认 |

解锁态只影响**使用**，不影响 `srv_list_targets`：清单本身不含秘密，未解锁也能列出来。

## 7. 工具设计

工具集：`new PluginToolset { Key = "servers", Name = "服务器", Description = "受管服务器的清单与远程终端" }`。
所有工具 id 形如 `plugin:htaci.servers:srv_exec`。

| 工具 | 危险等级 | 超时 | 说明 |
| --- | --- | --- | --- |
| `srv_list_targets` | Safe | 10s | 列出可访问目标（按 `agentAccess` 过滤） |
| `srv_exec` | Danger | 600s | 非交互式执行一条命令，返回 stdout/stderr/退出码 |
| `srv_shell_open` | Danger | 60s | 开一个持久交互式 shell，返回 `shell_id` |
| `srv_shell_send` | Danger | 300s | 往 shell 写输入并等到命令结束标记 |
| `srv_shell_read` | Risk | 60s | 只读：取 shell 的增量输出 |
| `srv_shell_close` | Safe | 10s | 关闭 shell |
| `srv_upload` | Danger | 300s | 本地文件 → 远端（SFTP），文件内容不进上下文 |
| `srv_download` | Risk | 300s | 远端 → 本地（SFTP），本地路径被锁在根目录内 |

Danger 的依据沿用内置浏览器的口径：**能不能丢掉用户的东西**。远程执行任意命令必然 Danger，
`shell_read` 只读但会消耗缓冲区（影响后续 `send` 的判断），所以 Risk。

### 7.1 `srv_list_targets`

入参 `{}`。返回：

```
可用目标 2 个（另一个目标被用户设为对 Agent 隐藏，不在此列出）：
- srv_7f3a1c  prod-web-01  生产  deploy@10.0.0.21:22   密钥认证
- srv_1c2d3e  staging      测试  root@10.0.0.30:22     密钥认证
提示：先用 srv_exec 跑只读命令确认环境，再动写入类操作。
```

`agentAccess=hidden` 的目标既不出现在这里，也不接受任何工具调用（返回「目标不存在或未授权」，
**不区分两者**，避免用错误文案探测 id）。`agentAccess=list` 的目标出现，但执行时拒绝并说明原因。

### 7.2 `srv_exec`

```json
{
  "type": "object",
  "properties": {
    "target": {"type": "string", "description": "srv_list_targets 给出的 id"},
    "command": {"type": "string", "description": "要在远端执行的一条命令；需要多步请用 && 连接"},
    "cwd": {"type": "string", "description": "可选。工作目录，默认用目标配置的目录"},
    "timeout_sec": {"type": "integer", "description": "可选。默认 120，最大 540"},
    "max_bytes": {"type": "integer", "description": "可选。stdout 上限，默认 65536"}
  },
  "required": ["target", "command"]
}
```

实现要点：

- 无 PTY（`SshCommand` 默认），因此**不能交互式回答提示**；`sudo` 无 NOPASSWD 会失败，
  需要交互的场景应改用 `srv_shell_*`。
- 用 `BeginExecute` + 流式读，**边读边截断**，不是先全读再截 —— 一条 `cat` 大文件不该把插件吃满内存。
- stdout 超限时保留头 48 KB + 尾 16 KB，中间插一行
  `… [已截断 1.2 MB / 共 1.4 MB] …`。
- stderr 单独一段，上限 16 KB。
- 返回文本格式（给模型看的，稳定格式便于它解析）：

```
[prod-web-01] exit=0  42ms
--- stdout ---
...内容...
--- stderr ---
（空）
```

- **退出码非 0 仍算 `Success=true`**：命令执行成功了，只是结果非零。只有**连接/认证/超时**才
  `Success=false`。这个区分很关键，否则模型会把「grep 没匹配到」当成基础设施故障。
- 超时要如实回 `Success=false`，并带已产生的输出（用户仍会看到部分结果）。

### 7.3 交互式 shell 的协议

这是本设计里唯一需要仔细设计的部分。核心问题：模型调用是**同步请求-响应**，
而终端是持续输出流。若每次 `send` 都靠固定 `sleep` 再读，模型会拿到半截输出、上下文被噪声污染。

**结束标记协议（nonce）**

1. `shell_open` 后先执行 `stty -echo 2>/dev/null; export PS1=; export PS2=; export PROMPT_COMMAND=`，
   让回显和提示符不再干扰输出（保留应用回显，便于调试）。
2. 每次 `send` 生成 8 位 hex nonce，实际写入：
   ```
   { <input> ; } ; printf '\n__HTACI_%s:%s\n' 3f9a1c2e $?
   ```
3. 读到 `__HTACI_3f9a1c2e:<code>` 行为止 → 剥离该行与命令回显 → 返回增量输出 + `exit_code`。
4. 超时未出现标记（例如进了 `vim`、`top`、`ssh` 到第二台机器）→ 返回已收到的输出 +
   `settled: false`，并在文案里提示「命令可能仍在运行，可用 `srv_shell_send` 发 `\u0003` 中断，
   或用 `srv_shell_read` 继续取输出」。**不假装成功**。

参数（`srv_shell_send`）：

| 参数 | 说明 |
| --- | --- |
| `shell_id` | 必填 |
| `input` | 要写入的文本，按字面写入（`\n` 需自己写） |
| `keys` | 可选，特殊键：`ctrl-c` / `ctrl-d` / `tab` / `enter` / `up` / `down` |
| `until` | 可选正则，替代 nonce（用于盯着某个输出出现，如 `Password:`） |
| `settle_ms` | 可选。无新输出持续多久算就绪，默认 700 |
| `timeout_sec` | 可选，默认 30，最大 300 |

状态机：`Opening → Ready → Busy → Ready … → Closed`。`Busy` 期间再调 `send` 直接拒绝
（避免两个调用交错写同一个流），文案说明「上一条还没结束，先 `srv_shell_read` 或提高 timeout」。

生命周期与配额：

- 内存 `Dictionary<string, ShellSession>`，key 为 `shell_id`（`sh_` + 6 hex）。
- **绑定 `call.SessionId`**：别的会话拿到 `shell_id` 也用不了（防跨会话串用）。
- 每目标最多 2 个、全局最多 4 个 shell，超出拒绝并提示关掉旧的。
- 空闲 10 分钟自动关闭；`ShutdownAsync` 时必须全部关闭并等待（否则插件会「需重启」才能卸载）。
- 连接断开（`client.IsConnected == false`）时从表里摘掉，工具返回「会话已断开」。

### 7.4 输出的通用规则

- 全部结果按「给模型看的文本」写：长输出自己截断、错误给可执行下一步。
- 剥离 ANSI 转义（默认），`strip_ansi: false` 可保留（部分用户要 `ls --color` 的对比度）。
- 全流程写 `context.Log`，但日志里**命令只写前 120 字符**。

### 7.5 文件传输（`srv_upload` / `srv_download`）

**为什么不复用 `srv_exec` + base64**：文件内容会整个进上下文（几 MB 就是几百万 token），
命令行有长度上限、NUL 字节不安全，出错只能整个重传。

| 项 | 约定 |
| --- | --- |
| 通道 | 复用同一套凭据与**同一套主机密钥校验**：`SftpClient` 由 `SshConnector` 开，未信任主机一样拒绝 |
| 单文件上限 | 8 MB（超过就拒绝，让用户在远端用 `head`/`tail` 取需要的一段） |
| 覆盖 | 默认不覆盖；远端/本地已存在时报错，model 要显式传 `overwrite=true` |
| 本地路径 | **必须落在「本地文件根目录」内**（默认 `…\Downloads\htaci-servers`，设置在插件页改）；相对路径按该目录解析；`..` 穿越一律拒绝 |
| 敏感目录 | 额外硬拦 `.ssh` / `.gnupg` 这些路径段 —— 用户把根目录设成主目录时也不至于一次事故 |
| 远端路径 | **不受限**。`srv_exec` 本来就能读写远端任意文件，限制它只会制造「为什么这个不行」的困惑 |

本地路径这条限制是本插件唯一新增的破坏面：没有它，模型就能把 `~/.ssh/id_rsa` 传到远端去，
整个凭据托管设计立刻失去意义。远端路径不设限则是诚实的取舍 —— 已有能力等价，不假装收紧。

## 8. 错误文案约定

模型会把这些话原样转述给用户，所以每条都要能直接行动。

| 场景 | 返回（`Success=false`） |
| --- | --- |
| 目标不存在 / 隐藏 | `目标不存在或未授权。请让用户确认「服务器」页里的 Agent 可见性设置。` |
| 目标只读 | `目标 prod-web-01 被设为「仅列表可见」，不能执行命令。` |
| 主机密钥未信任 | `主机密钥未信任（ssh-ed25519 SHA256:3Yk…9Qf）。请让用户在「服务器」页点「测试连接」确认指纹。` |
| 主机密钥变了 | `主机密钥与上次记录不一致！可能是主机重装，也可能是中间人。请让用户人工核对后再继续。` |
| 认证失败 | `认证失败（publickey）。请让用户检查密钥是否已加入远端 authorized_keys。` |
| 加密未解锁 | `凭据已加密且当前未解锁，请在「服务器」页点「解锁」输入加密密码后重试。` |
| 密码错误 | `加密密码校验失败（verify 令牌解不开），凭据未解密。请让用户确认密码（插件无法找回）。` |
| 网络不可达 | `连接超时 10s。请确认目标 host/port，或用户是否需要先连 VPN。` |

## 9. 实现计划

### M1 骨架（可跑通，不含真实 SSH）

- 工程与 `plugin.json`、部署脚本 `deploy.cmd`（参考内置浏览器那份）。
- `ServersDb`（SQLite 建表 + 迁移版本号 + 串行化访问）/ `TargetStore` / `HostKeyStore` /
  `CredentialStore`（`enc=0` 明文路径先跑通，`enc/kdf/kdf_params/salt/iv/tag` 列与 `vault` 表先建好）。
- 标签页 UI：列表、表单、保存、删除、底部状态条（含「加密：关」）。
- `srv_list_targets` + 设置页概览 + 启动卡片。
- 验收：能在 UI 里建/改/删目标，重启宿主后数据还在；`srv_list_targets` 返回正确且过滤 `hidden`。

### M2 SSH 执行链路

- `SshConnectionFactory`（密钥/密码、超时、host key 回调）。
- `srv_exec`（流式读取 + 截断 + 退出码语义）。
- UI 的「测试连接」与 TOFU 确认条。
- 验收：对真实主机执行 `uname -a` 成功；错误密码/错误端口给出对应文案；未信任主机被拒。

### M3 加密开关 + 交互式 shell

- 加密路径：Argon2id KDF（每行独立 salt + `kdf_params`）、AES-256-GCM（AAD 绑定 id/kind）、
  verify 令牌校验、开启/修改/关闭/重置、解锁与「立即锁定」。
- `ShellSession` / `ShellSessionManager`、nonce 协议、`srv_shell_open/send/read/close`。
- 验收：开启加密后库内 `payload` 是二进制且重启后需输密码；关闭加密后可直接读出明文；
  密码输错只报校验失败不损坏数据；把 A 行密文搬到 B 行 → 解密失败（AAD 生效）；
  `srv_shell_send` 能跑 `cd /tmp && ls`；
  `top` 这类不返回标记的场景返回 `settled:false` 且能靠 `ctrl-c` 恢复；
  插件禁用后 `shell` 全部关闭、无「需重启」提示。

### M4 打磨

- 审计筛选、导出（可选）、日志轮转。
- 分组拖拽、搜索、`hidden` 一键切换。
- 空状态引导（首次打开时的三步说明）。

### 实现状态（v0.2.0）

M1–M3 已完成并部署，M4 只做了审计筛选。落地位置：

| 层 | 文件 |
| --- | --- |
| 入口 | `ServersPlugin.cs`（IPlugin + IPluginSettingsProvider，含 SQLite 原生库 resolver） |
| 服务容器 | `ServerCore.cs` |
| 存储 | `Data/ServersDb.cs`（建表/迁移/串行化/`secure_delete`/`CompactAsync`）、`TargetStore`、`CredentialStore`、`HostKeyStore`、`AuditStore` |
| 加密 | `Security/Crypto.cs`（Argon2id + AES-GCM）、`VaultSession.cs`、`CredentialSecret.cs` |
| 私钥 | `Security/KeyInspector.cs`（清洗 + 形状诊断 + 体检）、`PrivateKeyLoader.cs`（含旧式加密 PEM 解密） |
| SSH | `Ssh/SshConnector.cs`（唯一建连接入口）、`CommandRunner`、`ShellSession`、`TransferService` |
| 工具 | `Tools/ServersTools.cs`（8 个） |
| 界面 | `Ui/ServersPage`、`Ui/TargetDetailPanel`、`Ui/VaultPanel`、`Ui/AuditPanel`、`Ui/ServersSettingsView`、`Ui/UiKit`、`Ui/Icons`（宿主 Fluent 字形） |
| 自检 | `HtaciAI.Plugins` 仓库的 `HtaciAI.Servers.Tests`：默认 60 项断言；`--ui <目录>` 出界面截图；`--credform <目录>` 走真实控件的表单回归；`--keys <目录>` 对目录里每个文件跑私钥体检；`--authswitch` 反复切换认证方式查崩溃 |

自检里发现并修掉的真问题，都写在这里免得被后人重犯：

1. **审计写入用 `ContinueWith(OnlyOnFaulted)` 吞异常**，结果成功时返回的是一个 Canceled 的 Task，
   `await` 直接抛 `TaskCanceledException` —— 每一次正常执行都会变成「已取消」。改成 `try/catch`。
2. **SQLite 的 UPDATE 不擦旧字节**：开启加密后，加密前的明文仍然躺在空闲页里，
   用文本工具一搜就能翻出来，「库被拷走读不出私钥」这句话是假的。
   修法是连接上 `PRAGMA secure_delete=ON`，并在每次整批重写凭据后 `VACUUM` + `wal_checkpoint(TRUNCATE)`。
3. **顶部「保存」不提交认证区**：用户在私钥框里粘贴、点顶部「保存」，界面回「已保存」，
   而库里 `credentials` 一条都没有。这是用户实际报上来的问题，也是本文件里最贵的一次教训 ——
   「两个按钮提交的东西不一样」在界面上完全看不出来。修法见 §6.2（保存=整页，另加三选一确认）。
4. **正确的私钥配上「not a valid Base-64 string」**：从网页/聊天工具复制 PEM 时混进了
   零宽空格与全角空格，`Convert.FromBase64String` 直接抛底层异常，用户完全无从下手。
   修法是先 `KeyInspector.Normalize` 清洗再解析，并把底层异常换成可行动的中文说明（见 §6.3）。
5. **改了代码但界面没变**：一次「已部署」实际只复制了旧产物，宿主重启后看到的还是旧界面
   （认证区仍挂着已删除的「保存密钥」按钮）。教训：部署后必须核对部署目录与 `bin` 里
   `HtaciAI.Servers.dll` 的**大小与写入时间一致**，只看「复制成功」不算。
6. **旧式加密 PEM 一律被判「不是有效私钥」**：`ssh-keygen -m PEM` 加口令生成的密钥，
   SSH.NET 自己解析不了（`Invalid private key file.`），于是正确的密钥 + 正确的口令也存不进去。
   修法是 `PrivateKeyLoader` 用 BouncyCastle 先解密再交给 SSH.NET（见 §6.3）。
   **这条只有真的拿 `ssh-keygen` 生成各种格式跑一遍才会发现** —— 手写几个"看起来像"的
   PEM 样本永远撞不上。
7. **报错说不到点子上等于没报错**：用户手里那把密钥在自己机器上是好的，报「无法解码」只会
   被理解成插件坏了。现在先量 Base64 段形状，直接说「共 1590 个字符，余 2；第 23 行有 66 个字符」。
8. **切换认证方式把宿主搞崩**：点「密码」→ 切回「SSH 密钥」→ 再点「密码」，界面卡死、进程退出。
   根因是每次切换都新建容器再把同一个 TextBox 挂进去 —— 旧容器仍占着父级，第三次挂载抛异常，
   而异常发生在 Click 事件里没人接。修法是认证区三块内容只建一次、只切 `IsVisible`；
   同时把 `UiKit.ChipRow` 改成只更新样式、不在自己的 Click 里重建自己。回归用例：`--authswitch`。
   这类"复用控件 + 重建容器"的写法在 Avalonia 里是硬伤，不是偶发。

## 10. 验收清单

1. 插件页显示「运行中」，`plugin.log` 无警告；卸载后状态是「已停止」而不是「需重启」。
2. 关闭加密时：`servers.db` 用 DB 工具打开，`credentials.payload` 能直接读出私钥 / 密码原文
   —— 这是开关的**预期行为**，用以验证「明文」不是靠模糊措辞掩盖的。
3. 开启加密后：`payload` 为二进制、`salt/iv/tag` 有值；把 `servers.db` 拷到另一台机器，
   没有密码拿不到任何凭据；库里搜不到密码原文，也搜不到任何等价的哈希/密钥字段
   （`vault` 表里只有 salt/iv/ct/tag）。
4. 密码输错 → 报「校验失败」，`payload` 一个字节都没被改写；连续错 5 次不锁库（无全局锁，
   只是每次都失败），因为池子里本来就只有用户自己在试。
5. **verify 令牌与 AAD**：手工把 `vault.ct` 改成随机字节再解锁 → 必然失败但数据完好；
   把 A 行的 `payload/salt/iv/tag` 整套拷到 B 行 → 解密失败（AAD 绑定了 id 与 kind），
   而不是悄悄解出一堆乱码当私钥用。
6. 工具入参 schema 里没有任何凭据字段；任何工具输出都不含私钥内容、密码、绝对路径。
7. `agentAccess=hidden` 的目标：列表里看不到，直接用 id 调 `srv_exec` 被拒。
8. 未信任主机 → `srv_exec` 失败并带指纹；在 UI 确认后同一目标立刻可用。
9. 主机密钥被篡改 → 红色警告，工具拒绝连接。
10. `srv_exec` 里 `exit 3` 返回 `Success=true` 且 `exit=3`；`timeout` 返回 `Success=false` 且带部分输出。
11. 100 MB 的 `cat` 不会让插件内存暴涨；返回内容含截断标记。
12. 两个并行会话互相拿不到对方的 `shell_id`。
13. 插件禁用时活动的 shell 被关闭，插件能干净卸载。
14. **旧明文残留**：开启加密后，`servers.db` 与 `servers.db-wal` 里用文本工具搜不到私钥/密码原文
    （靠 `secure_delete=ON` + 整批重写后的 `VACUUM` 保证；这一条曾实测失败，见 §9 实现状态）。
15. `srv_upload` / `srv_download`：本地路径越出根目录、含 `..` 穿越、指向 `.ssh` 一律拒绝；
    单文件超过 8 MB 拒绝；默认不覆盖已存在文件。
16. **顶部「保存」提交整页**：在密钥框里粘贴私钥、不点「保存密钥」，直接点顶部「保存」→
    `credentials` 表新增 1 条、目标 `credential_id` 已绑定、解密回原文与粘贴内容一致
    （换行已规范成 LF）。这一条曾实测失败：旧实现的顶部保存不碰认证区。
17. **未保存内容确认**：新建目标或改动字段后，点左侧另一条目标 / 「首页」 / 「新建目标」 / 「刷新」→
    弹出「取消 / 不保存 / 保存」；选取消不做任何改变且草稿还在，选不保存丢弃并继续，
    选保存先落库再继续；校验失败（缺名称/主机/用户、私钥解析不了）时留在原地。
18. **私钥脏数据**：私钥里混入 BOM / 零宽空格 / 全角空格、前后带文件名与时间戳、
    或者 base64 被折成 20 列 —— 全部能解析成功，且指纹与干净原文一致；入库的是清洗后的文本。
    残缺的私钥仍然失败，且失败文案里不出现 `Base-64` 这类底层术语。
19. **列表头图标按钮**：`home` / `add` / `arrow_clockwise` 三个 Fluent 字形（不是自绘路径、
    不是 `Segoe Fluent Icons`），都有 tooltip；有未保存内容时都会先弹三选一。
20. **私钥格式覆盖**：OpenSSH、PKCS#1（`RSA PRIVATE KEY`）、PKCS#8（`PRIVATE KEY`）、
    ECDSA、ed25519 全部能保存；旧式加密 PEM（`Proc-Type: 4,ENCRYPTED`）填对口令能保存，
    口令留空 / 口令错误各给一条中文提示。用 `--keys <目录>` 可对真实 `ssh-keygen` 产物复验。
21. **私钥报错能定位**：把一把完整密钥删掉 2 个字符再粘贴 → 报错必须说出 Base64 段总字符数、
    余数、以及宽度异常的那一行；不能只回「无法解码」。
22. **大密钥不被输入框截断**：1700 字节 / 2458 字符的 RSA PEM 填进 `TextBox` → 内容长度不变、
    保存成功、从库里解出来逐字相同、再次解析指纹一致。
23. **「从文件载入…」可用**：选磁盘上的 `.pem` → 内容进入输入框、提示字符数；
    点「保存」后落库。
24. **误贴识别**：贴 `.pub` 公钥 / `.ppk` / PKCS#8 加密私钥 / JSON 凭据 → 各自给出针对性的中文提示。
25. **反复切换认证方式不崩**：在「SSH 密钥 / 密码」之间来回点十几次，
    包括点「替换」之后继续切换 → 不抛异常、不卡死；
    切走再切回来时输入框里的内容还在。用 `--authswitch` 复验。

## 11. 待定问题

| # | 问题 | 建议 |
| --- | --- | --- |
| Q1 | 是否允许模型新建目标（例如「把这台机器加进去」） | 不允许。目标只能由用户在 UI 里添加，这是本插件安全模型的基础 |
| Q2 | 是否需要「在 UI 里开一个交互终端」而不只是给模型用 | 建议 M4 再说：它需要 `IPluginInteractionHost` 之外的一套 IO 视图，收益主要是排障 |
| Q3 | 审计里的命令是否跟加密走 | 不跟。审计记录的是命令文本（无凭据），用户自查用；库被拷走时命令会暴露，可接受。若以后要跟，只加密 `command` 列即可 |
| Q4 | Linux/macOS 支持 | 去掉 DPAPI 后不再依赖平台专有 API，三平台同一套实现，`platforms` 可写全；但只在 Windows 实测过，其余待验证 |
| Q5 | 加密默认开还是关 | **默认关**（本轮决策）。首次打开服务器页时顶部提示一次「当前为明文存储」，点「去开启」或「知道了」，不重复打扰 |
| Q6 | 是否给「记住加密密码到重启」之外的选项 | 建议保持「进程内解锁」一种。系统级自动解锁意味着密码又要落盘，等于白加密 |
| Q7 | KDF 用 Argon2id 还是退到 BCL 的 PBKDF2 | **已定案 Argon2id**（`Konscious.Security.Cryptography.Argon2` 1.3.1，t=3 / 64 MB / p=4 / out 32 B）。包已装进本机缓存并实测 253 ms，确定性、GCM、AAD、SQLite 全部验证通过。PBKDF2 600000 次仅作为 `kdf` 列的备用分支保留 |
| Q8 | 每行独立 salt 还是全局 salt | 每行独立（本轮定稿）。换密码可逐行惰性重加密，某行被解开不影响其它行；代价是每行解密要多跑一次 KDF，用内存缓存 `(credId, salt) → key` 摊掉 |
