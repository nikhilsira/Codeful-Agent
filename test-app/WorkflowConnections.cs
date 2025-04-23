using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LogicApps.Agent;

public class WorkflowConnections
{
    [JsonProperty]
    public Dictionary<string, JObject> ServiceProviderConnections { get; set; }

    [JsonProperty]
    public Dictionary<string, JObject> managedApiConnections { get; set; }

    [JsonProperty]
    public Dictionary<string, JObject> AgentConnections { get; set; }
}