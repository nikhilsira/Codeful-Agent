namespace LogicApps.Agent;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;

using Newtonsoft.Json;

public class ChatMessageSummary
{
    [JsonProperty]
    public string messageEntryType { get; set; }

    [JsonProperty]
    public string role { get; set; }

    [JsonProperty]
    public DateTime timestamp { get; set; }

    [JsonProperty]
    public int iteration { get; set; }

    [JsonProperty]
    public ChatMessagePayload messageEntryPayload { get; set; }
}

public class ChatMessagePayload
{
    [JsonProperty]
    public string content { get; set; }
}

public class ChatHistoryResponse
{
    [JsonProperty]
    public ChatMessageSummary[] value { get; set; }
}
    
public class GlobalChatHistory
{
    private static List<ChatMessageSummary> Messages = new();
    private static int Counter = 0;

    [FunctionName("GetAllChatHistory")]
    public static HttpResponseMessage GetChatHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous)] HttpRequest req)
    {
        ChatMessageSummary[] messages; 
        lock(Messages) 
        {
            messages = Messages.ToArray();
        }
            
        var responseContent = new ChatHistoryResponse
        {
            value = messages
        };

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(responseContent), System.Text.Encoding.UTF8, "application/json")
        };
        response.Headers.Add("Access-Control-Allow-Origin", "*");

        return response;
    }

    public static int GetNextIteration()
    {
        return Interlocked.Increment(ref Counter);
    }

    public static void AddMessage(string type, string role, string message, int iteration)
    {
        lock(Messages)
        {
            Messages.Add(new ChatMessageSummary
            {
                messageEntryType = type,
                role = role,
                messageEntryPayload = new ChatMessagePayload { content = message },
                iteration = iteration,
                timestamp = DateTime.UtcNow
            });
        }
    }
}