# Lyricify 3.8.8 × YesPlayMusic — int64 歌曲ID修复 / int64 song-id fix

[中文](#中文) | [English](#english)

---

<a id="中文"></a>

## 中文

### 这是什么

Lyricify 3.8.8（已 EOL 的桌面歌词软件）在配合 [YesPlayMusic](https://github.com/qier222/YesPlayMusic) 播放 **2023 年以后的新歌**时显示"暂无歌词"或完全不识别曲目的修复补丁。

**不需要改动 Lyricify 或 YesPlayMusic 的任何文件**，只替换/新增 Lyricify 目录下 2 个 DLL 和 1 处配置。

### 根因

Lyricify 内部的 YesPlayMusic 曲目模型 `Track.Id` 字段是 **32 位 int**（上限 2,147,483,647）。网易云 2023 年后新歌的 ID 已经超过这个值（例如 3,399,723,492），导致 Newtonsoft.Json 解析播放信息时直接抛出 `JsonReaderException`，整首曲目的信息（歌名、歌手、时长）全部丢失，歌词流程根本不触发。老歌 ID 小，所以一切正常。

修好解析后还有第二层问题：Lyricify 拿着溢出回绕后的错误 ID 去网易云 API 查歌词，API 返回占位内容 `"[00:00.00]暂无歌词"`。因此本修复同时做了**应答修复**：检测到占位/空歌词应答时，实时向 YesPlayMusic 询问当前曲目的真实 int64 ID，用真实 ID 取回正确歌词并原位替换。

### 安装

1. 完全退出 Lyricify（含托盘图标）
2. 把 `bin/` 里的两个 DLL 复制到 Lyricify 安装目录（默认 `C:\Program Files (x86)\Lyricify` 或自选目录），**替换**原有的 `Newtonsoft.Json.dll`
3. 在 Lyricify 目录的 `Lyricify.exe.config` 中，`<runtime>` 段里加入：

```xml
<assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
  <dependentAssembly>
    <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
    <bindingRedirect oldVersion="0.0.0.0-13.0.0.1" newVersion="13.0.0.1" />
  </dependentAssembly>
</assemblyBinding>
```

或者直接运行 `scripts/install-fix.bat`（脚本内路径按你的安装目录修改）。

### 验证

播放一首 2023 年后发行的新歌，Lyricify 应正常滚动歌词。日志在 `%APPDATA%\Lyricify\lyricify_hook.log`，出现 `repaired lyric json for id <数字>` 即修复生效。

### 回滚

运行 `scripts/rollback.bat`，或手动：还原原始 `Newtonsoft.Json.dll`（备份在 `bin/orig-backup.md` 说明中）、删除 `HookTemplate.dll`、移除 config 里的 bindingRedirect。

### 原理（给想改代码的人）

- `src/Patcher.cs`：用 [dnlib](https://github.com/0xd4d/dnlib) 对原始 `Newtonsoft.Json.dll`（13.0.0.x）打 5 个 IL 补丁：
  1. `JsonTextReader.ParseReadNumber` — Int32 溢出分支改为调用 hook 回绕并记录（替换抛异常）
  2. `JsonReader.ReadAsInt32` — `Convert.ToInt32` 调用替换为安全版本
  3. `ConvertUtils.ConvertOrCast` — 包装一层：目标是 int 且源是超范围 long 时走回绕
  4. `JToken.op_Explicit(int)` — 同上替换
  5. `JsonConvert.DeserializeObject(string, Type, JsonSerializerSettings)` — 入口插入 `MaybeRepairLyricResponse` 应答修复
  - 同时把程序集版本改为 13.0.0.1：强名程序集的 NGEN 原生缓存按完整标识匹配，版本一变即失效，确保补丁 IL 被真正 JIT
- `src/HookTemplate.cs`：hook 实现（强名签名 + `[SecurityTransparent]`，因为 Newtonsoft 标记了 APTCA/Level2 透明，只有透明代码能被它调用）。溢出 ID 回绕进 64 槽环形缓冲；`MaybeRepairLyricResponse` 识别歌词形状 JSON 中 `lrc.lyric` 为空或为网易云占位符 `[00:00.00]暂无歌词` 的应答，向 `127.0.0.1:27232/player` 取当前曲目真实 ID（`currentTrack` 到第一个 `"ar"` 之间的第一个 `id` 字段），再从 `127.0.0.1:10754/lyric?id=<真实ID>` 取回正确歌词 JSON 原位替换。附带一个本地 relay + WebRequest 代理（对应答修复非必需，保留作备用诊断通道）。
- 重建命令见 `src/BUILD.md`。

### 重新编译

需要 .NET Framework 的 csc（C# 5 语法即可编译）和 dnlib 4.4.0。注意：hook 的签名密钥（.snk）**不随仓库分发**——重新编译时用 `sn -k` 生成自己的密钥对，并用 `Patcher.cs` 中 `hook.pub` 的提取逻辑改为读取你自己的公钥（或直接用完整公钥字节数组）。

### 已知限制

- 仅适配 Lyricify 3.8.8 + Newtonsoft.Json 13.0.0.x；其他版本需自行确认补丁点的 IL 形状
- 纯音乐（网易云标记 `pureMusic`）本来就没有歌词行，显示"纯音乐，请欣赏"是正常的
- YesPlayMusic 需在运行中（本地 API 端口 27232/10754 由它提供）

### 许可

- 本仓库代码：MIT
- 补丁修改的 Newtonsoft.Json：MIT，见 `licenses/LICENSE-Newtonsoft.txt`（MIT 允许修改和再分发）
- 不分发、不修改 Lyricify / YesPlayMusic 本体

---

<a id="english"></a>

## English

### What is this

A fix for Lyricify 3.8.8 (an EOL desktop-lyrics app) failing to show lyrics for **post-2023 NetEase Cloud Music songs** when used together with [YesPlayMusic](https://github.com/qier222/YesPlayMusic): it either shows nothing or "暂无歌词" (no lyrics available).

No files of Lyricify or YesPlayMusic are modified — only 2 DLLs in the Lyricify folder are replaced/added, plus one config section.

### Root cause

Lyricify's internal YesPlayMusic track model declares the song `Id` as a **32-bit int** (max 2,147,483,647). NetEase song IDs for newer releases exceed that (e.g. 3,399,723,492), so Newtonsoft.Json throws a `JsonReaderException` while parsing the player state (`http://127.0.0.1:27232/player`) and the whole track info is lost — the lyrics pipeline never fires. Old songs have small IDs, which is why they kept working.

A second layer: once parsing is fixed, Lyricify queries the NetEase API with the *wrapped negative* id and gets a placeholder `"[00:00.00]暂无歌词"`. So the fix also does **response repair**: when a placeholder/empty lyric response is detected, it asks YesPlayMusic for the current track's real int64 id, fetches the correct lyric JSON with it, and swaps it in place.

### Install

1. Fully exit Lyricify (including tray icon)
2. Copy both DLLs from `bin/` into the Lyricify install folder, **replacing** the original `Newtonsoft.Json.dll`; also add `HookTemplate.dll`
3. Add to the `<runtime>` section of `Lyricify.exe.config`:

```xml
<assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
  <dependentAssembly>
    <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
    <bindingRedirect oldVersion="0.0.0.0-13.0.0.1" newVersion="13.0.0.1" />
  </dependentAssembly>
</assemblyBinding>
```

Or run `scripts/install-fix.bat` (edit the path inside first).

### Verify

Play a recent song; lyrics should scroll normally. Log: `%APPDATA%\Lyricify\lyricify_hook.log` — look for `repaired lyric json for id <n>`.

### How it works

See the 中文 section above (`src/Patcher.cs` patches 5 IL sites with dnlib and bumps the assembly version to 13.0.0.1 to invalidate the NGEN cache; `src/HookTemplate.cs` wraps overflow ids and repairs placeholder lyric responses using the live track id from YesPlayMusic's local API). Rebuild instructions in `src/BUILD.md`.

### Known limitations

- Targets Lyricify 3.8.8 + Newtonsoft.Json 13.0.0.x only
- Instrumental tracks legitimately show "纯音乐，请欣赏" (no lyric lines)
- YesPlayMusic must be running (it serves the local API on ports 27232/10754)

### License

- Code in this repo: MIT
- Patched Newtonsoft.Json: MIT (see `licenses/LICENSE-Newtonsoft.txt`)
- Lyricify / YesPlayMusic binaries are not distributed or modified
