using System.Text;

namespace BillionCheckboxes.Models;

public record CheckboxesModel
{
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    
    public int TotalCheckboxes => Offset + Limit;

    public HashSet<int> CheckedIds { get; init; } = [];
    
    public string ToSignal()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.Append("{\'_boxes\':[");
        for (var i = Offset; i < TotalCheckboxes; i++)
        {
            stringBuilder.Append(CheckedIds.Contains(i) ? "true," : "false,");
        }
        
        stringBuilder.Remove(stringBuilder.Length - 1, 1); // Remove the last comma
        stringBuilder.Append("]}");
        return stringBuilder.ToString();
    }
}