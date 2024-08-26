namespace App.Shared;

public interface ICommand;

public sealed class TerminateSelf : ICommand
{
    public static readonly TerminateSelf Instance = new();
    private TerminateSelf() { }
}

public sealed class ReportState : ICommand
{
    public static readonly ReportState Instance = new();
    private ReportState() { }
}

public sealed class GetReport
{
    public static readonly GetReport Instance = new();
    private GetReport() { }
}

public sealed record Report(ReportRow[] Rows);

public sealed record ReportRow(string Name, long LastSequenceNumber, long Checksum)
{
    public static readonly ReportRowNameComparer Comparer = new();
}

public sealed class ReportRowNameComparer : IComparer<ReportRow>
{
    public int Compare(ReportRow? x, ReportRow? y)
    {
        if (x is null && y is null) return 0;
        if(x is null) return -1;
        if(y is null) return 1;
        return string.Compare(x.Name, y.Name, StringComparison.InvariantCultureIgnoreCase);
    }
}