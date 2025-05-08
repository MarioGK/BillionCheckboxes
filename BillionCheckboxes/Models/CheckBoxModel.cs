using System.Text.Json.Serialization;

namespace BillionCheckboxes.Models;

public class CheckBoxModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("value")]
    public bool Value { get; set; }
}