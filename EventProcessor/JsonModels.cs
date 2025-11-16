using System.Text.Json.Serialization;

namespace EventProcessor
{
    public class Message
    {
        [JsonPropertyName("MessageID")] public Guid MessageID { get; set; }

        [JsonPropertyName("GeneratedDate")] public DateTimeOffset GeneratedDate { get; set; }

        [JsonPropertyName("Event")] public Event? Event { get; set; }
    }

    public class Event
    {
        [JsonPropertyName("ProviderEventID")] public long ProviderEventID { get; set; }

        [JsonPropertyName("EventName")] public string? EventName { get; set; }

        [JsonPropertyName("EventDate")] public DateTime EventDate { get; set; }

        [JsonPropertyName("OddsList")] public List<Odd>? OddsList { get; set; }
    }

    public class Odd
    {
        [JsonPropertyName("ProviderOddsID")] public long ProviderOddsID { get; set; }

        [JsonPropertyName("OddsName")] public string? OddsName { get; set; }

        [JsonPropertyName("OddsRate")] public double OddsRate { get; set; }

        [JsonPropertyName("Status")] public string? Status { get; set; }
    }
}