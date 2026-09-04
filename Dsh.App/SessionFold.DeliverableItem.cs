namespace Dsh.App;

partial class SessionFold
{
    /// <summary>One produced-file fact: which seq produced it and at what path.</summary>
    public sealed record DeliverableItem(long Seq, string Path);
}
