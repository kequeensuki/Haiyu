# 抽卡记录导出：URL 拼装、网页取数与跨会话累积方案

> 目标：为 Haiyu 游戏数据导出新增"抽卡记录"业务，产物落在 `<playerId>/gacha/`，
> 对同一 playerId 跨会话累积，仅增不删不改。
> 取证方式：官方客户端反编译（`WutheringMod/DecompiledSource`）+ 生产环境网页 bundle 实际下载分析 + 接口实测探测 + **游戏 pak 解密检索**（repak + AES）。
> 取证时间：2026-11-07。基线分支：`feature/waves-kcp-capture`。
> 相关既有实现：`WavesRecordManager`、`RecordHelper`、`CreateForGameContextViewModel`（社区抽卡记录功能，非本导出管线）。

---

## 1. 结论摘要

1. **抽卡记录不在 TCP 协议里。** 客户端 `GachaController.GachaHistoryRequest` 在本版本是**空实现**；"查看抽卡记录"按钮直接打开一个 webview 页面，页面再走 HTTP 拉数据。因此抽卡记录**不能**只靠被动抓包得到。
2. **页面 URL 由客户端拼装**，参数含 `svr_id / player_id / lang / gacha_id / gacha_type / svr_area / record_id / resources_id / platform`（见 §2）。
3. **页面数据来自 `POST {base}/gacha/record/query`**，请求体 6 个字段，响应 `{code,message,data[]}`（见 §3，已实测确认包络）。
4. **六个请求参数全部可闭环**：`playerId` 来自抓包 `BasicInfoNotify`；`recordId`/`cardPoolId` 来自抓包 `GachaInfoResponse`；`serverId` 是 **pak 内硬编码常量**（`SERVER_ID_CN`/`SERVER_ID_GLOBAL`），由 Haiyu 既有区服上下文二选一；`languageCode` 取游戏包语言；`cardPoolType` 为官方页面常量。**无需读游戏日志。**
5. **发现一处池类型差异**：官方页面可选 10 个池（1,2,3,4,5,6,8,9,12,13），与 Haiyu 现有 `CardPoolType` 枚举（1–11）**不一致**；页面有 12/13，Haiyu 有 7/10/11。导出必须按官方页面的集合来查询（见 §6）。
6. **官方记录只保留有限时间**，这正是必须本地跨会话累积的根本原因。

---

## 2. 取证 A：抽卡记录页 URL 的拼装逻辑

证据文件（只读参考，未修改）：`DecompiledSource/Aki.Game/GachaMainView.cs` → `OnRecordBtnClick()`。

拼接结果（`string.Concat(array)`，6 段）：

```text
{prefix}/aki/gacha/index.html#/record?
  svr_id={serverId}
  &player_id={playerId}
  &lang={packageLanguage}
  &gacha_id={gachaInfo.Id}
  &gacha_type={groupId}
  &svr_area={serverArea}
  &record_id={recordId}
  &resources_id={resourcesId}
  &platform={platformStr}
```

各字段来源：

| 参数 | 客户端取值 | 说明 |
|---|---|---|
| `prefix` | `BaseConfigController.Instance.GetGachaUrl().GachaRecord` | CDN 配置下发；观测值为 `https://aki-gm-resources.aki-game.com`（国服）/ `https://aki-gm-resources-oversea.aki-game.net`（国际服） |
| `svr_id` | `LoginModel.Instance.GetServerId()` | 启动器/SDK 选定服务器 ID（hex 串） |
| `player_id` | `PlayerInfoModel.Instance.GetId()` | 玩家 UID |
| `lang` | `LanguageSystem.Instance.PackageLanguage` | 如 `zh-Hans` |
| `gacha_id` | `ProtoGachaInfo.Id` | 抽卡 ID（活动期次） |
| `gacha_type` | `ProtoGachaInfo.GroupId` ⭐ | **= `GachaConfig.RuleGroupId`**，即"池类型"语义编号 |
| `svr_area` | `GachaModel.GetServerArea()` | 非 Global SDK → `cn`；否则 `global` |
| `record_id` | `GachaModel.Instance.RecordId` ⭐ | **只来自 `GachaInfoResponse.record_id`（proto field 15）** |
| `resources_id` | `ProtoGachaInfo.ResourcesId` ⭐ | **来自 `GachaInfo.resources_id`（proto field 12）** |
| `platform` | `KuroSdkModel.GetPlatformStr()` | `PC`（默认）/ `Android` / `iOS` / `Mac` / `PS5` / `XSX` / `WinGDK` / `OpenHarmony` |

⭐ 说明 `gacha_type`：`ProtoGachaInfo` 构造器里 `this.GroupId = GachaConfig.Instance.GetGachaConfig(this.Id).Value.RuleGroupId`，即**池类型由本地配置 `BinData/gacha/gacha.json` 的 `RuleGroupId` 决定，不是服务端字段**。
实测 `gacha.json`：`Id=100001 → RuleGroupId=1`、`Id=2 → RuleGroupId=3`、`Id=5 → RuleGroupId=7`，全表 RuleGroupId ∈ 1..13。

---

## 3. 取证 B：网页如何拿到抽卡数据

### 3.1 抓取对象

生产包（2026-07-08 构建，CommitId `c6201577…`）：

- 页面：`https://aki-gm-resources.aki-game.com/aki/gacha/index.html`（国际服为 `aki-gm-resources-oversea.aki-game.net`）
- 主 bundle：`assets/index-57810756.js`
- 路由表：`record = { path: "/record", name: "Record", component: () => import("./index-e63ca197.js") }`
- 页面组件：`index-e63ca197.js`
- 接口模块：`index-d69ea129.js`

### 3.2 页面组装的请求体

`index-e63ca197.js`（`setup` 内）：

```js
const {svr_id, resources_id, lang, player_id, record_id} = route.query;
return {
  playerId:     player_id,
  cardPoolId:   resources_id,
  cardPoolType: <当前选中的池类型>,
  serverId:     svr_id,
  languageCode: lang,
  recordId:     record_id
};
```

注意：URL 里的 `gacha_type` **只用于选择初始池类型**（`Number(route.query.gacha_type) || 1`）与埋点，**不进入请求体**；
`gacha_id`、`platform`、`svr_area` 同样**不进入请求体**（`svr_area` 只用于选择 API 域名）。

### 3.3 接口调用

`index-d69ea129.js`：

```js
const cn = getUrlQuery().svr_area === "cn";
const base = cn
  ? "https://gmserver-api.aki-game2.com/gacha"
  : "https://gmserver-api.aki-game2.net/gacha";

// 成功判定：code==0 || code==200 || message==="成功"
queryRecord(body) => POST "${base}/record/query", body
                     → 取 response.data.data （即 payload.data）
```

即最终地址：

| 区域 | 实际请求 |
|---|---|
| 国服 | `POST https://gmserver-api.aki-game2.com/gacha/record/query` |
| 国际服 | `POST https://gmserver-api.aki-game2.net/gacha/record/query` |

### 3.4 响应包络（实测确认）

用伪造 `recordId` 实测（匿名请求，不含任何账号信息）：

```json
{"code":-1,"message":"请求游戏获取日志异常!","data":[]}
```

- HTTP 200，业务码在 body 内。
- 成功条件按页面实现：`code==0 || code==200 || message==="成功"`；`data` 为记录数组。
- 单条记录字段见 §3.5。

### 3.5 单条记录字段（既有实现已建模，与本仓库一致）

```json
{ "cardPoolType": "角色活动唤取", "resourceId": 1301, "qualityLevel": 5,
  "resourceType": "角色", "name": "…", "count": 1, "time": "2026-11-01 12:00:00" }
```

本仓库 `Waves.Api/Models/CloudGame/PlayerCard.cs` 的 `Datum` 与之逐字段对应，可直接复用。
`cardPoolType` 在该接口里是**本地化显示字符串**（不是数字）；数字池类型由请求参数决定。

### 3.6 页面不翻页、不分页

页面把 `data` 全量取回，前端按 `pageSize`（默认 5）本地切片。接口本身无分页参数。

---

## 4. 取证 C：服务端下行能提供什么

proto 依据：`src/Extensions/Haiyu.ABI/Protocol/WavesGameData.proto:23939-23945`。

```protobuf
message GachaInfoResponse {
  repeated GachaInfo gacha_infos = 1;
  ErrorCode error_code = 6;
  int32 daily_total_left_times = 14;
  string record_id = 15;          // ★ 历史记录查询凭证
}

message GachaInfo {
  int32 id = 1; int32 today_times = 2; int32 total_times = 3; int32 item_id = 4;
  repeated GachaConsume gacha_consumes = 5; int32 use_pool_id = 6;
  repeated GachaPoolInfo pools = 7; int64 begin_time = 8; int64 end_time = 9;
  int32 daily_limit_times = 10; int32 total_limit_times = 11;
  string resources_id = 12;       // ★ 请求体的 cardPoolId
  int32 gacha_accumulate_id = 13;
  repeated GachaDiscountInfo gacha_discount_infos = 14;
  bool only_view_discount = 15; bool is_show_progress = 16;
  map<int32,string> discount_tag_details = 17;
}
```

消息 ID：`GachaInfoResponse = 18695`、`GachaResponse = 19056`、`GachaResultNotify = 26700`、`GachaNewNotify = 18079`、`GachaDiscountNotify = 26843`、`GachaAccumulateInfoNotify = 19571`、`GachaAccumulateInfoResponse = 20853`。

`player_id` 由既有 `PlayerIdResolver` 从 `BasicInfoNotify(15380).id` 取得，无需新增来源。

---

## 5. 参数来源可用性矩阵（已全部实机验证）

### 5.1 六个请求参数的最终结论

| 参数 | 权威来源 | 验证 | 备注 |
|---|---|---|---|
| `playerId` | 抓包 `BasicInfoNotify.id`(15380) | ✅ 管线既有 | 也可从日志 URL `player_id=` |
| `recordId` | 抓包 `GachaInfoResponse.record_id`(field 15) | ✅ proto + 实测 | 也可从日志抽卡 URL |
| `cardPoolId` | 抓包 `GachaInfo.resources_id`(field 12) | ✅ proto + 实测 | **接口不校验**，可空 |
| `serverId` | **pak 常量** `SERVER_ID_CN`/`SERVER_ID_GLOBAL` | ✅ pak 解密确认 | 见 §5.2 |
| `languageCode` | 游戏当前包语言（`zh-Hans`） | ✅ 实测 | 错值会返回空列表 |
| `cardPoolType` | 官方页面 10 个常量 | ✅ 逐一探测 | 见 §6 |

### 5.2 serverId 来自 pak：`LauncherNoticeDefine.js`

**实测结论**：该值不是服务端下发，也不是从游戏日志抓的，而是**硬编码在客户端 pak 内的启动器脚本常量**。

证据（`pakchunk1-WindowsNoEditor.pak`，AES 解密后）：

```
Client/Content/Aki/JavaScript/Launcher/Notice/LauncherNoticeDefine.js
```

```js
exports.SERVER_ID_CN     = "76402e5b20be2c39f095a152090afddc";   // ★ 国服
exports.SERVER_ID_GLOBAL = "86d52186155b148b5c138ceb41be9650";   // ★ 国际服
exports.NOTICE_PRE_URL_CN     = "https://aki-gm-resources.aki-game.com/aki/announcement/index.html";
exports.NOTICE_PRE_URL_GLOBAL = "https://aki-gm-resources-oversea.aki-game.net/aki/announcement/index.html";
```

**取值逻辑链**（`LauncherNoticeUtils.js`）：

```js
static KZs()  { return "CN" !== BaseConfigController.GetPublicValue("SdkArea"); }
static T11()  { return this.KZs() ? SERVER_ID_GLOBAL : SERVER_ID_CN; }
static S11()  {
  const servers = BaseConfigController.GetCdnReturnConfigInfo()?.LoginServers;
  return (servers && servers.length !== 0) ? (servers[0].id ?? this.T11()) : this.T11();
}
```

即：
1. 优先用 **CDN 下发的 `LoginServers[0].id`**（运行时拉取，AES 存储，非 pak 常量）；
2. 回退到 pak 内置的 `SERVER_ID_CN` / `SERVER_ID_GLOBAL`；
3. 区域判定来自 `KuroPublicConfig.ini` 的 `SdkArea=CN`（国服包）→ **CN**。

**两个常量必须同时硬编码**（国服与国际服各一个，缺一不可）：

| 常量 | 值 | 适用 |
|---|---|---|
| `SERVER_ID_CN` | `76402e5b20be2c39f095a152090afddc` | 国服：官服 `WavesMainGameContextV2` + B 服 `WavesBiliBiliGameContextV2` |
| `SERVER_ID_GLOBAL` | `86d52186155b148b5c138ceb41be9650` | 国际服：`WavesGlobalGameContextV2` |

**实测交叉验证**（同一国服账号，同 `recordId`）：

| serverId | 实测结果 |
|---|---|
| `76402e5b…`（CN） | `code=0`，**返回 260 条记录** ✅ |
| `86d52186…`（GLOBAL） | `code=-4` `当前serverId查询不到区服信息!` |

> 国际服值返回 `-4` **不是因为常量错误**，而是因为该账号在国服区服。
> 这恰好证明二选一正确：**必须按账号所在区服选常量，不能用错**。

> ⚠️ **对之前方案的重要修正**：此前推测"serverId 需从游戏日志解析"，该结论**不成立**。
> 日志中的 `server_id=` 只是同一常量被写进公告 URL 后的回显；pak 才是权威出处，且日志解析在用户未开公告时并不可靠。

### 5.3 判定国服 / 国际服：以接口实测结果为准（首选方案）

**核心思路**：不去猜区服，而是**把两个 serverId 各试一次**，谁能查出数据谁就是对的。

#### 为什么这样最可靠

服务端对三种失败给出的 `code` **完全可区分**（实测）：

| 场景 | `code` | `message` |
|---|---|---|
| serverId 与账号区服匹配，且 recordId 有效 | `0` | `success` |
| serverId 与账号区服匹配，但 recordId 无效/过期 | `-1` | 请求游戏获取日志异常! |
| **serverId 与账号区服不匹配** | `-4` | 当前serverId查询不到区服信息! |

关键实测（同账号、同 recordId、同池）：

| 组合 | 结果 |
|---|---|
| `SERVER_ID_CN` + 有记录的池 | `code=0`，260 条 |
| `SERVER_ID_CN` + **无记录**的池（pool 5） | `code=0`，**0 条** |
| `SERVER_ID_GLOBAL` + 有记录的池 | `code=-4` |
| `SERVER_ID_GLOBAL` + **无记录**的池 | `code=-4` |

结论：

- **`code` 与"有没有记录"无关**，只与 serverId 是否为该账号的区服有关 → `code` 是干净的判据；
- **`code=0 + n=0` 是合法结果**（该池确实没抽过），**不能**据此判定 serverId 错误；
- `code=-4` **只可能**是 serverId 不匹配（recordId 无效时是 `-1`，不会误混）。

#### 探测流程

```text
探测(recordId, playerId, languageCode):
  for sid in [SERVER_ID_CN, SERVER_ID_GLOBAL]:
      r = POST /gacha/record/query { serverId: sid, ... }   // 固定 cardPoolType=1
      if r.code == 0:
          → 命中：serverId = sid，且 r.data 可直接作为首批数据
      elif r.code == -4:
          → 该 sid 不是本账号区服，继续下一个
      elif r.code == -1:
          → recordId 无效/过期，探测无法进行，报错（不重试另一个 sid）
      else:
          → 网络/未知错误，报错
  两次都 -4 → 数据异常，报错（不猜）
```

要点：

- 探测只需 **1 次请求**（命中即返回数据，同时顺带完成首次拉取，不浪费）；
- 探测固定用 `cardPoolType = 1`，命中后的 `data` 就是池 1 的记录，可直接合并；
- `code=0 && n=0` 时仍然 **判定命中**（区服正确，只是该池无记录）——这是最容易被写错的地方。

#### 结果持久化（以 playerId 为 key）

判定结果必须落盘，避免每次会话都探测：

```text
%USERPROFILE%\Documents\Waves\GameDataExport\gacha-servers.json
{
  "schemaVersion": 1,
  "servers": {
    "100000001": { "serverId": "76402e5b20be2c39f095a152090afddc",
                   "region": "cn",
                   "languageCode": "zh-Hans",
                   "resolvedAt": "2026-11-07T19:40:00+08:00",
                   "source": "probe" }
  }
}
```

规则：

| 情况 | 行为 |
|---|---|
| 缓存命中该 playerId | 直接使用，**不探测** |
| 缓存未命中 | 探测 → 成功后写入缓存 |
| 缓存值导致 `code=-4`（如官方换服/合服） | **删除该条缓存**，重新探测一次；仍失败则报错 |
| 同一 playerId 出现在不同区服 | 以后一次探测结果覆盖，并记录 `resolvedAt` |
| 写入失败 | 不阻断本次导出，只是下次重新探测 |

> 缓存文件放在导出根目录（与 `<playerId>/` 同级），不放进 `<playerId>/gacha/`：
> 它是"跨玩家的区服索引"，不是某个玩家的抽卡数据。

#### 为什么还需要上下文判据（作为加速与降级）

探测要花一次网络请求。可以用 Haiyu 既有上下文**先给出首选顺序**，减少无用探测：

三个鸣潮上下文各自绑定独立配置（`GameContextFactory` + `KuroGameApiConfig`）：

| 上下文 | 显示名 | `Config` | `GameID` | 预期区服 |
|---|---|---|---|---|
| `WavesMainGameContextV2` | 鸣潮官服 | `MainAPiConfig` | `G152` | CN |
| `WavesBiliBiliGameContextV2` | 鸣潮B服 | `BilibiliConfig` | `G152` | CN |
| `WavesGlobalGameContextV2` | 鸣潮国际服 | `GlobalConfig` | `G153` | GLOBAL |

现有代码已在用同一判据（`src/WutheringWavesTool/Services/WavesRecordManager.cs:49`）：

```csharp
var url = request.GameContextName == nameof(WavesGlobalGameContextV2)
    ? GlobalRecordUrl : DefaultRecordUrl;
```

**用法**：上下文只用来决定**先试哪个**（命中率极高），失败仍回退到另一个。
上下文不是真相来源，最终以接口 `code` 为准。

**建议实现**：

```csharp
internal enum GachaRegion { Cn, Global }

internal static class GachaServerIds
{
    internal const string Cn     = "76402e5b20be2c39f095a152090afddc";
    internal const string Global = "86d52186155b148b5c138ceb41be9650";

    /// <summary>上下文只决定探测顺序，不作为真相来源。</summary>
    internal static GachaRegion Preferred(IGameContextV2? context) =>
        context is not null
        && (context.ContextName == nameof(WavesGlobalGameContextV2)
            || string.Equals(context.Config?.GameID, "G153", StringComparison.Ordinal))
            ? GachaRegion.Global : GachaRegion.Cn;
}
```

> 语言（`zh-Hans`/`zh-Hant`）与区服**无对应关系**（国际服包默认也是 `zh-Hant`），不可用作判据。

### 5.3.1 缓存位置（已定）

`<playerId>/gacha/servers.json`，与抽卡记录同目录。每玩家自包含，
删除该玩家目录即完整清理，无需跨目录查找。

### 5.4 派生参数

| 参数 | CN | GLOBAL | 说明 |
|---|---|---|---|
| `serverId` | `76402e5b…` | `86d52186…` | 见 §5.2 |
| `svr_area` | `cn` | `global` | 决定 API 域名与页面域名 |
| API 域名 | `gmserver-api.aki-game2.com/gacha` | `gmserver-api.aki-game2.net/gacha` | 与页面 bundle 一致 |
| 页面域名 | `aki-gm-resources.aki-game.com` | `aki-gm-resources-oversea.aki-game.net` | 仅用于展示 |
| `platform` | 固定 `PC` | 固定 `PC` | 不进入请求体 |

### 5.5 实现取舍

两个常量**一起**硬编码，集中一处并写明出处，不在调用点写裸字符串：

```csharp
// 出处：pakchunk1 Client/Content/Aki/JavaScript/Launcher/Notice/LauncherNoticeDefine.js
//   exports.SERVER_ID_CN / exports.SERVER_ID_GLOBAL
internal static class GachaServerIds
{
    internal const string Cn     = "76402e5b20be2c39f095a152090afddc"; // 官服 + B 服
    internal const string Global = "86d52186155b148b5c138ceb41be9650"; // 国际服
}
```

理由：

- 这是**版本相关的客户端常量**，不是账号凭据，也不含个人信息；
- 两个值都必须有——只硬编码 CN 会让国际服账号全部 `code=-4`；
- 出处可复核，将来官方更换时改一处即可。

**失败处理按 `code` 分流，不猜测**：

| `code` | 含义 | 处理 |
|---|---|---|
| `0`（含 `n=0`） | 区服正确 | 命中，正常合并 |
| `-4` | **该 serverId 不属于本账号** | 换另一个常量重试；两个都 `-4` 才报错 |
| `-1` | recordId 无效/过期 | 报错并等待新的 `GachaInfoResponse`；**不要**换 serverId（换也无效） |
| 其他 | 网络/服务异常 | 报错，保留旧文件 |

> `code=0 && n=0` 是**合法命中**（该池确实没抽过），绝不能当成失败重试——
> 这是本方案最容易写错的一处，必须有测试守住。

---

## 6. 取证 D：池类型集合与既有枚举不一致

官方页面 `index-e63ca197.js` 的可选列表（`selectList`）：

| 页面标签 | cardPoolType |
|---|---|
| 角色活动唤取 | 1 |
| 武器活动唤取 | 2 |
| 角色常驻唤取 | 3 |
| 武器常驻唤取 | 4 |
| 新手唤取 | 5 |
| 新手自选唤取 | 6 |
| 角色新旅唤取 | 8 |
| 武器新旅唤取 | 9 |
| 角色忆旅唤取 | 12 |
| 武器忆旅唤取 | 13 |

对比本仓库 `src/Extensions/Waves.Api/Models/Enums/CardPoolType.cs`（`1..11`）：

- 页面**没有** 7（感恩定向）、10、11（联动）；
- 页面**独有** 12、13（忆旅），本仓库枚举缺失。

结论：**导出按官方页面的 10 个值查询**，不复用 `CardPoolTypeValues.All`。
本仓库既有的 `WavesRecordManager` 用 `CardPoolTypeValues.All`（11 个）属于历史偏差，本方案不修改它（超出本次范围），但抽卡导出侧必须用官方集合。

页面另有 `_e` 埋点映射（`gacha_type → 文案`，含 2=角色活动唤取-2、7=感恩定向），那是旧池类型的历史标签表，与可查询集合不同，不用于请求。

---

## 7. 方案设计

### 7.1 产物位置（需求③）

```text
%USERPROFILE%\Documents\Waves\GameDataExport\
  <playerId>\
    game-data.json          （既有：背包/邮件/成就/角色，不变）
    gacha\                  ★ 新增，抽卡记录独占
      record.json           汇总：全部池类型 + 元数据
```

- 抽卡**不写入** `game-data.json`，也不改变其既有字段；
- 抽卡是**独立导出模块**（moduleId `gacha`），可单独启停；
- 不新增时间戳文件、不每池一个文件（避免文件爆炸）。

`record.json` 结构（草案）：

```json
{
  "schemaVersion": 1,
  "playerId": 100000001,
  "updatedAt": "2026-11-07T18:00:00+08:00",
  "query": {
    "serverArea": "cn",
    "serverId": "…",
    "recordId": "…",
    "resourceId": "…",
    "languageCode": "zh-Hans",
    "lastFetchedAt": "2026-11-07T17:59:00+08:00",
    "lastFetchStatus": "ok",
    "lastError": null,
    "fetchedPoolTypes": [1, 2, 3, 4, 5, 6, 8, 9, 12, 13]
  },
  "pools": [
    {
      "cardPoolType": 1,
      "count": 123,
      "firstTime": "2024-01-01 00:00:00",
      "lastTime": "2026-11-01 12:00:00",
      "items": [
        {
          "cardPoolType": 1,
          "poolLabel": "角色活动唤取",
          "resourceId": 1301,
          "qualityLevel": 5,
          "resourceType": "角色",
          "name": "…",
          "count": 1,
          "time": "2026-11-01 12:00:00",
          "occurrence": 0
        }
      ]
    }
  ],
  "totals": { "count": 456, "firstTime": "…", "lastTime": "…" }
}
```

- `poolLabel` 保留服务端返回的本地化显示名（`cardPoolType` 字符串），不参与判定；
- `occurrence` 见 §7.3。

### 7.2 只增语义（需求②）

抽卡记录**没有修改与删除**。据此：

- 领域集合继承 `DataCollectionBase<,\\…>`，**只重写/使用 `Add`**；
- `Update` / `Remove` **保留基类默认行为**（`Update` 缺目标→`MissingTarget`，`Remove` 不存在→`Unchanged`），不额外实现业务语义；
- 适配器**不注册**任何全量替换（`Full`）路径，避免"接口返回空就清空本地"——这正是跨会话累积的反面；
- 失败的拉取**不触碰**已有数据（`lastFetchStatus=failed`，记录保留）。

### 7.3 跨会话累积与去重（需求④）

接口只返回保留期内的记录，且**不保证顺序稳定**；本地必须累积。

合并键（`GachaRecordKey`）：

```text
cardPoolType, time, resourceId, qualityLevel, resourceType, name, count, occurrence
```

- 前 7 项即本仓库既有 `RecordHelper.AreItemsEqual` 的判等条件；
- `occurrence` 是**同键在单次响应组内的出现序号**（0,1,2,…）。
  原因：一次十连可能出两把同名三星武器，`time` 精确到秒且相同，
  只看前 7 项会把两条合并成一条、导致少计。
- 合并规则：对每个键取 **max(已存最大 occurrence, 本次最大 occurrence)**，
  即按多重集补齐，而不是按时间窗口整体替换。

幂等性：重复拉取同一批数据不产生新条目、不产生新文件版本。

读取旧文件失败（损坏/不可解析）时：
**不覆盖**，将本次结果写入 `record.pending.json` 并报错，等待人工或下次成功合并——
避免"读不到旧文件就用新数据覆盖"导致历史丢失。

### 7.4 数据获取流程

```text
1. 抓包收到 GachaInfoResponse(18695)
     → 记录 recordId / 各 GachaInfo.resources_id / 当前 use_pool_id
2. 会话身份已确认（playerId 已知）
3. 查 gacha-servers.json 中该 playerId 的 serverId
     ├ 命中 → 直接使用，跳过探测
     └ 未命中 → 探测（见 §5.3）：上下文定顺序，依次试两个常量
               命中后立刻持久化 { playerId → serverId, region }
4. languageCode 取当前游戏包语言
5. 若 1~4 齐备且未超过冷却（每会话一次）
     → 逐 cardPoolType ∈ {1,2,3,4,5,6,8,9,12,13}
         POST {cn|global base}/gacha/record/query
         携带 {playerId, cardPoolId, cardPoolType, serverId, languageCode, recordId}
6. 成功项合并进 <playerId>/gacha/record.json（只增）
7. 任何一步失败：记录状态，保留旧文件
```

要点：

- **探测与首次拉取合并**：探测用的那一次请求（`cardPoolType=1`）成功即同时得到池 1 数据，不重复请求；
- **探测只在缓存未命中时发生**，正常情形每账号仅一次；
- 缓存 `serverId` 若导致 `code=-4`（换服/合服），删除缓存重新探测一次；
- **全程不读游戏日志**：serverId 由"缓存 → 探测"得到，其余参数来自抓包与应用语言。

不做的事：
- 不在抓包热路径同步发 HTTP；
- 不因 `GachaResultNotify` 单独落盘（它无 record_id、无时间戳、无池归属，只能作为本次会话的即时事件，归 `item-changes` 事件模块既有能力）；
- 不修改 `WavesRecordManager` 的社区记录功能。

### 7.5 分层落点

| 层 | 位置 | 职责 |
|---|---|---|
| 抓包参数来源 | `Haiyu.ABI` `Adapters/Gacha/GachaMessageAdapter.cs` | 只解析 `GachaInfoResponse` 的 18695，产出 `GachaQueryParametersChanged`（recordId/resourcesId） |
| 业务集合 | `Haiyu.ABI` `Domains/Gacha/` | `GachaRecordState` / `GachaRecordCollection`（只增） |
| 区服解析（探测+缓存） | `Waves.Core` `Services/Gacha/GachaServerResolver.cs` | 查缓存 → 未命中则按上下文顺序探测两个常量 → 持久化 `gacha-servers.json` |
| serverId 常量 | `Waves.Core` `Services/Gacha/GachaServerIds.cs` | 两个 pak 常量集中定义 |
| HTTP 拉取 | `Waves.Core` `Services/GachaRecordFetchService` | 复刻 §3.3 请求，逐池拉取，产出 `GachaRecordItem` 列表 |
| 累积合并与落盘 | `Waves.Core` `Services/GachaRecordStore` | 读旧 `record.json` → 多重集合并 → 原子写；失败不覆盖 |
| 触发与生命周期 | `Haiyu.ABI` 新模块 `Modules/GachaRecordExportModule` | 监听 `GachaInfoResponse`，经 Core 回调执行拉取与合并；不在 ABI 内发 HTTP |

> 分层理由：ABI 是 NativeAOT 运行时，无游戏上下文与 HTTP 依赖；HTTP、区服探测与缓存都在 `Waves.Core`。
> ABI 负责"我抓到了什么参数"（recordId/resourcesId），Core 负责"我用哪个 serverId 去拉并合并"。

### 7.6 ABI DTO

- 新增 `ABI.Models/GameGachaData.cs`：`GameGachaData` / `GameGachaPool` / `GameGachaRecord` / `GameGachaQuery`；
- `GameDataExportResult` **尾部追加**可空字段 `[JsonPropertyName("gachaSummary")] GameGachaSummary? GachaSummary`（仅状态与计数，供 UI），不携带全量条目；
- 注册 `ABIJsonContext` 与 `ABIMemoryPack`；
- `GameDataExportScope` 启用预留位 `Gacha = 1 << 2`（原注释位是 `1 << 1`，已被 `AllServerMessages` 占用，需顺延并注明）。

### 7.7 语言与文档

- 四语言（zh-Hans / zh-Hant / en-US / ja-JP）新增：模块名、来源名、计数键、失败状态；走 LangGen 生成流程，不手改 `Assets/Languages/*.json`；
- 本文件即业务协议说明（`Dev/Development/`）。

---

## 8. 边界与显式未知

| 情况 | 处理 |
|---|---|
| 未收到 `GachaInfoResponse` | 无 `record_id`，不拉取，不创建空文件；状态 `missing` |
| 无 `IGameContextV2` | 仍可探测：两个常量都试一遍；结果照常缓存 |
| 探测两次都 `code=-4` | `regionUnresolved`；报错并保留旧数据，**不写空文件** |
| 缓存 serverId 导致 `code=-4` | 判定为换服/合服：**删除该 playerId 缓存** → 重新探测一次 → 仍失败才报错 |
| 接口 `code=0 && n=0` | **合法命中**（该池无记录），合并 0 条，**不得**视为失败或重试 |
| 接口 `code=-1` | recordId 无效/过期：`lastFetchStatus=failed`，保留旧数据；等待新 `GachaInfoResponse` 刷新 |
| 缓存文件写入失败 | 不阻断导出，下次重新探测 |
| 记录超过官方保留期 | 本地已累积的条目**不删除**（这正是累积的意义） |
| 同名同秒重复出金 | 靠 `occurrence` 保留，不合并 |
| 换账号 / 换区服 | `recordId` 与 `serverId` 变化视为新查询上下文；旧数据按 playerId 隔离，不互相污染 |
| 旧 `record.json` 损坏 | 不覆盖，写 `record.pending.json` 并报错 |
| 官方页面池类型再变 | 以 `fetchPoolTypes` 常量集中维护，单点修改 |

---

## 9. 验收计划

1. **领域边界**：只增（重复 Add 幂等、无 Update/Remove 语义）；`occurrence` 多重集合并；十连同名条目不丢。
2. **协议适配**：`GachaInfoResponse` 成功/失败；`error_code` 非 0 时不产生参数变更。
3. **区服探测**：`code=0`（含 `n=0`）判定命中；`code=-4` 切换下一个常量；两次 `-4` 报错；
   探测成功后写入 `gacha-servers.json`；二次启动命中缓存**不再发探测请求**；
   缓存值失效（`-4`）时删除缓存并重探。
4. **HTTP 契约**：用本地 fixture 服务器复刻 `{code,message,data}`；覆盖 `0 / 200 / 成功文案 / -1 / -4`；
   **必须**覆盖 `code=0 && n=0` 不得被判为失败的用例；不得真的请求生产接口。
5. **累积**：会话 A 拉取 100 条 → 会话 B 拉到 80 条（含重叠）→ 结果 100 条；官方记录减少不导致本地减少。
6. **落盘**：`<playerId>/gacha/record.json` 实际文件；原子写失败保留旧文件；损坏旧文件不覆盖。
7. **隔离**：同 playerId 重登、不同 playerId 互不污染。
8. **NativeAOT**：新增 DTO 的 MemoryPack 往返；原生可执行文件实际运行。
9. 新增测试必须在 `Program.cs` 测试表与 `--game-data-export` 筛选器**两处**注册，并出现在 `run_export_checks.py --native` 的 PASS 输出中。

---

## 10. 已解决 / 待确认

**已解决**：

| 原问题 | 结论 |
|---|---|
| `serverId` 出处悬而未决 | ✅ pak 内 `LauncherNoticeDefine.js` 两个常量，见 §5.2 |
| 是否需要读游戏日志 | ✅ 不需要 |
| 国服/国际服怎么判定 | ✅ **以接口实测为准**：两个常量各试一次，按 `code` 判定；结果按 playerId 持久化，见 §5.3 |

**已定决策**：

| 决策 | 结论 | 依据 |
|---|---|---|
| 缓存位置 | `<playerId>/gacha/servers.json` | 每玩家自包含，删除目录即清理 |
| 池标签 | 采用官方页面 10 个中文名 | `index-e63ca197.js` 常量 `C` + `locales/zh-hans.json` 的 `selectList` |

**待确认**：

1. **拉取触发时机**：`GachaInfoResponse` 到达时每会话一次（方案默认），还是导出结束时统一拉一次？

---

## 11. 附：证据索引

| 结论 | 证据 |
|---|---|
| URL 拼装 | `DecompiledSource/Aki.Game/GachaMainView.cs` `OnRecordBtnClick` |
| `gacha_type = RuleGroupId` | `DecompiledSource/Aki.Game/ProtoGachaInfo.cs` 构造器 |
| 页面请求体 | `aki/gacha/assets/index-e63ca197.js`（生产包，2026-07-08 构建） |
| 接口域名/路径/成功判定 | `aki/gacha/assets/index-d69ea129.js` |
| 响应包络 | 实测 `POST /gacha/record/query` → `{"code":-1,...}` |
| `record_id` 来源 | `WavesGameData.proto:23944`；`GachaController.GachaInfoRequest` |
| `resources_id` 来源 | `WavesGameData.proto:23926`；`ProtoGachaInfo` 构造器 |
| **serverId 常量** | `pakchunk1` → `Client/Content/Aki/JavaScript/Launcher/Notice/LauncherNoticeDefine.js`（`SERVER_ID_CN`/`SERVER_ID_GLOBAL`） |
| serverId 取值链 | `.../Launcher/Notice/LauncherNoticeUtils.js` `S11()`/`T11()`/`KZs()` |
| 区域配置 | `pakchunk0` → `Client/Config/Kuro/KuroPublicConfig.ini`（`SdkArea=CN`） |
| Haiyu 区服判据 | `WavesRecordManager.cs:49`；`KuroGameApiConfig.MainAPiConfig/GlobalConfig`（`G152`/`G153`） |
| 双常量实机验证 | 同账号同 `recordId`：CN→`code=0` 260 条；GLOBAL→`code=-4` |
| `code` 与"有无记录"无关 | CN + pool5（无记录）→`code=0, n=0`；GLOBAL + pool5 →`code=-4` |
| 错误码可区分 | serverId 不符→`-4`；recordId 无效→`-1`；成功→`0` |
| 页面池类型集合 | `index-e63ca197.js` `selectList` / 常量 `C` |
| 既有判等条件 | `src/Extensions/Waves.Api/Helper/RecordHelper.cs` `AreItemsEqual` |
