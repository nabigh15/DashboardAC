namespace DashboardAC.Models
{
    public class LossTimeData
    {
        public int WorkingTime { get; set; }
        public int LossTime { get; set; }
        public int LoadTime { get; set; }
        public Dictionary<int, List<LossEvent>> HourlyEvents { get; set; } = new Dictionary<int, List<LossEvent>>();
        public List<BreakTime> BreakTimes { get; set; } = new List<BreakTime>();
    }
}