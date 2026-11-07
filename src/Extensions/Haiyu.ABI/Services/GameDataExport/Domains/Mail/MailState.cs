namespace Haiyu.ABI.Services.GameDataExport.Domains.Mail;

internal sealed record MailAttachment(int Id, int Count);
internal sealed record MailState(string Id, long ReceivedTime, long ReadTime, int State, int Level,
    string Title, string Content, string Sender, int ValidTime, int ReadValidTime,
    IReadOnlyList<MailAttachment> Attachments, int ConfigId, long ExpiryTime, bool IsIntervalMail);

internal abstract record MailChange;
internal sealed record MailSynced(MailState Value) : MailChange;
internal sealed record MailReadChanged(int State, long ReadTime, long ExpiryTime) : MailChange;
internal sealed record MailAttachmentsChanged(int State) : MailChange;
internal sealed record MailFavoriteChanged(int State, long ExpiryTime) : MailChange;
