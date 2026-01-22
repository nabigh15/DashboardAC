namespace DashboardAC.Models
{
    public class BreakTime
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
    }
    public class DashboardViewModel
    {
        public LineData LineCU { get; set; } = new LineData();
        public LineData LineCS { get; set; } = new LineData();
        public LossTimeData LossTimeCU { get; set; } = new LossTimeData();
        public LossTimeData LossTimeCS { get; set; } = new LossTimeData();
    }
}