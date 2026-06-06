namespace NaluResumeBot.Domain.Entities;

public class SummaryEntity : BaseEntity
{
    public Guid DocumentId { get; private set; }
    public string Content { get; private set; }

    private SummaryEntity() : base() { }

    public SummaryEntity(Guid documentId, string content) : base()
    {
        DocumentId = documentId;
        Content = content;
    }
}