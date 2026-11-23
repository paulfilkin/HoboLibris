namespace HoboLibris.ViewModels
{
    public enum RowStatus
    {
        Idle,
        Queued,
        Working,
        Failed,
    }

    /// <summary>What a list row's copy or download is doing right now.</summary>
    public record RowState(RowStatus Status, double Progress = 0, string? Error = null)
    {
        public static readonly RowState Idle = new(RowStatus.Idle);
        public static readonly RowState Queued = new(RowStatus.Queued);
        public static RowState Working(double progress) => new(RowStatus.Working, progress);
        public static RowState Failed(string error) => new(RowStatus.Failed, 0, error);
    }
}
