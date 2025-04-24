using Newtonsoft.Json;

namespace LogicApps.Agent
{
    public class WriterAgentInput
    {
        [JsonProperty("current_draft")]
        public string CurrentDraft { get; set; }
        [JsonProperty("current_draft_feedback")]
        public string CurrentDraftFeedback { get; set; }
        [JsonProperty("report_date_range")]
        public string ReportDateRange { get; set; }
    }
}
