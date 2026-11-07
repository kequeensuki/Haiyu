using Haiyu.ABI.Protocol;
using Haiyu.ABI.Services.GameDataExport.Domains.Mail;
using Haiyu.ABI.Services.GameDataExport.Protocol;

namespace Haiyu.ABI.Services.GameDataExport.Adapters.Mail;

internal sealed class MailMessageAdapter : CollectionMessageAdapter<string, MailState, MailChange>
{
    internal MailMessageAdapter() : base("mails", mails => new MailCollection(mails), mail =>
        512 + 2L * (mail.Id.Length + mail.Title.Length + mail.Content.Length + mail.Sender.Length) + 48L * mail.Attachments.Count)
    {
        InitialFullThenMerge(WavesMessageIds.MailInfosNotify, 5, Read, mail => mail.Id, mail => new MailSynced(mail));
        On(WavesMessageIds.MailAddNotify, 0, (m, c) => c.Add(Read(m.Message(4)
            ?? throw new InvalidDataException("新邮件通知缺少邮件对象。"))));
        On(WavesMessageIds.MailDeleteNotify, 0, (m, c) => c.Remove(RequiredId(m.String(6))));
        On(WavesMessageIds.MailDeleteResponse, 7, (m, c) => Each(m.Strings(11), id => c.Remove(RequiredId(id))));
        On(WavesMessageIds.MailReadResponse, 5, (m, c) => c.Update(RequiredId(m.String(15)),
            new MailReadChanged(m.Int32(8), m.Int64(13), m.Int64(10))));
        // SuccessIdMap 的值是该封邮件的新状态位，不是每封邮件的错误码。
        On(WavesMessageIds.MailGetAttachmentResponse, 14, (m, c) => Each(m.Int32Map(4), entry =>
            c.Update(RequiredId(entry.Key), new MailAttachmentsChanged(entry.Value))));
        On(WavesMessageIds.MailFavoriteResponse, 3, (m, c) => c.Update(RequiredId(m.String(15)),
            new MailFavoriteChanged(m.Int32(4), m.Int64(8))));
    }
    private static string RequiredId(string id) => !string.IsNullOrEmpty(id) ? id
        : throw new InvalidDataException("邮件标识为空。");
    private static MailState Read(ProtocolMessage m) => new(RequiredId(m.String(1)), m.Int64(2), m.Int64(3), m.Int32(4),
        m.Int32(5), m.String(6), m.String(7), m.String(8), m.Int32(9), m.Int32(10),
        Array.AsReadOnly(m.Messages(11).Select(item => new MailAttachment(item.Int32(1), item.Int32(2))).ToArray()),
        m.Int32(12), m.Int64(13), m.Bool(14));
}
