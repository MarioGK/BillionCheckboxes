using System.Text.Json.Serialization;

namespace BillionCheckboxes.Models;

public class CheckBoxPaginationSignal
{
    /*[JsonPropertyName("id")]
    public uint Id { get; set; }
    
    [JsonPropertyName("value")]
    public bool Value { get; set; }*/
    
    [JsonPropertyName("limit")]
    public int Limit { get; set; }
    
    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}