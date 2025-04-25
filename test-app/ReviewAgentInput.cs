using Newtonsoft.Json;

namespace LogicApps.Agent
{
    public class ReviewAgentInput
    {
        [JsonProperty("draft_report")]
        public string BodyDraftReport { get; set; }
        [JsonProperty("report_period")]
        public string ReportPeriod { get; set; }
    }
}
