# GameDataExport 道具通知接入与验收

## 范围与依据

依据用户提供的《鸣潮服务器道具新增通知机制调研.md》，并核对 WutheringMod `48026711997617dbe86f266c948573efcae94c87` 的只读 `DecompiledSource`。Haiyu 开发基线为 `b2e6e042`。该源码版本与现有协议生成 manifest 的 sourceCommit 一致，所需消息均已在生成的 schema 和 ID 表中，无需手工修改或重新生成 proto。

文档中的声骸操作 ID `16448 / 18169 / 21501` 是请求 ID。下行响应分别是 `PhantomLevelUpResponse = 22489`、`PhantomIdentifyResponse = 21034`、`PhantomVicePolishAckResponse = 22926`，以 `Aki.Protocol/EMessageId.cs` 和响应类型的 NetMessage 特性为准。

## 背包快照

继续通过已有的 `inventory` 模块生成 `{playerId}/game-data-*.json`，四种全量响应是各自来源的基线。新增适配如下：

| 来源 | 下行消息 | 投影语义 |
|---|---|---|
| 普通道具 | NormalItemAdd/Update/RemoveNotify | Id 为键；Add 只添加不存在的键，Update 替换已有对象的绝对数量，Remove 删除指定 Id；Update 保留原过期时间 |
| 武器 | WeaponItemAdd/RemoveNotify | IncrId 为键；添加与删除实例，不能按配置 Id 合并多把武器 |
| 限时道具 | ValidTimeItemAdd/Update/RemoveNotify | (Id, IncrId) 为键；Update 替换数量和过期时间，Remove 使用 (ItemId, IncrId) |
| 声骸 | PhantomItemAdd/RemoveNotify | IncrId 为键；新增实例或替换已有堆叠对象，已有编号且 StackCount 缺失/为 0 的 Add 不清空原对象 |
| 声骸 | PhantomItemUpdateNotify | 批量完整覆盖对象，StackCount 为 0 删除实例 |
| 声骸 | PhantomLevelUpResponse | 成功时先应用 StackSplitInfo 的操作对象和剩余对象，再覆盖 UpdateInfo；ItemMap 是返还展示，不重复累加普通道具 |
| 声骸 | PhantomIdentifyResponse / PhantomVicePolishAckResponse | 成功时用 UpdateInfo 完整覆盖，包含清空旧词条；失败响应不改写状态 |

证据位置：

- `Aki.Game/InventoryController.cs`：普通道具 379–521；限时道具 539–644；武器 647–720；声骸 723–850；堆叠拆分/合并 854–915。
- `Aki.Game/InventoryModel.cs`：NewCommonItemData 319–338（已有键不重复新增）；NewWeaponItemData 539–549；UpdatePhantomItemData 608–635。
- `Aki.Game/PhantomBattleController.cs`：升级 122–164；服务器完整更新 443–466；调谐 469–507。
- `Aki.Game/CalabashController.cs`：洗练确认 611–638。
- `Aki.Game/PhantomStackUtil.cs`：IsUpdateInfoRemoved 判定 StackCount == 0。
- `Aki.Game/PhantomBattleModel.cs`：NewPhantomBattleData / UpdatePhantomBattleData 234–257；PhantomLevelUpReceiveItem 438–455（返还展示）。

声骸 JSON 兼容保留原字段，追加 `unAckSubProp`、`lockPropIndex`、`addTime`、`funcValue`。升级或调谐响应是完整对象，不能只合并非零字段，否则会残留已清空的词条或锁定槽位。UI 与高权限 ABI 交换模型一并发布。

## 顺序、隔离与输出

- `InventoryMessageProjection` 只负责解释消息与实例键；`InventoryExportModule` 负责会话状态、大小预算和快照调度。
- 通用 `SourceState.ApplyOrdered` 要求已有可用基线和来源消息索引。事件索引由已有串行协调器产生，只维护本地版本，不将其冒充服务器业务版本。重复/倒序索引不应用，不改变已有的带服务器版本 Delta API。
- 基线前的增量不会组成完整快照，也不排队重放。后来的全量可能已经包含这些变更，盲目重放会覆盖更近状态；完整原始事件可通过事件模块保留。
- 新登录仍建立独立来源状态；旧连接、未验证连接和非当前登录消息不能修改当前背包；沿用 playerId 路径和写入所有权隔离。
- 相同更新值、重复新增和删除不存在的键不制造新版本。全量重复标识或不合法增量会使来源等待新的全量，保留上次成功文件。
- 聚合值保持不可变，候选及其写入回执机制不变。32 MiB 来源预算改为估算保留字段树大小，避免用一条小 Notify 的长度低估整个背包；估算不是 CLR 堆精确大小。仍有每来源 100000 项和 4096 个登录状态的限制。

## 道具与角色变更事件模块

在组合根注册可选模块 `item-changes`，界面自动出现“道具与角色变更事件（JSONL）”。默认不勾选；背包快照原有默认选择不变。模块复用 `ServerMessagesExportModule`，不新增抓包器、解码器或写盘通道。

共筛选 33 种服务器下行消息：上述 18 种背包消息，PushDataCompleteNotify、角色列表与激活、主角切换与解锁、角色皮肤全量与新增、武器/声骸皮肤新增、总 COST、奖励展示、洗练预览，以及文档提及的旧 WeaponAdd/AllInfo/RemoveNotify。文档中的客户端 Request 不属于下行导出范围；AOI EntityAddNotify 与背包无关，不纳入此模块，仍可在“全部服务端消息”中导出。

证据：角色与主角处理见 `RoleController.cs` 474–494、`MainRoleController.cs` 132–153；皮肤见 `SkinController.cs` 164–205、`CSharpScript/Game/Module/Skin/Tab/Weapon/WeaponSkinController.cs` 52–64、`PhantomBattleController.cs` 378–434；奖励展示见 `ItemHintController.cs` 92–159。

角色、皮肤、COST、奖励及洗练预览保留完整协议 JSON 事件，**本次不合成为角色/皮肤状态快照**，也不当作背包数量变更。Reward 与 ItemMap 不能重复入账；NoTips、Reason、WeaponSkinAddNotify.IsLogin 等字段原样保留，不因是否弹窗丢失事件。历史武器协议只记录，不冒充已确认的背包更新通道。

文件位于 `{playerId}/captures/{captureId}/modules/item-changes/`，manifest、未归属消息处理及分片沿用通用实现；公共 `captures/{captureId}/capture-index.json` 登记模块状态、计数和产物。单独选择本模块即可导出，不依赖背包或全量事件模块。

## Windows 验收

1. 退出 Haiyu，同步分支并运行根目录 `publish.ps1`，确保 UI 与 ABI 一起更新。ABI 必须部署到实际“文档”目录下的 `Waves/Haiyu.ABI`。
2. 选择“背包快照”和“道具与角色变更事件”，开始捕获后重新登录游戏；首次全量到齐应生成原有玩家目录与快照，公共索引应登记两个模块。
3. 获取、使用、删除普通道具，获取/删除武器，改变限时道具。快照应更新原文件；数量等于游戏最新数量，不能累计相同的绝对数量，同配置的多个实例不能互相覆盖。
4. 升级声骸到 5 级并调谐，核对等级、经验及完整副词条；有未确认词条时检查 unAckSubProp，确认后旧词条和锁定槽位应按新完整对象清空或替换。失败操作不改变成功快照或增加更新次数。
5. 使用堆叠素材，检查拆分后的新实例及剩余数量；收到 StackCount=0 的更新后该实例应消失。奖励横幅或返还 ItemMap 不应再额外增加库存。
6. 切换账号后检查两个玩家文件、更新次数及索引归属独立，旧登录的流量不能覆盖新账号。停止后两位玩家统计应仍保留。
7. 打开 item-changes 的 JSONL，核对相应消息名称、真正响应 ID 和完整 payload；新角色、皮肤、奖励消息应出现；未确认洗练预览只出现事件，不能提前改写声骸快照。单独选择事件模块时也应正常导出。
8. 停止捕获后可运行 `python tools/validate_game_data_export.py --capture-index <capture-index.json>` 检查完整性。本地测试覆盖可重现的基线、增删改、响应错误、隔离及事件筛选；Windows UI/Npcap/真实服务器行为须按以上步骤实机验收。

## 本次验证结果

- .NET 10 测试工程串行编译通过，无错误。
- 34 组导出回归通过，含新增的绝对数量/实例键/幂等、升级/调谐/确认/拆分/失败响应、基线与顺序水位、账号隔离、事件筛选和单独导出检查；追加声骸字段的 JSON 与 MemoryPack 往返通过。
- 新模块名称在多语言源文件及四种语言资产中一致，逐项比较确认既有资产未被删除或改写。
- 全仓库 `validate_localization.py --generate` 未通过：原有语言资产中已有 47 个键不在源文件，另外有既有 UI 字面文案缺少源条目。生成时会删去这些既有键，因此本次保留原资产，只追加新模块名称。该全仓库问题未计入本次通过项目，也未顺带修改无关文案。
- 代码与协议元数据核对通过。没有运行 Windows UI、Npcap 或真实账号操作；以上 Windows 步骤是待实机验证的验收标准。
