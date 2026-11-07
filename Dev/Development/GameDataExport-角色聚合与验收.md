# 角色、声骸装备、属性及技能导出

角色与装备均并入 `<playerId>/game-data.json`，新增 `roles.items`，不增加时间戳文件。账号角色以首次 `PbGetRoleListNotify` 或成功的 `PbGetRoleListResponse.role_list` 建立完整基线；同一登录内后续 Notify 按角色合并，不因列表缺席删除角色，也不用于解除已有失同步。成功 Response 才按完整列表替换并恢复同步；`PbRoleActiveNotify` 加入新角色，`RoleChangeNotify` 删除源角色并加入切换后的角色。试用/机器人属性响应不会加入账号角色列表。

## 数据语义

- `skills`：协议技能 ID 与实际等级。技能预览响应不是实际等级，不参与更新；技能节点另存 `skillNodes`。
- `baseProps/addProps`：角色属性原始整数值。`attributes` 按相同 ID 的 base+add 合计，依据客户端 `RoleAttributeData.GetAttrValueById`；保留协议单位，不转换百分比或舍入，不代表战斗中动态 buff 的最终面板。
- `echoes`：最多五个槽位，输出槽号 1–5，槽位 1 为主声骸。每件关联背包实例的完整详情，包括等级、词条和 `fetterGroupId`。这里导出服务器套装组 ID，不猜测配置名称或套装效果。
- `echoBaseProps/echoAddProps`：服务器单独推送的声骸属性贡献；未收到时为 null，不重复加到角色属性。
- `equipmentStatus`：missing 表示未收到装备基线；ready 表示槽位可解析；incomplete 表示实例缺失或等级异常。槽位 status：empty / ready / unresolved / invalid_level。异常等级保留原值，不夹到 25。

## 协议与源码依据

参考只读 WutheringMod `3d8b5cee02dd49f168fac6c361bede6df1274ecb`：

- `RoleController.OnRegisterNetEvent`、`RoleInstance.RefreshRoleInfo/RefreshRoleAttr/RefreshSkillInfo`：登录列表、新角色、等级、属性、技能变化。普通属性通知替换整个 base/add 列表，空列表清空旧值；单技能通知按键更新。已有角色的后续 roleInfo 同样逐技能合并，不凭技能 ID 缺席删除旧技能；明确的全量基线则重建集合。`RoleModel.UpdateRoleSkillNodeData` 替换节点列表。
- `RoleController.RobotRolePropRequest`：成功属性查询按键合并，不依缺席删除；可能含机器人角色，不能作为拥有角色的依据。
- `MainRoleController` 注册 `RoleChangeNotify`；`RoleModel.RoleChange` 移除源角色，同时 `PhantomBattleModel.DeleteBattleData` 清除源角色装备，避免该角色重新加入时复活旧装备。
- `PhantomBattleController.OnEquipPhantomItem/OnPhantomEquipInfoNotify`、`PhantomBattleModel.UpdateRoleEquipmentData/UpdateRoleEquipmentPropData`：全量装备/属性及增量；同实例转移会清空旧角色对应槽位。
- `PhantomRoleEquipmentData`：五槽、索引 0 主声骸、0 表示空槽；装备属性对象整体替换。
- `PbOverRoleResponse` 成功更新突破；`RoleSkillQuickLevelUpResponse` 成功且携带角色对象时更新角色；成功但未携带可选 RoleInfo 的 ACK 不创建来源、不触发失同步。失败响应无状态变化。

`roles` 与 `roleEquipment` 是独立来源，装备可先于角色列表到达。共享的声骸全量/换装响应同时分派到背包与装备集合；同一来源重复注册仍拒绝。角色变更通过明确的业务变更类型进入集合；技能合并、属性替换等规则由领域集合负责，适配器只转换协议字段。集合批内失败回滚。已有角色列表基线时，先于完整角色到达的属性/技能/节点创建待初始化标识，聚合显示 AwaitingEntities 并暂停落盘；完整角色对象到达后恢复，且不重放其之前的初始化片段。真正无效字段或缺失整个角色列表基线仍按硬失同步处理，必须重新建立完整基线；跨来源不承诺服务器事务一致性。声骸背包变化自动重建角色关联详情，无需复制维护另一套声骸库存。

## 验收

覆盖首次列表/后续列表合并、可选 RoleInfo ACK、明确空全量、新角色、技能升级及后续 roleInfo 保留缺席技能、节点替换、属性替换及零值、属性查询合并、突破、改名、主角替换、声骸转移/卸下/升级/调谐、失败响应、异常槽位、实例缺失、缺少新增、全量恢复及同账号重登。包含 JSON/MemoryPack 与 Linux NativeAOT 加密帧端到端验证。Windows 实机抓包仍需验证。

ABI DTO 新增角色对象，主程序和 ABI 必须配套更新。

## 原生序列化兼容

Linux .NET 10 NativeAOT 烟测发现 MemoryPack 1.21.4 在 `GameRoleEcho.Item -> GamePhantomItem` 的嵌套 `WritePackable` 静态接口分派处访问空函数指针。gdb 调用栈确认发生于 MemoryPack 序列化阶段，此前 JSON 已成功落盘。该属性通过专用 attribute 使用注册的实例 formatter (`WriteValue/ReadValue`)；仍使用原 GamePhantomItem formatter，不改变传输格式。原生烟测同时包含装备实例及空槽，覆盖该路径。

验证结果（2026-11-07）：`ABIRuntime.TransportTests --game-data-export` 54 项全部通过；Linux x64 NativeAOT 加密帧烟测通过（协议消息 5481，注册编号 3910），包括角色声骸非空实例及空槽的 MemoryPack 往返。Windows 实机抓包未在此环境验证。

## 真实流量回归

实际登录数据中，`roleInfo.base_prop` 会重复出现相同属性 ID。客户端 `RoleInstance.RefreshRoleAttr` 调用字典赋值，后值覆盖前值；不能因重复 ID 拒绝整个角色列表。技能与声骸属性同样按客户端逐键赋值规则处理；无效 ID、重复节点标识及无效槽位仍拒绝。

获得新角色时可观察到 `RolePhantomPropUpdateNotify → PbRolePropsNotify → RoleSkillNodeNotify/PbRoleSkillLevelNotify → PbRoleActiveNotify`。完整角色尚未到达时，不能凭零散字段伪造角色，也不能永久拒绝后到的 Active。领域集合声明 IsComplete，快照冻结该标记，通用聚合器暂停不完整数据的投影但继续接收有序事件。待初始化角色未补齐便停止时报告 Incomplete；硬失同步仍不能被单个 Active 绕过。

新增匿名回归向量，覆盖重复字段后值覆盖、初始化先于激活、完整对象的权威替换、遗漏激活时保留旧文件、软等待与硬失同步区分。54 项导出回归及 Linux x64 NativeAOT 烟测通过。真实 JSONL 及账号资料仅在隔离分析目录回放，未加入仓库。
