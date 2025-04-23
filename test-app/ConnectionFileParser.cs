using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LogicApps.Agent;

public static class ConnectionFileParser
{
    public static WorkflowConnections ParseConnectionFile(string filePath)
    {
        var jsonString = File.ReadAllText(filePath);
        // var jObject = JObject.Parse(jsonString);

        var obj = JsonConvert.DeserializeObject<WorkflowConnections>(jsonString);

        return obj;
    }

    public static AgentConnection GetAgentConnection(string connectionName)
    {
        var jsonString = File.ReadAllText("connections.json");
        var obj = JsonConvert.DeserializeObject<WorkflowConnections>(jsonString);

        return new AgentConnection
        {
            Endpoint = (string)obj.AgentConnections[connectionName]["endpoint"],
            ApiKey = (string)obj.AgentConnections[connectionName]["authentication"]["key"],
        };
    }
}
