# 移交：CS2 player 模式托管 BOT 被算进原生投票分母

交接对象：Fable 5.1  
交接日期：2026-09-05  
服务器主人：xiaoyueyoqwq  
当前结论：**BotIdentity 0.1.3 + BotVoteFix 1.1.2 已在线上被证伪。身份字段清零这条路停机。不要继续盲写 offset。**

本文只记录已验证事实、证伪项、约束和下一步允许的研究方向。不要把文中的「未验证」当成补丁清单。

配套仓库：https://github.com/xiaoyueyoqwq/CS2-Bot-Identity/tree/handover/fable-5.1

---

## 1. 问题

目标：CS2 原生投票（`callvote` / Panorama 投票 UI）的法定人数只算真人，不算托管 BOT。

现状：BOT 以 **player 身份伪装** 运行（记分牌像人、有合成 SteamID）。Valve 把它们算进 `m_nPotentialVotes`。1 个真人投 yes，投票仍按「1 / (1+N bots)」走 50% 门槛，约 20.2s 静默超时，没有 `vote_passed` / `vote_failed`，只有 BotVoteFix 的 `RefreshVote: controller vanished`。

CS2 没有公开的 `CVoteController::IsValidVoter` / `CBaseIssue::CountPotentialVoters` 源码。hl2sdk-cs2（`/tmp/bothider-deps.tPWfkO/hl2sdk-cs2`）里也没有投票实现。唯一完整参考是 **CS:GO** `cstrike15_src/game/server/vote_controller.cpp`：release 构建里 `SV_VOTE_IGNORE_BOTS` 恒为 true，`IsValidVoter` 排除 `IsBot()` / `IsFakeClient()` / HLTV / Replay，再按队过滤。我们按这套模型改过 CS2，**这台服务器上不成立**。

---

## 2. 硬约束（违反即停）

这些是主人的长期约定，不是建议。

1. **始终中文** 向主人汇报。
2. **先读再改。** URL / issue / PR / 手册 / 文件路径必须先读完再动代码。
3. **非平凡改动先出计划，等人批准。** 多文件/多函数实现不要直接写。
4. **不要自评、不要预庆祝。** 验证前禁止「完美」「现在应该可以了」。
5. **失败就停。** 同一策略连试三次算噪音。报告实际日志行，换策略或问主人。
6. **未经批准不改运行时。** 有玩家在线时禁止停服、换图、改 CVar、热重载、替换插件。手册优先于「我想测一下」。
7. **Native Metamod 插件不能热重载。** 换 `.so` 必须空服或授权窗口 + 全量 CS2 重启（`stop.sh` / `start.sh`）。
8. **禁止查看、引用、移植、部署 BotHider 的 native 源码**（Issue #30，维护者敌对）。仓库在 `/home/xiaoyueyoqwq/project/CS2-Bot-Hider/`。**可以读** BotHiderImpl 的 CSS、以及 BotHider 的 gamedata JSON / configs。**跳过** native `cpp` / `.so`。
9. **不要动这些有意保留的行为：** 双名字层、difficulty 2、BotAI 跳过的补丁、已恢复的低配 `botprofile.vpk`。
10. **手册绑定：**
    - `/home/xiaoyueyoqwq/文档/CS2_服务器管理员维护手册_v1.0.md`
    - `/home/xiaoyueyoqwq/文档/CS2_服务器连接与维护手册.md`
    - SSH：`ssh cs2`，登录 `admin`，运行用户 `steam`，`sudo -n` / `sudo -n -u steam`
    - 部署：空服或授权 → SHA-256 → 先放到 `/tmp` 再 `steam` 用户 `cp`（不要从 admin 暂存 `cp -a`）→ 备份 `/home/steam/recovery/` → `stop.sh`/`start.sh`
11. Pin `CounterStrikeSharp.API` **1.0.371**。更新的 NuGet 可能丢掉当前运行时兼容。
12. Native 投票行为 **跟 CS2 构建绑定**。换版本必须在那一版上重测 `CVoteController` 写入。

---

## 3. 仓库与线上位置

| 角色 | 路径 |
|---|---|
| CSS 投票插件（本仓库） | `/home/xiaoyueyoqwq/project/CS2-Vote-Improver/` |
| Native 身份插件 | `/home/xiaoyueyoqwq/project/CS2-Bot-Identity/` |
| 构建 BotIdentity | `CS2-Bot-Identity/build`，Release，`/usr/bin/c++`，CMake |
| 构建 BotVoteFix | `dotnet build -c Release`，输出 `bin/Release/net10.0/BotVoteFix.dll` |
| 线上 BotIdentity | `/home/steam/cs2-server/game/csgo/addons/BotIdentity/` |
| 线上 BotVoteFix | `/home/steam/cs2-server/game/csgo/addons/counterstrikesharp/plugins/BotVoteFix/BotVoteFix.dll` |
| 共享内存 API | `/dev/shm/CS2BotHider_Slots`（名字历史遗留；能力名是 `botidentity:api`） |
| 回滚包 | `/home/steam/recovery/pre-botidentity-0.1.3-20260905-201113` |

不要覆盖线上 `config.json` / `bots.json`，除非主人明确要求。`voteTransactionHoldFrames: 3` 已在线上 config 里。

本分支 `handover/fable-5.1` 是交接快照。不要把失败的身份清零再合进 `main` / `master` 当已修好。

---

## 4. 当前线上状态（2026-09-05 20:11 部署，20:23/20:24 测失败）

进程：PID `1370667`，`de_dust2`，`+game_type 0 +game_mode 1`（交接时仍在跑，以现场 `pgrep` 为准）。

| 组件 | 版本 | SHA-256 | 大小 |
|---|---|---|---|
| `BotIdentity.so` | 0.1.3 | `bdfd71b2d751a7dab68432668376aaf82f5cc1b8d9b1c781e41788aee32aa158` | 249848 |
| `gamedata.json` | 注释更新，offset 键未改 | `0643b4601fecc3615315892addcfc8da42b646fe9663d76d90e003638316c933` | — |
| `BotVoteFix.dll` | 1.1.2 | `21c6ada7902ce737863c429dbeef3daa2ec7f90088fde969f51efbf167601a76` | 17920 |

启动行应含：`version=0.1.3 … voteHoldFrames=3 ctrlSteamIdWrite=scan`  
`meta list`：`BotIdentity (0.1.3)`  
`css_plugins list`：`Bot Vote Fix (1.1.2)`  
`botidentity:api available`

仍加载着遗留 **BotHiderImpl**（CSS）。它会按 2s / 0.25s 窗口用 Schema 写回 controller `m_steamID`，并在投票时给托管 BOT 打 HUD 自动 yes。`auto_vote_for_managed_bots=false` 配了也还会打。**自动 yes 不是超时原因**（yes 票足够过任何含 bot 的池子；超时是因为 Valve 内部法定人数仍把 bot 算进去）。

---

## 5. 系统怎么叠在一起

```
真人 callvote
  → ICvar::DispatchConCommand Pre  ("callvote")
      BotIdentity BeginVoteTransaction
        每个托管 slot：
          SetFakePlayer (SSC m_bFakePlayer=1，改 connection flags)
          SetControllerFakeClientFlag (m_fFlags |= 0x100)
          扫描 SSC[0,2048) 和 controller[0,4096)，把伪装 SteamID64 的 uint64 拷贝清零（最多 8 个）
          WriteSteamId(client, 0)
          探测 GetClientXUID / GetClientSteamID / GetPlayerNetworkIDString
  → Valve 处理 callvote，发出 vote_options（同步，在 CountPotentialVoters 之前——这是 CS:GO 顺序，CS2 未证实同源）
      BotVoteFix OnVoteOptions（同步，不是 NextFrame）
        Schema.SetSchemaValue(controller, CBasePlayerController.m_steamID, 0)  不 SetStateChanged
        开始 0.10s refresh：重清 Schema SteamID（对抗 BotHiderImpl），改 CVoteController 字段（只是 HUD/日志探针）
  → DispatchConCommand Post
      ScheduleVoteTransactionEnd(3 GameFrame)
  → GameFrame_Post × 3：Reassert 标记；第一拍再探测一次引擎身份
  → Valve 第一次 Think 写 m_nPotentialVotes   ← 0.1.3 时这发生在 hold remaining=2
  → 3 帧结束：把伪装写回去（ClearFakePlayer、SteamID 拷贝、清 0x100）
```

日常伪装（`ApplyDisguise`，连接时）：`ClearFakePlayer` + `WriteSteamId(synthetic)` + 清 controller `0x100`。**不写** controller Schema `m_steamID`（那是 BotHiderImpl CSS 在写）。  
断开 `RestoreIdentity`：`SetFakePlayer` + `0x100` + `WriteSteamId(0)`。同样不写 Schema `m_steamID`。

CSS `CCSPlayerController.IsBot` = `m_fFlags & FL_FAKECLIENT`。BotHiderImpl 还有 Harmony postfix，只影响 C#，Valve 看不见。  
player 模式 **从不拆** pawn 上的 `CCSBot*`。

SteamID64 公式：`76561197960265728 + accountid`，和 userinfo `[U:1:accountid]` 对得上。

---

## 6. 试过什么、结果是什么

| 版本 | 做法 | 结果 |
|---|---|---|
| BotVoteFix 早期 | 写 `CVoteController.m_nPotentialVotes` / option counts / `SetStateChanged` | 字段被 Valve 盖掉。日志里能看到 `potential 10 -> 1`，内部法定人数不变 |
| 诊断：`CBaseIssue` 指针、`ProcessResults` vtable 22 | 未在本机构建上验证 | **已回滚**。构建敏感，不要当现成补丁捡回来 |
| 0.1.0 `114b355` | `callvote` dispatch 窗口内恢复 native 标记，dispatch 返回就关 | `g_pCVar` 未赋值崩溃（已修）。修好后窗口关得太早：`vote_options` 时 potential 仍是 0，几毫秒后 Valve 写 4/5 |
| 0.1.1 | dispatch 后再 hold **3** 帧，覆盖 Valve 第一次 Think；只改 fake 标记，不改 SteamID | Think 时仍写 `potential 4`（CT：1 人 + 3 伪装 CT bot）。**不要再加 hold** |
| 0.1.2 | hold=3 + SSC SteamID=0 + 无条件写 controller **+1800** | 换图 `potential 6`。`ctrl_sid=3977426905120`（`0x39e111e2820`）是堆指针，不是 SteamID64。**+1800 是错字段** |
| 0.1.3 + BotVoteFix 1.1.2 | 停写 +1800；扫描清 SteamID64 拷贝；CSS Schema `m_steamID=0`（真 offset **2528**）；探测 XUID/netid | **换图失败。停机条件打中。** |

### 6.1 0.1.3 决定性日志（2026-09-05 20:23:12，换图 `team=-1`）

1 真人 Yanhang slot=0 + 9 托管 bot。证据文件（cs2 主机）：

- CSS：`/home/steam/cs2-server/game/csgo/addons/counterstrikesharp/logs/log-BotVoteFix20260905.txt`
- 控制台抓取：`/tmp/vote013-capture.txt`、`/tmp/cs2-console-vote013.log`

Native（每个 bot 同类）：

```
native bot identity restored slot=1 ssc_sid=76561197961483905->0 ctrl1800=2535156297920 copies=5 offs=s171,s179,s432,s440,c2528 controller=ok
identity probe when=begin slot=1 xuid=0 engine_sid=0 netid='BOT' controller=0x24e66943800
vote transaction: holding native identity for 3 frames
identity probe when=hold slot=1 xuid=0 engine_sid=0 netid='BOT' ...
hold tick remaining=2
```

CSS：

```
schema m_steamID offset=2528
schema steamid snapshot slot=1 handle=0x24E66943800 steamid=0 restore=76561197961483905 netid=[U:1:1218177]
SetStateChanged: potential 0 -> 1, eligible 1/10
vote_cast: voter=Yanhang slot=0 option=0 team=-1
SetStateChanged: potential 10 -> 1, eligible 1/10     ← Valve 在 hold remaining=2 时写了 10
… slots 1–9 HUD auto-yes …
20:23:32.959 RefreshVote: controller vanished          ← ~20.2s，无 vote_passed
```

没有 `steamid reappeared`。controller 指针和 CSS `player.Handle` 一致。`c2528` 就是 Schema `m_steamID`。

20:24:18 又测了一次 **队内** 投票（`team=3`）：Valve 写 `potential 5`，同样 20.2s 超时。不是换图，但同样说明清零没有缩小池子。

### 6.2 误判（不要再犯）

2026-09-05 13:53 T 侧暂停：`potential 2`（Yanhang + T bot Weicheng Zhang），1/2 @ 50% 约 6s 过了。主人一度以为修好了。**这是队内投票数学，不是 SteamID 排除。** 有效测试只有：

- `changelevel` / 任何 `team=-1`，或
- 同队至少 3 个 bot 的队内投票（1 人 yes 达不到 50%）

8 月 28 日「`native_bot` 能过、`player` 超时」是主人 A/B 的文字记录，**没有**带 `potential=1` 的换图日志。在 13:53 那种误判之后，不能再把那次 A/B 当成已证实。

---

## 7. 已证伪（不要再做）

在 Valve **正在写** `potential=10` 的同一毫秒，下面这些已经是 bot 形态，池子仍是 1+9：

1. `CVoteController` 字段改写（BotVoteFix 的 `eligible 1/10` 被忽略）
2. SSC `m_bFakePlayer` + connection flags + controller `FL_FAKECLIENT`（0.1.1 已盖住 Think）
3. SSC SteamID / SteamIDMirror = 0
4. 扫描到的额外 uint64 拷贝：`s171,s179,s432,s440,c2528`
5. CSS Schema `CBasePlayerController::m_steamID` = 0（真 offset **2528**，不是 gamedata 的 1800）
6. `IVEngineServer::GetClientXUID` = 0
7. `IVEngineServer::GetClientSteamID` = 0
8. `IVEngineServer::GetPlayerNetworkIDString` = `'BOT'`

因此 **不要**：

- 增加 `voteTransactionHoldFrames`
- 再扫、再清另一个 live uint64 SteamID
- 再无条件写 controller +1800
- 发明 userinfo protobuf `xuid` offset 当补丁
- 再捡 `ProcessResults` vtable index 22（未验证，已回滚）
- 用队内暂停当成功标准
- 为了投票去改双名字 / difficulty / BotAI / botprofile.vpk
- 读或移植 BotHider native cpp/.so
- 再改 BotIdentity 的 `vote_transaction` 当投票修复

gamedata 里 `CBasePlayerController::m_steamID = 1800` **是错的**（schema dump 残留）。线上 Schema.GetSchemaOffset 和 native 扫描都给出 **2528**。CSS 的 `gamedata.json` 也没有 `m_steamID` 键。

0.1.3 的 hold 探测 **没打印** `m_bFakePlayer` / `m_fFlags` 当时的值。这是测量缺口，不是加 hold 的理由：0.1.1 已经在 Think 窗口里持有过 fake 标记，仍然失败。

---

## 8. 看到了、但没接到投票函数上

CSS `player.NetworkIDString` 在 Schema SteamID 已是 0、引擎 netid 已是 `BOT` 时，仍是 `[U:1:accountid]`。

这是两个存储。**没有证据表明 Valve 的计票读 CSS 这一侧。** 在定位 `CountPotentialVoters` / 等价函数之前，去清 `[U:1:…]` / `m_szNetworkIDString` 仍是盲写。

---

## 9. 当前判断（有依据的部分 vs 未知）

**有依据：** 这台 CS2 建池时，不用我们在 3 帧窗口里改过的那些身份字段。player 模式就是让 bot 看起来像已连接的人；如果投票走同一套「已连接控制器」视图，短暂改回 bot 标记不会改变分母。C# 层改 `CVoteController` 从来没有驱动过内部法定人数。

**未知（因为没有 CS2 投票源码）：**

- CS2 是否还存在 `IsValidVoter`，以及它读什么
- 池子是在 `callvote` 当时建，还是连接时快照，还是每次 Think 重算（我们只知道 `m_nPotentialVotes` 在 hold 第 2 帧被写成 10）
- 8 月 28 日 `identity_mode=bot` 在 **换图** 上是否真的把池子缩到 1

**因此下一步如果还做，只允许研究，不允许再赌字段：**

1. **分叉测量（仍不算猜，但要主人授权，因为可能改身份模式）：** 空服或授权后，用 `identity_mode=bot`（真 fake-client，不伪装成人）打一次 **换图**。  
   - `potential` 变成 1 → CS2 仍在滤真 bot，只是 0.1.3 没还原到它读的那一位。然后才有理由在 `libserver` 里对比「真 bot 对象 vs 我们 hold 时的对象」。  
   - `potential` 仍是 1+bots → 这条 CS2 已经不滤 bot，身份交易整条作废。
2. **读函数，不要猜字段：** 在对应 CS2 构建的 `libserver.so` 里定位 `CountPotentialVoters` / `IsValidVoter` / `callvote` 处理，把谓词读出来。读出来之前没有补丁。
3. **产品分叉（需主人拍板，不是技术猜测）：** 放弃原生 `callvote` 分母，改用插件自建投票池（例如 PanoramaVote 自己按 `!IsBot` 计）。原生 `callvote` 会继续坏。

投票修复 **不要再改 BotIdentity 的 vote_transaction**。BotIdentity 仍负责日常伪装和 `botidentity:api`，不要卸载。

---

## 10. 关键源文件（先读这些）

BotIdentity（`handover/fable-5.1`）：

- `src/vote_transaction.cpp` / `vote_transaction.h` — 交易、扫描、探测
- `src/ssc_ops.h` — 偏移与 fake/SteamID 写入；`WriteControllerSteamId` 已定义但投票路径不用
- `src/plugin.cpp` — `DispatchConCommand` Pre/Post、`GameFrame_Post` 里 `TickVoteTransaction`、`ApplyDisguise` / `RestoreIdentity`
- `src/plugin.h` — 版本 `"0.1.3"`
- `gamedata.json` — 1800 条目有「不要无条件写」的注释
- `config.json` — `voteTransactionHoldFrames: 3`
- `HANDOVER-FABLE-5.1.md` — 本仓库短指针

BotVoteFix（本分支）：

- `BotVoteFix.cs` — 1.1.2；`vote_options` 同步清 Schema SteamID；refresh 重清；结束时恢复；`CVoteController` 写入只是探针
- `BotVoteFix.csproj` — CSS API 1.0.371，net10.0，引用 `CS2-Bot-Identity/csharp/BotIdentityApi`
- `README.md` — 已写明换图才是测试，CVoteController 改写无效

BotHiderImpl（可读 CSS，勿读 native）：

- 线上仍在跑；会写回 Schema SteamID；HUD 自动 yes
- Harmony `IsBot` postfix 只影响 C#

---

## 11. 运维速查

只读：

```bash
ssh cs2 'sudo -n -u steam tmux capture-pane -t cs2 -p -J'   # 控制台；-J 拼回被 wrap 的启动行
ssh cs2 'pgrep -a -u steam -x cs2'
```

控制台（有人在线只读）：`status` / `meta list` / `css_plugins list` / `version`

回滚 0.1.3（需空服或授权 + 全量重启）：

```text
restore /home/steam/recovery/pre-botidentity-0.1.3-20260905-201113
  含 0.1.2 .so (8b1b0f60…)、gamedata (eaca5036…)、BotVoteFix 1.1.1 (74a64ef5…)
然后 stop.sh / start.sh
```

成功标准（如果以后还有补丁）：换图，1 真人 yes，Valve 自己写的 `potential` 必须是 **1**（不是 BotVoteFix 改过的 1），然后 `vote_passed`。`SetStateChanged: potential 10 -> 1` 不算成功。
