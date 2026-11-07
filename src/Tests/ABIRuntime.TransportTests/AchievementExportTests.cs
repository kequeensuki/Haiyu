using System.Text.Json;
using ABI.Models;
using Haiyu.ABI.Protocol;
using MemoryPack;

namespace ABIRuntime.TransportTests;

internal static partial class Program
{
    private static AchievementEntry Achievement(int id, uint finish = 0, bool receive = false, int progress = 0) =>
        new() { Id = id, FinishTime = finish, IsReceive = receive, Progress = new AchievementProgress { CurProgress = progress, TotalProgress = 10 } };
    private static AchievementInfoResponse AchievementBase(params AchievementEntry[] entries) => new()
    {
        FinishCount = 12, StarCount = 99, AchievementGroupInfoList = { new AchievementGroupInfo
        { AchievementGroupEntry = new AchievementGroupEntry { Id = 10, FinishTime = 100 }, AchievementEntryList = { entries } } }
    };
    private static async Task TestAchievementStatesAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        var absent = await f.Flush();
        Equal(true, absent.Achievements is null);
        using (var initial = JsonDocument.Parse(File.ReadAllText(absent.OutputJsonPath!)))
            Equal(false, initial.RootElement.TryGetProperty("achievements", out _));
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(
            Achievement(1, progress: 10), Achievement(2, uint.MaxValue), Achievement(3, receive: true), new AchievementEntry { Id = 4 }));
        var result = await f.Flush();
        var data = result.Achievements!;
        Equal("not_available", data.Items.Single(a => a.Id == 1).RewardStatus); // 进度满但无完成时间仍不能认定可领奖。
        Equal(false, data.Items.Single(a => a.Id == 1).IsCompleted);
        Equal("unclaimed", data.Items.Single(a => a.Id == 2).RewardStatus);
        Equal(uint.MaxValue, data.Items.Single(a => a.Id == 2).FinishTime);
        Equal("claimed", data.Items.Single(a => a.Id == 3).RewardStatus); // 领取标志优先，即使完成时间为零。
        Equal(true, data.Items.Single(a => a.Id == 3).IsCompleted);
        Equal(true, data.Items.Single(a => a.Id == 4).CurrentProgress is null);
        Equal("unclaimed", data.Groups.Single().RewardStatus);
        Equal(12, data.FinishCount); Equal(99, data.StarCount); // 不从稀疏条目数推算服务器总数。
        f.Feed(WavesMessageIds.AchievementProgressNotify, new AchievementProgressNotify { AchievementEntry = Achievement(1, 200) });
        var complete = await f.Flush();
        Equal("unclaimed", complete.Achievements!.Items.Single(a => a.Id == 1).RewardStatus);
        Equal(10, complete.Achievements.Items.Single(a => a.Id == 1).GroupId!.Value);
        f.Feed(WavesMessageIds.AchievementProgressNotify, new AchievementProgressNotify { AchievementEntry = Achievement(1, 200) });
        Equal(complete.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.AchievementListProgressNotify, new AchievementListProgressNotify
            { AchievementEntryList = { Achievement(1, 200, true), Achievement(10, 300, true) } });
        var claimed = await f.Flush();
        Equal("claimed", claimed.Achievements!.Items.Single(a => a.Id == 1).RewardStatus);
        Equal(true, claimed.Achievements.Items.Single(a => a.Id == 10).GroupId is null);
        Equal(false, claimed.Achievements.Groups.Single().RewardClaimed); // 组 ID 与成就 ID 可重合，奖励独立。
        f.Feed(WavesMessageIds.AchievementGroupProgressNotify, new AchievementGroupProgressNotify
            { AchievementGroupEntry = new AchievementGroupEntry { Id = 10, FinishTime = 100, IsReceive = true } });
        Equal(true, (await f.Flush()).Achievements!.Groups.Single().RewardClaimed);
        f.Feed(WavesMessageIds.AchievementCountChangeNotify, new AchievementCountChangeNotify { FinishCount = 7, StarCount = 25 });
        var counted = await f.Flush(); Equal(7, counted.Achievements!.FinishCount); Equal(25, counted.Achievements.StarCount);
        f.Feed(WavesMessageIds.AchievementUpdateResponse, new AchievementUpdateResponse { AchievementEntryList = { Achievement(2, uint.MaxValue, true) } });
        result = await f.Flush();
        Equal(5, result.Achievements!.Items.Count); // 部分更新不删除缺席条目。
        Equal(true, result.Achievements.Items.Single(a => a.Id == 2).RewardClaimed);
        var round = MemoryPackSerializer.Deserialize<GameDataExportResult>(MemoryPackSerializer.Serialize(result, ABIMemoryPack.Options), ABIMemoryPack.Options)!;
        Equal(uint.MaxValue, round.Achievements!.Items.Single(a => a.Id == 2).FinishTime);
        var jsonRound = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ABIJsonContext.Default.GameDataExportResult), ABIJsonContext.Default.GameDataExportResult)!;
        Equal(5, jsonRound.Achievements!.Items.Count);
        using var file = JsonDocument.Parse(File.ReadAllText(result.OutputJsonPath!));
        Equal(5, file.RootElement.GetProperty("achievements").GetProperty("items").GetArrayLength());
        Equal(absent.OutputJsonPath, result.OutputJsonPath);
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(9)));
        Equal(9, (await f.Flush()).Achievements!.Items.Single().Id); // 真正全量会替换旧条目。
    }
    private static async Task TestAchievementRewardAndRollbackAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(1, 100)));
        var initial = await f.Flush();
        f.Feed(WavesMessageIds.AchievementReceiveResponse, new AchievementReceiveResponse());
        f.Feed(WavesMessageIds.MulAchievementReceiveResponse, new MulAchievementReceiveResponse
            { AchievementIds = { 1, 999 }, AchievementGroupIds = { 10 } });
        f.Feed(WavesMessageIds.AchievementFinishResponse, new AchievementFinishResponse());
        var ack = await f.Flush();
        Equal(initial.UpdateCount, ack.UpdateCount);
        Equal(false, ack.Achievements!.Items.Single().RewardClaimed); // ACK 没有状态条目，不能猜测领取目标。
        f.Feed(WavesMessageIds.AchievementUpdateResponse, new AchievementUpdateResponse
            { ErrorCode = (ErrorCode)1, AchievementEntryList = { Achievement(1, 100, true) } });
        f.Feed(WavesMessageIds.AchievementReceiveResponse, new AchievementReceiveResponse { ErrorCode = (ErrorCode)1 });
        f.Feed(WavesMessageIds.MulAchievementReceiveResponse, new MulAchievementReceiveResponse
            { ErrorCode = (ErrorCode)1, AchievementIds = { 1 } });
        Equal(initial.UpdateCount, (await f.Flush()).UpdateCount);
        var before = File.ReadAllText(initial.OutputJsonPath!);
        f.Feed(WavesMessageIds.AchievementListProgressNotify, new AchievementListProgressNotify
            { AchievementEntryList = { Achievement(1, 100, true), Achievement(0) } });
        var failed = await f.Flush();
        Equal("AwaitingBaseline", failed.Modules!.Single().Sources.Single(s => s.SourceId == "achievements").State);
        Equal(before, File.ReadAllText(initial.OutputJsonPath!));
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(1, 100, true)));
        var recovered = await f.Flush();
        Equal(true, recovered.Achievements!.Items.Single().RewardClaimed);
        Equal("Ready", recovered.Modules!.Single().Sources.Single(s => s.SourceId == "achievements").State);
        f.Feed(WavesMessageIds.AchievementInfoResponse, new AchievementInfoResponse());
        var empty = await f.Flush(); Equal(0, empty.Achievements!.Items.Count); Equal(0, empty.Achievements.Groups.Count);
    }
    private static async Task TestAchievementBaselineAndIsolationAsync()
    {
        await using var f = new NotificationFixture(); f.Full();
        var before = await f.Flush();
        f.Feed(WavesMessageIds.AchievementProgressNotify, new AchievementProgressNotify { AchievementEntry = Achievement(1, 123) });
        Equal(before.UpdateCount, (await f.Flush()).UpdateCount);
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(1, 123)));
        var first = await f.Flush(); Equal("unclaimed", first.Achievements!.Items.Single().RewardStatus);
        var original = File.ReadAllText(first.OutputJsonPath!);
        f.Full(session: "second-player", player: 222);
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(1, 123, true)), "second-player", 222);
        Equal(true, (await f.Flush()).Achievements!.Items.Single().RewardClaimed);
        Equal(original, File.ReadAllText(first.OutputJsonPath!));
        f.Full(session: "same-player-new-login", player: 111);
        var pending = await f.Flush();
        Equal(true, pending.Achievements is null);
        Equal(false, pending.Modules!.Single().Counters.Any(c => c.Name == "achievements" && c.PlayerId == 111));
        f.Feed(WavesMessageIds.AchievementInfoResponse, AchievementBase(Achievement(8)), "same-player-new-login", 111);
        var next = await f.Flush(); Equal(8, next.Achievements!.Items.Single().Id);
        f.Feed(WavesMessageIds.AchievementProgressNotify, new AchievementProgressNotify { AchievementEntry = Achievement(8, 999, true) }, current: false);
        Equal(false, (await f.Flush()).Achievements!.Items.Single().RewardClaimed);
    }
}
