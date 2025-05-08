using System.Text.Json.Serialization;

namespace BillionCheckboxes.Models;

public class CheckBoxSignal
{
    /*[JsonPropertyName("id")]
    public uint Id { get; set; }
    
    [JsonPropertyName("value")]
    public bool Value { get; set; }*/
    
    [JsonPropertyName("boxes")]
    public List<string> Boxes { get; set; }
}