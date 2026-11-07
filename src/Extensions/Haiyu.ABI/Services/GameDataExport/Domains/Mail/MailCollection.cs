using Haiyu.ABI.Services.GameDataExport.Aggregation.Collections;
using Haiyu.ABI.Services.GameDataExport.Aggregation.Contracts;

namespace Haiyu.ABI.Services.GameDataExport.Domains.Mail;

internal sealed class MailCollection(IEnumerable<MailState> mails)
    : DataCollectionBase<string, MailState, MailChange>(mails, mail => mail.Id,
        key => !string.IsNullOrEmpty(key), StringComparer.Ordinal)
{
    // 客户端 AddMail/MakeMailInstance 按 ID 替换完整对象；同 ID 通知也可能是服务器修订。
    public override ChangeResult Add(MailState mail) => Replace(mail.Id, mail);
    // 登录列表同样按完整对象合并，不按缺席删除。
    public override ChangeResult Update(string key, MailChange change) => change is MailSynced synced
        ? Replace(key, synced.Value) : base.Update(key, change);
    protected override MailState Merge(MailState current, MailChange change) => change switch
    {
        MailReadChanged read => current with { State = read.State, ReadTime = read.ReadTime, ExpiryTime = read.ExpiryTime },
        MailAttachmentsChanged claim => current with { State = claim.State },
        MailFavoriteChanged favorite => current with { State = favorite.State, ExpiryTime = favorite.ExpiryTime },
        _ => throw new InvalidDataException("不支持的邮件变更。")
    };
    protected override bool Equivalent(MailState first, MailState second) => ReferenceEquals(first, second)
        || (first with { Attachments = second.Attachments } == second && first.Attachments.SequenceEqual(second.Attachments));
}
