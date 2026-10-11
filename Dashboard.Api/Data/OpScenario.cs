namespace Dashboard.Api.Data;

public class OpScenario
{
    public long Id { get; set; }

    public long AccountId { get; set; }

    /// <summary>A or B slot label.</summary>
    public string Slot { get; set; } = "A";

    public string Template { get; set; } = "";

    public string Title { get; set; } = "";

    public string ResultJson { get; set; } = "";

    public DateTimeOffset SavedAt { get; set; }
}
