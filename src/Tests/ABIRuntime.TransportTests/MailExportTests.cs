using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Protocol;
using MemoryPack;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static PbMailInfo Mail(string id, int state = 0) => new()
    {
        Id = id, State = state, Title = "标题<测试>", Content = "正文\n第二行", Sender = "sender",
        ReceivedTime = 9007199254740993L, ReadTime = 0, ExpiryTime = 2000,
        ValidTime = 30, ReadValidTime = 7, ConfigId = 123, IsIntervalMail = true,
        Attachments = { new PbMailAttachment { Id = 1, Count = 5 } }
    };
    private static PbMailInfo EmptyAttachmentMail(string id)
    {
        var mail = Mail(id); mail.Attachments.Clear(); return mail;
    }
    private static async Task TestMailMutationChainAsync()
    {
        await using var f = new NotificationFixture();
        f.Full();
        var inventoryOnly = await f.Flush();
        Equal(true, inventoryOnly.Mails is null);
        using (var initial = JsonDocument.Parse(File.ReadAllText(inventoryOnly.OutputJsonPath!)))
            Equal(false, initial.RootElement.TryGetProperty("mails", out _));
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("0001"), EmptyAttachmentMail("1") } });
        var result = await f.Flush();
        Equal(2, result.Mails!.Items.Count); // 字符串标识不能被数值归一化。
        Equal("unclaimed", result.Mails.Items.Single(m => m.Id == "0001").AttachmentStatus);
        Equal("none", result.Mails.Items.Single(m => m.Id == "1").AttachmentStatus);
        Equal(9007199254740993L, result.Mails.Items[0].ReceivedTime);
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("new") });
        f.Feed(WavesMessageIds.MailReadResponse, new MailReadResponse { Id = "new", State = 1, ReadTime = 100, ExpiryTime = 3000 });
        var read = await f.Flush();
        Equal(true, read.Mails!.Items.Single(m => m.Id == "new").IsRead);
        // AddMail 是完整对象覆盖：同 ID 可能携带正文等修订，不能简单忽略。
        var revised = Mail("new", 1); revised.ReadTime = 100; revised.ExpiryTime = 3000; revised.Title = "服务器修订标题";
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = revised });
        var revisedResult = await f.Flush();
        Equal("服务器修订标题", revisedResult.Mails!.Items.Single(m => m.Id == "new").Title);
        Equal(read.UpdateCount + 1, revisedResult.UpdateCount);
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = revised });
        Equal(revisedResult.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.MailGetAttachmentResponse, new MailGetAttachmentResponse { SuccessIdMap = { { "new", 3 } } });
        var claimed = await f.Flush();
        var mail = claimed.Mails!.Items.Single(m => m.Id == "new");
        Equal("claimed", mail.AttachmentStatus); Equal(100L, mail.ReadTime); Equal(3000L, mail.ExpiryTime);
        Equal(5, mail.Attachments.Single().Count); // 领取只更新状态，不删除附件定义或叠加背包数量。
        f.Feed(WavesMessageIds.MailFavoriteResponse, new MailFavoriteResponse { Id = "new", State = 7, ExpiryTime = 0 });
        mail = (await f.Flush()).Mails!.Items.Single(m => m.Id == "new");
        Equal(true, mail.IsFavorite); Equal(0L, mail.ExpiryTime);
        f.Feed(WavesMessageIds.MailFavoriteResponse, new MailFavoriteResponse { Id = "new", State = 3, ExpiryTime = 4000 });
        Equal(false, (await f.Flush()).Mails!.Items.Single(m => m.Id == "new").IsFavorite);
        // 成功列表可能仅含部分请求项；没有成功的邮件不能被删除。
        f.Feed(WavesMessageIds.MailDeleteResponse, new MailDeleteResponse { SuccessIds = { "0001" } });
        Equal(2, (await f.Flush()).Mails!.Items.Count);
        f.Feed(WavesMessageIds.MailDeleteNotify, new MailDeleteNotify { Id = "new" });
        var deleted = await f.Flush();
        Equal("1", deleted.Mails!.Items.Single().Id);
        f.Feed(WavesMessageIds.MailDeleteNotify, new MailDeleteNotify { Id = "new" });
        Equal(deleted.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("new") });
        Equal(false, (await f.Flush()).Mails!.Items.Single(m => m.Id == "new").IsRead);
        // 后续列表逐封合并，列表里未出现的邮件不能被推测为已删除。
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("third", 0x85) } });
        result = await f.Flush();
        Equal(3, result.Mails!.Items.Count);
        Equal(0x85, result.Mails.Items.Single(m => m.Id == "third").State);
        Equal(true, result.Mails.Items.Single(m => m.Id == "third").IsFavorite);
        Equal(true, result.Mails.Items.Single(m => m.Id == "third").IsRead);
        var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options), ABIMemoryPack.Options)!;
        Equal(result.Mails.Items.Count, round.Mails!.Items.Count);
        Equal(result.Mails.Items[0].Content, round.Mails.Items[0].Content);
        var jsonRound = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ABIJsonContext.Default.GameDataExportResult), ABIJsonContext.Default.GameDataExportResult)!;
        Equal(result.Mails.Items[0].ReceivedTime, jsonRound.Mails!.Items[0].ReceivedTime);
        using var file = JsonDocument.Parse(File.ReadAllText(result.OutputJsonPath!));
        Equal(3, file.RootElement.GetProperty("mails").GetProperty("count").GetInt32());
        Equal(true, file.RootElement.TryGetProperty("inventory", out _));
        Equal(inventoryOnly.OutputJsonPath, result.OutputJsonPath);
        Equal(1, Directory.GetFiles(Path.GetDirectoryName(result.OutputJsonPath!)!, "*.json").Length);
    }

    private static async Task TestMailFailureAndIsolationAsync()
    {
        await using var f = new NotificationFixture();
        f.Full(); f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify());
        var empty = await f.Flush();
        Equal(0, empty.Mails!.Items.Count); // 收到空列表与未收到同步不同。
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("a") });
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("b") });
        var before = await f.Flush();
        f.Feed(WavesMessageIds.MailReadResponse, new MailReadResponse { Id = "a", State = 1, ErrorCode = (ErrorCode)1 });
        f.Feed(WavesMessageIds.MailGetAttachmentResponse, new MailGetAttachmentResponse { ErrorCode = (ErrorCode)1, SuccessIdMap = { { "a", 3 } } });
        f.Feed(WavesMessageIds.MailDeleteResponse, new MailDeleteResponse { ErrorCode = (ErrorCode)1, SuccessIds = { "a" } });
        f.Feed(WavesMessageIds.MailFavoriteResponse, new MailFavoriteResponse { ErrorCode = (ErrorCode)1, Id = "a", State = 4 });
        Equal(before.UpdateCount, (await f.Flush()).UpdateCount);
        var bytes = File.ReadAllText(before.OutputJsonPath!);
        f.Feed(WavesMessageIds.MailGetAttachmentResponse, new MailGetAttachmentResponse { SuccessIdMap = { { "a", 3 }, { "missing", 3 } } });
        var invalid = await f.Flush();
        Equal("AwaitingBaseline", invalid.Modules!.Single().Sources.Single(s => s.SourceId == "mails").State);
        Equal(bytes, File.ReadAllText(before.OutputJsonPath!)); // 不部分提交已知邮件的领取。
        // 无全量边界的后续列表不能谎称修复遗漏。
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("a", 3) } });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "mails").State);
        f.Full(session: "login-B", player: 222);
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("a", 1) } }, "login-B", 222);
        var other = await f.Flush();
        Equal(222, other.PlayerId); Equal(1, other.Mails!.Items.Count); Equal(true, other.Mails.Items.Single().IsRead);
        Equal(bytes, File.ReadAllText(before.OutputJsonPath!));
        f.Feed(WavesMessageIds.MailDeleteNotify, new MailDeleteNotify { Id = "a" }, current: false);
        Equal(1, (await f.Flush()).Mails!.Items.Count);
        f.Full(session: "login-A2", player: 111);
        var pendingMail = await f.Flush();
        Equal(true, pendingMail.Mails is null);
        Equal(false, pendingMail.Modules!.Single().Counters.Any(c => c.Name == "mails" && c.PlayerId == 111));
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("restored", 7) } }, "login-A2", 111);
        var recovered = await f.Flush();
        Equal("restored", recovered.Mails!.Items.Single().Id);
        Equal("Ready", recovered.Modules!.Single().Sources.Single(s => s.SourceId == "mails" && s.SessionId == "login-A2" && s.PlayerId == 111).State);
    }

    private static async Task TestMailMissingBaselineAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        var previous = await f.Flush();
        f.Feed(WavesMessageIds.MailAddNotify, new MailAddNotify { NewMail = Mail("early") });
        var waiting = await f.Flush();
        Equal("AwaitingBaseline", waiting.Modules!.Single().Sources.Single(s => s.SourceId == "mails").State);
        Equal(previous.UpdateCount, waiting.UpdateCount);
        f.Feed(WavesMessageIds.MailInfosNotify, new MailInfosNotify { MailInfos = { Mail("early") } });
        Equal("early", (await f.Flush()).Mails!.Items.Single().Id);
        f.Feed(WavesMessageIds.MailReadResponse, new MailReadResponse { Id = "early", State = 0, ReadTime = 0, ExpiryTime = 0 });
        var unread = (await f.Flush()).Mails!.Items.Single();
        Equal(false, unread.IsRead); Equal(0L, unread.ReadTime); Equal(0L, unread.ExpiryTime);
        f.Feed(WavesMessageIds.MailReadResponse, new MailReadResponse { Id = "missing", State = 1 });
        Equal("AwaitingBaseline", (await f.Flush()).Modules!.Single().Sources.Single(s => s.SourceId == "mails").State);
    }
}
