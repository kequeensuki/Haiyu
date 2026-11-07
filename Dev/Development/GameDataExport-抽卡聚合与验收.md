# 抽卡记录：聚合、跨会话累积与验收

抽卡记录独立落盘到 `<playerId>/gacha/record.json`，对同一 playerId **跨会话累积**。
它不并入 `game-data.json`，也不来自 TCP 下行——数据由一次 HTTP 查询获得。

协议依据、URL 拼装与网页取数取证见 `GameDataExport-抽卡记录导出调研与方案.md`。

## 1. 为什么不是"被动消费下行"

客户端 `GachaController.GachaHistoryRequest` 在本版本是空实现；"查看抽卡记录"直接打开 webview，
页面再调用 `POST {cn|global}/gacha/record/query`。因此抽卡记录**无法**只靠抓包得到。

本模块因此是全导出管线里唯一的主动请求模块：启动时执行一次查询，其余模块仍然只消费下行。

## 2. 六个请求参数的来源

| 参数 | 来源 |
|---|---|
| `playerId` | 抓包 `BasicInfoNotify`(15380) → 主程序填入 `GameGachaRequest` |
| `recordId` | 抓包 `GachaInfoResponse.record_id`(field 15) |
| `cardPoolId` | 抓包 `GachaInfo.resources_id`(field 12)；接口不校验，可为空 |
| `serverId` | 两个 pak 常量，由**接口探测**二选一（见 §3） |
| `languageCode` | 主程序传入；实测错值会静默返回空列表 |
| `cardPoolType` | 官方页面常量 `GachaPoolTypes.All` |

### serverId 常量出处

`pakchunk1-WindowsNoEditor.pak` →
`Client/Content/Aki/JavaScript/Launcher/Notice/LauncherNoticeDefine.js`：

```js
exports.SERVER_ID_CN     = "76402e5b20be2c39f095a152090afddc";
exports.SERVER_ID_GLOBAL = "86d52186155b148b5c138ceb41be9650";
```

对 pak 内 20953 个脚本做全量检索，这两个值**仅**出现在该文件，没有第三个变体。
它们是版本相关的客户端常量，不是账号凭据。

## 3. 区服判定：以接口实测为准

不猜上下文，而是把两个 serverId 各试一次，按 `code` 分流。

| 场景 | `code` | 含义 |
|---|---|---|
| serverId 匹配，recordId 有效 | `0` | 成功（**即使 0 条**也是成功） |
| serverId 匹配，recordId 失效 | `-1` | 凭证过期；换 serverId 无效 |
| serverId 不匹配 | `-4` | 换另一个常量重试 |

实测（同账号、同 recordId）：

| 组合 | 结果 |
|---|---|
| `SERVER_ID_CN` + 有记录的池 | `code=0`，260 条 |
| `SERVER_ID_CN` + **无记录**的池 | `code=0`，**0 条** |
| `SERVER_ID_GLOBAL` + 任意池 | `code=-4` |

结论：**`code` 与"这个池有没有记录"无关**，只与 serverId 是否为该账号区服有关。
`code=0 && n=0` 是合法命中，绝不能当失败重试——这是最容易写错的一处，已有测试守住。

探测固定用 `cardPoolType=1`，命中时顺带取回该池数据，不额外请求。

### 持久化（每玩家自包含）

`<playerId>/gacha/servers.json`，与抽卡记录同目录——删除该玩家目录即完整清理，无需跨目录查找：

```json
{ "schemaVersion": 1,
  "servers": { "100000001": { "serverId": "76402e5b…", "region": "cn",
                              "languageCode": "zh-Hans", "resolvedAt": "…", "source": "probe" } } }
```

- 命中缓存 → 不探测；
- 缓存值导致 `-4`（换服/合服）→ 删除该条缓存，下次重新探测；
- 缓存文件损坏 → 退化为空缓存（最坏多探测一次），不阻断导出。

## 4. languageCode 是有实际后果的参数

实测（同一账号、同一 recordId、同一池）：

| languageCode | 结果 |
|---|---|
| `zh-Hans` | `code=0`，260 条，标签正常 |
| `zh-Hant` | `code=0`，260 条，标签退化为裸数字 |
| `en-US` / `ja-JP` / `ko-KR` | `code=0`，**0 条** |

**错语言会静默得到空数据**，不是报错。因此不能按"应用界面语言"随意取值，
必须传账号实际使用的游戏语言。模块在未提供时回退 `zh-Hans`。

## 5. 只增语义

抽卡记录没有修改与删除：

- `GachaRecordCollection` 只重写新增路径；`Update`/`Remove` 保留 `DataCollectionBase` 默认行为；
- 适配器不存在全量替换入口，合并不可能因"本次结果为空"而清空本地；
- **旧记录永不被删除**：官方只保留有限时间，过期后本地必须留存，这正是跨会话累积的意义。

### 合并键与 occurrence

```text
cardPoolType, time, resourceId, qualityLevel, resourceType, name, count, occurrence
```

前 7 项与既有 `RecordHelper.AreItemsEqual` 一致。`occurrence` 是同一键在**单次响应内**的序号：
一次十连可能同秒抽到两把同名三星武器，只按前 7 项会少计。

因为 occurrence 由本次响应重新计算，重复拉取同一批数据得到相同键 → **天然幂等**，不产生新版本。

## 6. 损坏文件保护

读取旧 `record.json` 失败（非法 JSON、schemaVersion 不符、IO 失败）时：

- **拒绝写入**并报错，保留旧文件；
- 绝不用新数据覆盖，避免"读不到就当没有"抹掉历史。

## 7. 卡池编号与可读标签

官方页面可查询的 10 个值：`1,2,3,4,5,6,8,9,12,13`，
与既有 `CardPoolType` 枚举（1..11）不同（页面无 7/10/11，独有 12/13）。导出以页面集合为准。

**标签来源**：生产包 `aki/gacha/assets/index-e63ca197.js` 的常量 `C`，
以及 `aki/gacha/locales/zh-hans.json` 的 `selectList`。两处一致，与游戏内唤取界面文案相同。

| 编号 | 标签 |
|---|---|
| 1 | 角色活动唤取 |
| 2 | 武器活动唤取 |
| 3 | 角色常驻唤取 |
| 4 | 武器常驻唤取 |
| 5 | 新手唤取 |
| 6 | 新手自选唤取 |
| 8 | 角色新旅唤取 |
| 9 | 武器新旅唤取 |
| 12 | 角色忆旅唤取 |
| 13 | 武器忆旅唤取 |

接口返回的 `cardPoolType` 是**本地化显示文本**，实测 8/9/12/13 只返回裸数字，
且部分池文本已过期（返回"角色精准调谐"而非页面写的"角色活动唤取"）。
因此：**分类与标签都以请求时的池编号为准**，接口文本仅在官方表未收录时兜底；
未收录编号显示为 `未本地化池(n)`，不推测。

## 8. 验收

`ABIRuntime.TransportTests --game-data-export` 新增 4 项：

| 测试 | 覆盖 |
|---|---|
| `TestGachaServerProbeAsync` | 国服命中即停、缓存跳过、国际服偏好回退、`code=0&&n=0` 合法命中、`-1` 不重试、两次 `-4` 报错 |
| `TestGachaAccumulationAsync` | occurrence 去重、只增不删、重复拉取幂等、**官方池标签**（裸数字 "8" → 角色新旅唤取）、未收录编号标注、JSON/MemoryPack 往返 |
| `TestGachaStoreRecoveryAsync` | 首次无文件、读回一致、损坏与版本不符均拒绝覆盖、缓存解析降级、**每玩家缓存/记录路径** |
| `TestGachaWireParsingAsync` | 成功/空池/`-4`/`-1`/200 折叠/裸数字池文本 |
| `TestGachaModuleWiringAsync` | 组合根注册/目录可选/缺参数不请求/真实落盘路径与标签 |

全部使用可编程假端点，**不请求生产接口**。

## 9. 已知限制

- `recordId` 有效期未实测；过期表现为 `code=-1`，需等新的 `GachaInfoResponse` 刷新；
- 国际服区服仅用常量与 `code=-4` 验证，未在真实国际服账号上跑通全链路；
- 池标签取自官方页面简体中文表；其它语言界面下仍显示中文名（记录本身保留原始数据）；
- 本模块是全管线唯一主动请求者，受用户选择与 `record_id` 可用性限制。
