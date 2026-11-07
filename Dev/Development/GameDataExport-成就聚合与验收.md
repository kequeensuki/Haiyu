# 成就聚合与验收

## 源码依据

参考 WutheringMod `3d8b5cee02dd49f168fac6c361bede6df1274ecb` 的只读 `DecompiledSource/Aki.Game`，协议使用当前 Haiyu 已生成的 WavesGameData.proto，无新增自造协议。

初始同步并不是简单的主动全量通知：`AchievementController.OnLoadingNetDataDone`（92）调用 `RequestAchievementBasicInfo`（98），游戏自己发送 AchievementInfoRequest，导出器仅被动接收 AchievementInfoResponse。

| 消息 | ID | 客户端依据 | 行为 |
|---|---:|---|---|
| AchievementInfoResponse | 26649 | AchievementModel.PhraseBaseData（121） | 从分组列表建立新的条目、组和汇总基线 |
| AchievementUpdateResponse | 21082 | AchievementController.RequestUpdateAchievementInfo（114）；AchievementModel.PhraseUpdateData（189） | 成功后合并条目，不删除未出现的条目 |
| AchievementProgressNotify | 25834 | AchievementController.OnAchievementProgressNotify（185）；AchievementModel.OnAchievementProgressNotify（198） | 合并单个完整条目 |
| AchievementListProgressNotify | 23250 | AchievementController.OnAchievementListProgressNotify（232） | 整批合并条目 |
| AchievementGroupProgressNotify | 15821 | AchievementController.OnAchievementGroupProgressNotify（250）；AchievementModel.OnAchievementGroupProgressNotify（231） | 更新独立的成就组状态 |
| AchievementCountChangeNotify | 28476 | AchievementModel.OnAchievementCountChangeNotify（253） | 更新服务器 finishCount、starCount，不从本地条目推算 |
| AchievementReceiveResponse | 22908 | AchievementController.RequestGetAchievementReward（137） | 仅确认；成功响应不含目标 ID 或领取状态，不猜测目标 |
| MulAchievementReceiveResponse | 29250 | AchievementController.RequestGetMultiAchievementReward（154） | 仅确认；客户端回调只检查错误码，不据返回 ID 列表修改状态 |
| AchievementFinishResponse | 21914 | AchievementController.RequestAchievementFinish（170） | 仅确认；不据成功 ACK 伪造完成或领奖 |

收到确认不等于导出器已收到对应状态。实际领取状态以 Entry/GroupEntry 的 isReceive 为准，通过状态通知、更新响应或下一次初始同步刷新。确认先到时保留上次已确认状态；不把 ACK 的 itemMap 奖励展示再计入背包，也不把未知 ID 误标为已领取。

`AchievementModel.GetAchievementData`（322）与 GetAchievementGroupData（379）会懒创建新对象，因此已建立基线后首次出现的合法成就/组可以新增，不能套用武器“目标不存在就是漏收”的规则。新条目没有服务器分组关系时 groupId 为 null；已有条目的分组关系在不带组 ID 的进度通知中保留。

## 完成和奖励状态

依据 `AchievementData.GetFinishState`（329）、`AchievementGroupData.GetFinishState`（171）、EAchievementStateEnum：

| 条件（按优先级） | rewardStatus | isCompleted / rewardClaimed |
|---|---|---|
| isReceive=true | claimed | true / true |
| isReceive=false 且 finishTime>0 | unclaimed | true / false |
| 其余 | not_available | false / false |

进度达到目标不代替完成时间；成就完成不代替奖励领取。成就组奖励与单个成就奖励完全独立，ID 即使相同也不能串改。原始 isReceive 与 finishTime 保留；finishTime 按协议 uint32 无损保存，不经 int32 截断。缺失的 progress 输出 null，不补造 0/0。

rewardStatus 表示收到的完成/领取状态，不是能否点击领奖的 UI 资格判定。客户端显示还依赖隐藏规则、成就链、奖励配置等（如 AchievementGroupData.CanGetReward 检查奖励包），本功能不伪造 canClaim、标题、奖励包或本地未同步的配置条目。未出现在同步中的成就不直接标为“未完成”。

## 产物与边界

仍然覆盖同一个 `GameDataExport/{playerId}/game-data.json`，增加 achievements 节点：

```json
{
  "achievements": {
    "finishCount": 12,
    "starCount": 99,
    "items": [{
      "id": 1, "groupId": 10, "finishTime": 1700000000,
      "isReceive": false, "currentProgress": 10, "totalProgress": 10,
      "isCompleted": true, "rewardClaimed": false, "rewardStatus": "unclaimed"
    }],
    "groups": [{
      "id": 10, "finishTime": 1700000000, "isReceive": false,
      "isCompleted": true, "rewardClaimed": false, "rewardStatus": "unclaimed"
    }]
  }
}
```

初始未收到数据时不出现 achievements，索引标记可选来源 Missing；首次状态变更后该来源变为必需，缺基线则保留旧文件。有效空 InfoResponse 与未收到不同。初始响应重建已同步记录集合，部分更新和通知只合并；不凭增量列表缺席或领奖 ACK 删除成就。没有补造单独删除协议。

非法 ID、缺失必要通知对象、重复初始键或解析失败会使来源等待基线；批量任何一项失败均整体回滚。完整 InfoResponse 可恢复。登录会话重新建立基线，即使 playerId 和密钥不变，也不会残留上次登录的条目或领取状态。切号隔离、固定文件覆盖、版本回执与原子写入沿用现有管线。

各条通知没有提供服务器事务版本，汇总与条目可能先后到达；导出为最近已接收状态，不承诺整个服务器状态的同一事务切面。

## 代码与验证

- Domains/Achievements：类型化状态、领奖规则和集合，复用 DataCollectionBase 的增删改及批事务。
- Adapters/Achievements：9 个消息注册和字段转换；成就、成就组及汇总使用不同键空间。
- PlayerDataMessageRegistry 注册可选来源；PlayerDataSnapshotMapper 和 InventoryJsonWriter 输出稳定 DTO。
- GameDataExportResult 尾部追加可空 Achievements，显式注册 JSON 源生成和 MemoryPack 类型。主程序和 ABI 必须同包更新。
- 新增 AchievementExportTests，并扩展同账号真实重登链路和 NativeAOT 冒烟。

自动测试覆盖未完成/未领取/已领取、组奖励独立、相同 ID、uint32 上限、缺失进度、稀疏新条目、失败 ACK、不猜测领奖、批内回滚、空初始列表、全量恢复、部分更新不删除、跨玩家、同账号重登和 JSON/MemoryPack 往返。

Windows 实机：导出开启后重新登录，确认初始成就；完成一项后检查 unclaimed，再领取检查 claimed；分别测试组奖励、批量领取以及同账号重登。对比文件和捕获索引，不能仅凭领奖按钮已点击就断言消息已收到。Linux 验证不替代真实 Npcap/WinUI/Windows NativeAOT 验证。

最终验证：51 项导出回归全部通过；Linux x64 NativeAOT 冒烟实际执行通过，覆盖成就全量、领取状态通知、uint32 完成时间、成就组独立状态与 MemoryPack 往返。
