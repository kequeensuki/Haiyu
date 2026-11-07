# 邮件聚合与验收

## 源码依据

只读参考：WutheringMod `3d8b5cee02dd49f168fac6c361bede6df1274ecb`，`DecompiledSource/Aki.Game`；协议字段使用 Haiyu 已生成的 `WavesGameData.proto` 与 `WavesMessageIds.cs`，没有自造协议或修改游戏源码。

| 消息 | 编号 | 客户端证据 | 聚合行为 |
|---|---:|---|---|
| MailInfosNotify | 25021 | MailController.cs:128；MailModel.cs:324、430 | 首次列表建立登录基线；后续按完整对象合并，不因列表缺席而删除 |
| MailAddNotify | 29640 | MailController.cs:181；MailModel.AddMail/MakeMailInstance | 按 ID 新增或替换完整对象，接收服务器修订；完全相同的对象不重复落盘 |
| MailDeleteNotify | 16201 | MailController.cs:152 | 按字符串 ID 删除，含服务器撤回；重复删除幂等 |
| MailReadResponse | 28318 | MailController.cs:290 | 成功后替换 state、readTime、expiryTime |
| MailGetAttachmentResponse | 27409 | MailController.cs:345；MailModel.cs:207 | 成功后只更新 successIdMap 中的邮件；map 的值为新状态，不是错误码 |
| MailDeleteResponse | 20393 | MailController.cs:485 | 成功后仅删除 successIds，未出现在成功列表的邮件不动 |
| MailFavoriteResponse | 26065 | MailController.cs:386；MailModel.cs:100 | 成功后替换 state、expiryTime；收藏位可从返回 state 得到，无需猜测请求上下文 |

状态位证据：`MailModel.SetMailStatusByStatusCode`（MailModel.cs:536），`EMailState.cs` 与 `EMailAttachment.cs`。`state & 1` 表示已读，`state & 2` 表示附件已领取，`state & 4` 表示收藏。无附件时客户端状态为 NoneAttachment，不因领取位默认零而判定“有附件未领”。未知位完整保留。

注意：阅读、主动删除、领取、收藏是客户端操作对应的服务端响应，并非都有独立主动通知。`LoadingController.OnPushDataCompleteNotify` 只发布全局加载完成事件，不包含邮箱条目数、版本或分页信息，不能据此制造邮箱事务版本。

`OnMailInfosNotify` 遍历 AddMail，`SetValueOfMailList` 按 ID 赋值，未清空 MailMap。首次登录列表按用户实际观察的初始同步用作基线；协议不含邮件总数和完结标记，不能仅由这个消息名证明服务器不存在更多分批数据。后续列表必须合并。捕获应在登录前启动；不能据本功能恢复捕获前历史或保证任何网络遗漏都能被检测。

## 数据结构与输出

继续写入 `GameDataExport/{playerId}/game-data.json`，不新增时间戳文件。保留既有 inventory 与 scope 字段，在已收到邮件基线后增加：

```json
{
  "mails": {
    "count": 1,
    "items": [{
      "id": "mail-001",
      "state": 3,
      "isRead": true,
      "isFavorite": false,
      "attachmentStatus": "claimed"
    }]
  }
}
```

每封邮件还保存完整服务器字段：receivedTime、readTime、level、title、content、sender、validTime、readValidTime、attachments（id/count）、configId、expiryTime、isIntervalMail。字符串 ID 不转整数，时间保留 int64 原值，标题/正文保存原始文本和标记，不模拟客户端渲染或配置补文。

`attachmentStatus` 为 `none` / `unclaimed` / `claimed`。领取不会清空附件定义，不伪造阅读时间，不直接把奖励加到背包；背包仍由库存消息更新。状态码按响应整体替换，不用 OR 累积；服务器返回的零值也生效。

邮件来源 `mails` 初始为未收到的可选来源，避免破坏旧背包范围和不含邮件的回放；此时 JSON 不出现 mails，捕获索引显示 Missing，不能将其当作空邮箱。收到邮件相关成功消息或解码失败后，该来源转为必需；收到空 MailInfosNotify 后才输出 count=0。发生缺失基线、缺失修改目标、非法对象或解析失败时，保留上一份文件并显示 AwaitingBaseline。失败响应不改变数据，也不伪造成功。

同一批消息中任一修改失败则全部回滚。已建立过基线后，后续 MailInfosNotify 仅可合并，不能当作完整替换来解除失同步；重新登录产生独立会话并接收新的初始列表后恢复。切号按 playerId 隔离，旧会话和未验证连接不能修改新玩家数据。

## 代码组织

- `Domains/Mail`：邮件不可变状态与业务集合，复用通用增删改及批事务。
- `Adapters/Mail`：7 种消息的成功条件和字段转换。
- 公共适配器提升为 `Adapters/CollectionMessageAdapter`，公共快照提升为 `Aggregation/Collections/CollectionSnapshot`，组合根改名为 `PlayerDataMessageRegistry`。
- `PlayerDataSnapshotMapper` 负责背包和邮件到 ABI DTO 的映射，现有 inventory 模块标识保留兼容，显示名更新为玩家数据快照。
- `GameDataExportResult` 尾部追加可空 Mails，注册 JSON 源生成和 MemoryPack formatter。主程序和 ABI 必须同包更新，不能混用旧 ABI 交换模型。

没有新增全局邮件状态、副本版本或文件写入线程；同步状态仍由 SourceState 管理，落盘仍由现有原子写入和回执管线负责。

## 验证

`ABIRuntime.TransportTests --game-data-export` 新增 MailExportTests，覆盖新增→阅读→领取→收藏/取消→删除→重新新增、重复通知、后续列表不误删、空邮箱与未收到的区别、字符串 ID、int64、JSON/MemoryPack 往返、错误码、成功子集、批内失败回滚、缺失目标、无基线、切号、退役流量和重登恢复。

NativeAOT 冒烟新增实际邮件解码、已读、领取、文件落盘与 MemoryPack 往返。Windows 实机应在开启导出后重新登录，检查初始列表、读取未读邮件、单领/一键领取、收藏/取消、主动删除及可观测的新增/撤回；对比同一 playerId/game-data.json 与捕获索引中的 mails 来源状态。无需主动发送任何工具请求。

最终自动验证：43 项导出回归通过，Linux x64 NativeAOT 邮件冒烟实际运行通过。新增切回旧账号但尚未接收邮件基线时的邮件数量清理验证，避免当前文件不含 mails 却显示旧数量。Windows 实机尚未验证。


## 同账号退出再进入

新增 `SameAccountReloginTests`，通过真实 CaptureConnectionState/ExportCoordinator 链路验证同一连接、同一 playerId、复用同一 AES 密钥的两种路径：LogoutNotify→LoginResponse，及未收到 LogoutNotify 但收到新 LoginResponse。新 SessionId 的来源基线重新收集；邮件首次列表替换旧登录的邮箱，新全量为空时清空旧道具；全量未齐保留旧文件；同一玩家路径不变、次数续计，新基线后增量继续生效。51 项导出回归通过。

这不等同于同一会话内任意列表重推：缺少新登录/登出/换钥等可识别边界时，邮件列表仍按客户端的逐封合并语义处理，不能单凭某 ID 未出现就推断删除。
