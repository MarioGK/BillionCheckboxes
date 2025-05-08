using System.Text;

namespace BillionCheckboxes.Models;

public record CheckboxesModel
{
    public required int StartId { get; init; }
    public required int Amount { get; init; }
    
    public int TotalCheckboxes => StartId + Amount;

    public HashSet<int> CheckedIds { get; init; } = [];
    
    public string ToSignal()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.Append("{\'boxes\':[");
        for (int i = StartId; i < TotalCheckboxes; i++)
        {
            stringBuilder.Append(CheckedIds.Contains(i) ? "true," : "false,");
        }
        
        stringBuilder.Remove(stringBuilder.Length - 1, 1); // Remove the last comma
        stringBuilder.Append("]}");
        return stringBuilder.ToString();
    }
}