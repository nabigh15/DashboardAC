using DashboardAC.Models;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using System;
using System.Threading.Tasks;

namespace DashboardAC.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;

        // HttpClient dikonfigurasi di sini, hanya satu kali.
        private static readonly HttpClient client = new HttpClient()
        {
            // Atur timeout default untuk semua request dari client ini (misal: 15 detik)
            Timeout = TimeSpan.FromSeconds(15)
        };

        public HomeController(IConfiguration configuration)
        {
            _configuration = configuration;
            // Pengaturan timeout dihapus dari constructor
        }

        public IActionResult Index() { return View(); }

        private readonly List<(TimeSpan Start, TimeSpan End)> RegularDayBreakTimes = new List<(TimeSpan, TimeSpan)>
        {
            (new TimeSpan(9, 30, 0), new TimeSpan(9, 35, 0)),
            (new TimeSpan(11, 40, 0), new TimeSpan(12, 25, 0)),
            (new TimeSpan(14, 30, 0), new TimeSpan(14, 35, 0))
        };
        private readonly List<(TimeSpan Start, TimeSpan End)> FridayBreakTimes = new List<(TimeSpan, TimeSpan)>
        {
            (new TimeSpan(9, 30, 0), new TimeSpan(9, 35, 0)),
            (new TimeSpan(11, 50, 0), new TimeSpan(13, 15, 0)),
            (new TimeSpan(14, 30, 0), new TimeSpan(14, 35, 0))
        };

        [HttpGet]
        public async Task<IActionResult> GetDashboardData()
        {
            var viewModel = new DashboardViewModel();
            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                viewModel.LineCU = await GetLineDataAsync(connection, "MCH1-01");
                viewModel.LineCS = await GetLineDataAsync(connection, "MCH1-02");
                viewModel.LossTimeCU = await GetLossTimeDataAsync(connection, "MCH1-01");
                viewModel.LossTimeCS = await GetLossTimeDataAsync(connection, "MCH1-02");
            }
            return Json(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> GetMachineEfficiencyData()
        {
            try
            {
                string apiUrl = "http://10.83.33.125:8003/APIProduction/api/MachineEfficiency/data";
                HttpResponseMessage response = await client.GetAsync(apiUrl);
                response.EnsureSuccessStatusCode();
                string responseBody = await response.Content.ReadAsStringAsync();
                return Content(responseBody, "application/json");
            }
            catch (TaskCanceledException ex)
            {
                return StatusCode(500, $"Request to API timed out: {ex.Message}");
            }
            catch (HttpRequestException e)
            {
                string errorMessage = $"Error fetching data from API: {e.Message}";
                if (e.InnerException != null)
                {
                    errorMessage += $" | Inner Exception: {e.InnerException.Message}";
                }
                return StatusCode(500, errorMessage);
            }
        }

        private async Task<LineData> GetLineDataAsync(SqlConnection connection, string machineCode)
        {
            var lineData = new LineData();
            var oeeSql = @"
        SELECT TOP 1
            TargetUnit AS TotalPlan,
            TotalUnit AS TotalActual
        FROM OEESN
        WHERE CONVERT(date, SDate) = CONVERT(date, GETDATE())
          AND MachineCode = @MachineCode
        ORDER BY ID DESC;";
            var oeeData = await connection.QuerySingleOrDefaultAsync<LineData>(oeeSql, new { MachineCode = machineCode });
            if (oeeData != null)
            {
                lineData.TotalPlan = oeeData.TotalPlan;
                lineData.TotalActual = oeeData.TotalActual;
            }
            var dailyPlanQuery = @"
        WITH LatestPlan AS (
            SELECT TOP 1 PlanId FROM ProductionRecords WHERE MachineCode = @MachineCode ORDER BY ID DESC
        )
        SELECT ISNULL(SUM(Quantity), 0) FROM ProductionRecords
        WHERE PlanId = (SELECT PlanId FROM LatestPlan)
          AND MachineCode = @MachineCode;";
            lineData.DailyPlan = await connection.ExecuteScalarAsync<int?>(dailyPlanQuery, new { MachineCode = machineCode }) ?? 0;
            var defectQuery = @"
            SELECT COUNT(*) FROM NG_RPTS 
            WHERE CONVERT(date, Date) = CONVERT(date, GETDATE()) 
            AND MachineCode = @MachineCode;";
            int totalDefects = await connection.ExecuteScalarAsync<int?>(defectQuery, new { MachineCode = machineCode }) ?? 0;
            lineData.TotalDefects = totalDefects;
            if (lineData.TotalActual > 0)
            {
                double defectRate = (double)totalDefects / lineData.TotalActual * 100.0;
                lineData.QualityRate = 100.0 - defectRate;
            }
            else
            {
                lineData.QualityRate = 100.0;
            }
            lineData.QualityRate = Math.Max(0, lineData.QualityRate);
            return lineData;
        }

        private async Task<LossTimeData> GetLossTimeDataAsync(SqlConnection connection, string machineCode)
        {
            var data = new LossTimeData();
            TimeSpan workDayStart = new TimeSpan(7, 7, 0);
            TimeSpan workDayEnd = new TimeSpan(15, 55, 0);
            var today = DateTime.Now;
            var breakTimes = (today.DayOfWeek == DayOfWeek.Friday) ? FridayBreakTimes : RegularDayBreakTimes;
            data.BreakTimes = breakTimes.Select(b => new BreakTime { Start = b.Start, End = b.End }).ToList();
            var currentTime = today.TimeOfDay;
            if (currentTime > workDayStart)
            {
                var effectiveEndTime = (currentTime > workDayEnd) ? workDayEnd : currentTime;
                int totalDuration = (int)(effectiveEndTime - workDayStart).TotalMinutes;
                int totalRest = breakTimes.Sum(b =>
                {
                    var restStart = b.Start;
                    var restEnd = b.End;
                    if (effectiveEndTime <= restStart || workDayStart >= restEnd) return 0;
                    var effectiveRestStart = (restStart > workDayStart) ? restStart : workDayStart;
                    var effectiveRestEnd = (restEnd < effectiveEndTime) ? restEnd : effectiveEndTime;
                    var duration = (effectiveRestEnd - effectiveRestStart).TotalMinutes;
                    return (int)Math.Max(0, duration);
                });
                data.WorkingTime = totalDuration - totalRest;
            }
            data.WorkingTime = Math.Max(0, data.WorkingTime);
            var lossEventsQuery = @"
    SELECT 
        DATEPART(hour, Time) as Hour,
        DATEPART(minute, Time) as StartMinute,
        LossTime as DurationSeconds
    FROM AssemblyLossTime
    WHERE 
        CONVERT(date, Date) = CONVERT(date, GETDATE()) 
        AND MachineCode = @MachineCode;";
            var allLossEvents = await connection.QueryAsync<dynamic>(lossEventsQuery, new { MachineCode = machineCode });
            int totalLossSeconds = 0;
            for (int hour = 7; hour < 16; hour++)
            {
                data.HourlyEvents[hour] = new List<LossEvent>();
            }
            foreach (var ev in allLossEvents)
            {
                var eventTime = new TimeSpan((int)ev.Hour, (int)ev.StartMinute, 0);
                bool isDuringBreak = breakTimes.Any(b => eventTime >= b.Start && eventTime < b.End);
                if (isDuringBreak)
                {
                    continue;
                }
                int hour = (int)ev.Hour;
                if (data.HourlyEvents.ContainsKey(hour))
                {
                    data.HourlyEvents[hour].Add(new LossEvent
                    {
                        StartMinute = (int)ev.StartMinute,
                        DurationMinutes = (int)Math.Ceiling((decimal)ev.DurationSeconds / 60)
                    });
                }
                totalLossSeconds += (int)ev.DurationSeconds;
            }
            data.LossTime = totalLossSeconds / 60;
            data.LoadTime = data.WorkingTime - data.LossTime;
            data.LoadTime = Math.Max(0, data.LoadTime);
            return data;
        }
    }
}
