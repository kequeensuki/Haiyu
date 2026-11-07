using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Achievements;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Achievements;

internal sealed class AchievementMessageAdapter : CollectionMessageAdapter<(int Kind, int Id), AchievementState, AchievementChanged>
{
    internal AchievementMessageAdapter() : base("achievements", items => new AchievementCollection(items), _ => 256)
    {
        Full(WavesMessageIds.AchievementInfoResponse, ReadFull);
        On(WavesMessageIds.AchievementUpdateResponse, 1, (m, c) => Each(m.Messages(12), entry =>
            { var value = ReadEntry(entry); return c.Update(Key(value), new(value)); }));
        On(WavesMessageIds.AchievementProgressNotify, 0, (m, c) =>
        {
            var value = ReadEntry(Required(m, 1)); return c.Update(Key(value), new(value));
        });
        On(WavesMessageIds.AchievementListProgressNotify, 0, (m, c) => Each(m.Messages(10), entry =>
            { var value = ReadEntry(entry); return c.Update(Key(value), new(value)); }));
        On(WavesMessageIds.AchievementGroupProgressNotify, 0, (m, c) =>
        {
            var value = ReadGroup(Required(m, 5)); return c.Update(Key(value), new(value));
        });
        On(WavesMessageIds.AchievementCountChangeNotify, 0, (m, c) =>
            c.Update((0, 0), new(new(AchievementKind.Summary, 0, FinishCount: m.Int32(13), StarCount: m.Int32(14)))));
        // 官方回调只检查错误码；领取/完成事实以 Entry/Group 的状态通知为准。
        Ignore(WavesMessageIds.AchievementReceiveResponse, 3);
        Ignore(WavesMessageIds.MulAchievementReceiveResponse, 3);
        Ignore(WavesMessageIds.AchievementFinishResponse, 8);
    }
    private static (int Kind, int Id) Key(AchievementState value) => ((int)value.Kind, value.Id);
    private static ProtocolMessage Required(ProtocolMessage message, int field) => message.Message(field)
        ?? throw new InvalidDataException("成就通知缺少对象。");
    private static AchievementState ReadGroup(ProtocolMessage m) =>
        new(AchievementKind.Group, m.Int32(1), m.UInt32(2), m.Bool(3));
    private static AchievementState ReadEntry(ProtocolMessage m, int? groupId = null) =>
        new(AchievementKind.Entry, m.Int32(1), m.UInt32(2), m.Bool(3),
            m.Message(4)?.Int32(1), m.Message(4)?.Int32(2), groupId);
    private static IEnumerable<AchievementState> ReadFull(ProtocolMessage message)
    {
        yield return new(AchievementKind.Summary, 0, FinishCount: message.Int32(1), StarCount: message.Int32(4));
        foreach (var info in message.Messages(9))
        {
            var group = ReadGroup(Required(info, 1));
            yield return group;
            foreach (var entry in info.Messages(2)) yield return ReadEntry(entry, group.Id);
        }
    }
}
