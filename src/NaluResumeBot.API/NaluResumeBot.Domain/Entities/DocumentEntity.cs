namespace NaluResumeBot.Domain.Entities;

public class DocumentEntity : BaseEntity
{
    public string FileName { get; private set; }
    public string DiscordChannelId { get; private set; }
    public string? DiscordMessageId { get; private set; }   
    public DocumentStatus Status { get; private set; }
    public string? ErrorMessage { get; private set; }
    
    private DocumentEntity() : base() {}

    public DocumentEntity(string filename, string discordChannelId, string discordMessageId) : base()
    {
        if (string.IsNullOrEmpty(filename))
        {
            throw new ArgumentNullException("O nome do arquivo é obrigatorio");
        }

        FileName = filename;
        DiscordChannelId = discordChannelId;
        DiscordMessageId = discordMessageId;
        Status = new DocumentStatus();  
    }

    public void MarkAsExtracting()
    {
        Status = DocumentStatus.Extracting;
        UpdateTimestamp();
    }

    public void MarkAsProcessing()
    {
        Status = DocumentStatus.Processing;
        UpdateTimestamp();
    }
    
    public void MarkAsDone()
    {
        Status = DocumentStatus.Done;
        UpdateTimestamp();
    }
    
    public void MarkAsError()
    {
        Status = DocumentStatus.Error;
        UpdateTimestamp();
    }
}