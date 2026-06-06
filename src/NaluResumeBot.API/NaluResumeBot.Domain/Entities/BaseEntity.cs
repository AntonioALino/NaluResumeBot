namespace NaluResumeBot.Domain.Entities;

public class BaseEntity
{
    public Guid Id { get; protected set; }
    public DateTime CreatedAt {get; protected set;}
    public DateTime? UpdatedAt { get; protected set; }
    
    protected  BaseEntity()
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
    }
    
    public void UpdateTimestamp()
    {
        UpdatedAt = DateTime.UtcNow;
    }
}