using ABI.Models;
using Google.Protobuf;
using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Modules;
using Haiyu.ABI.Services.GameDataExport.Session;
using Haiyu.ABI.Services.GameDataExport.Storage;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static async Task TestSameAccountReloginSnapshotAsync()
    {
        // 两种边界：明确捕获登出；未捕获登出、仅由新 LoginResponse 识别重登。
        foreach (var observeLogout in new[] { true, false })
        {
            var root = Path.Combine(Path.GetTempPath(), "haiyu-same-account-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var peer = new ExportPeer();
                var clock = new ExportTestClock();
                var context = new ExportCaptureContext(new JsonExportStore(root), clock);
                await using var coordinator = new ExportCoordinator(context, BuiltInExportModules.Create(), ["inventory"]);
                using var capture = new CaptureConnectionState(peer.CreateLoader()) { Observer = coordinator, ConnectionId = 1 };
                void Feed(ushort id, IMessage message) => capture.ConsumeRaw(peer.Frame(id, message.ToByteArray()));
                async Task<GameDataExportResult> Flush()
                {
                    clock.Advance(TimeSpan.FromSeconds(2));
                    coordinator.Checkpoint();
                    await context.Output.FlushAsync();
                    return coordinator.LegacyResult(coordinator.Progress());
                }
                foreach (var frame in peer.Login(111)) capture.ConsumeRaw(frame);
                Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("old"), Mail("keep", 7) } });
                Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(1, 100, true)));
                Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1, 90), OwnedRole(2) } });
                Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse { PhantomItemList = { new PhantomItem { Id = 30, IncrId = 301, PhantomLevel = 25, StackCount = 1 } },
                    EquipInfo = { new RolePhantomEquipInfo { RoleId = 1, PhantomItemIncrId = { 301 } } } });
                var first = await Flush();
                Equal(301, first.Roles!.Items[0].Echoes[0].IncrId);
                Equal(2, first.Mails!.Items.Count);
                Equal(111, first.Inventory!.NormalItems.Single().Id);
                var previous = File.ReadAllText(first.OutputJsonPath!);
                var firstSession = capture.Session.SessionId;
                var originalKey = capture.Session.SessionKey!.ToArray();

                if (observeLogout) Feed(WavesMessageIds.LogoutNotify, new LogoutNotify());
                // 刻意不发送新 ProtoKeyResponse，保持同一连接、相同 playerId 和相同密钥。
                Feed(WavesMessageIds.LoginResponse, new LoginResponse());
                Equal(false, capture.Session.SessionId == firstSession);
                Equal(true, capture.Session.ConfirmedPlayerId is null);
                Equal(true, originalKey.SequenceEqual(capture.Session.SessionKey!));
                Feed(WavesMessageIds.BasicInfoNotify, new BasicInfoNotify { Id = 111 });
                var secondSession = capture.Session.SessionId;
                Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("keep", 0) } });
                Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(2, 200)));
                Feed(WavesMessageIds.PbGetRoleListNotify, new PbGetRoleListNotify { RoleList = { OwnedRole(1, 1) } });
                Feed(WavesMessageIds.NormalItemResponse, new NormalItemResponse());
                await Flush();
                Equal(previous, File.ReadAllText(first.OutputJsonPath!)); // 全量来源未齐，不用半份新数据覆盖。
                Feed(WavesMessageIds.WeaponItemResponse, new WeaponItemResponse());
                Feed(WavesMessageIds.PhantomItemResponse, new PhantomItemResponse());
                Feed(WavesMessageIds.ValidTimeItemResponse, new ValidTimeItemResponse());
                var second = await Flush();
                Equal(111, second.PlayerId);
                Equal(1, second.Roles!.Items.Single().Level);
                Equal(true, second.Roles.Items.Single().Echoes.All(e => e.Status == "empty"));
                Equal(first.OutputJsonPath, second.OutputJsonPath);
                Equal(first.UpdateCount + 1, second.UpdateCount);
                Equal(2, second.Achievements!.Items.Single().Id);
                Equal("unclaimed", second.Achievements.Items.Single().RewardStatus);
                Equal(0, second.Inventory!.NormalItems.Count); // 新全量为空也必须替换旧道具。
                Equal("keep", second.Mails!.Items.Single().Id); // 旧邮件不残留。
                Equal(false, second.Mails.Items.Single().IsRead);
                Equal(false, second.Mails.Items.Single().IsFavorite);
                Equal("unclaimed", second.Mails.Items.Single().AttachmentStatus);
                Equal(true, second.Modules!.Single().Artifacts.Single().Sources.All(s => s.SessionId == secondSession));
                Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("after") });
                Equal(2, (await Flush()).Mails!.Items.Count); // 新基线后的增量继续生效。
                Equal(1, Directory.GetFiles(Path.Combine(root, "111"), "*.json").Length);
                var modules = await coordinator.FinishAsync(new(null, false));
                Equal(GameDataExportModuleState.Completed, modules.Single().State);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
