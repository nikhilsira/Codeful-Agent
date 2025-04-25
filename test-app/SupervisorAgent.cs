using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using System.Net;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Threading.Tasks;
using System.Linq;
using System;
using Newtonsoft.Json;
using LogicApps.Connectors.ServiceProviders.AzureBlob;
using LogicApps.Connectors.Managed.Kusto;
using Fluid.Ast;

namespace LogicApps.Agent
{
    public class SupervisorAgentInput 
    {
        [JsonProperty("report_period")]
        public string ReportPeriod { get; set;}
    }

    public static class SupervisorAgentFunction
    {
        private static Kernel AgentKernel { get; set; }
        private static ChatHistory ChatHistory { get; set; }

        [FunctionName("SupervisorAgent_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {

            var requestStream = await req.Content.ReadAsStringAsync();
            var writerAgentInput = JsonConvert.DeserializeObject<WriterAgentInput>(requestStream);

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("SupervisorAgentOrchestrator", input: writerAgentInput);

            log.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
            
            DurableOrchestrationStatus status;
            do
            {
                status = await starter.GetStatusAsync(instanceId);
                await Task.Delay(1000); // Wait for 1 second before checking the status again
            } while (status.RuntimeStatus == OrchestrationRuntimeStatus.Running || 
                    status.RuntimeStatus == OrchestrationRuntimeStatus.Pending);

            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(status.Output.ToString(), System.Text.Encoding.UTF8, "text/plain")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Orchestration did not complete successfully.")
            };
        }

        [FunctionName("SupervisorAgentOrchestrator")]
        public static async Task<string> AgentOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log.LogInformation("Start agent orchestrator.");

            var writerAgentInput = context.GetInput<SupervisorAgentInput>();

            return await AgentLoop(
                connectionName: "agent",
                deploymentId: "gpt-4o",
                agentTools: new[] { WriterAgent, ReviewerAgent },
                input: writerAgentInput,
                context: context,
                log: log);
        }

        private static async Task<string> AgentLoop(
            string connectionName,
            string deploymentId,
            AgentTool[] agentTools,
            SupervisorAgentInput input,
            IDurableOrchestrationContext context,
            ILogger log)
        {
            var agentConnection = ConnectionFileParser.GetAgentConnection(connectionName);

            var userMessage = "the requested report period is " + input.ReportPeriod;

            await context.CallActivityAsync("SupervisorInitializeKernelAndQueueUserMessage", (deploymentId, agentConnection.Endpoint, agentConnection.ApiKey, userMessage));

            var result = await GetAgentResponse(context: context, log: log);

            log.LogInformation("Agent response: {response}", result);

            return result;
        }

        [FunctionName("SupervisorInitializeKernelAndQueueUserMessage")]
        public static async Task QueueUserMessageActivity(
            [ActivityTrigger] IDurableActivityContext context)
        {
            var (deploymentId, endpoint, apiKey, userMessage) = context.GetInput<(string, string, string, string)>();
 
            InitializeAgentKernel(
                deploymentId: deploymentId,
                endpoint: endpoint,
                connectionKey: apiKey);

            ChatHistory.AddUserMessage(userMessage);
        }

        [FunctionName("SupervisorGetChatHistory")]
        public static async Task<ChatMessageActivityResult> GetChatHistoryActivity(
            [ActivityTrigger] IDurableActivityContext context
        )
        {
            var settings = new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.EnableKernelFunctions,
            };

            var chatCompletionService = AgentKernel.GetRequiredService<IChatCompletionService>();

            var chatMessage = await chatCompletionService.GetChatMessageContentAsync(ChatHistory, settings, AgentKernel);
            ChatHistory.Add(chatMessage);

            var calls = FunctionCallContent.GetFunctionCalls(chatMessage);

            return new ChatMessageActivityResult {
                FunctionCalls = calls.Select(c => (c.FunctionName, c.Id, c.Arguments)).ToArray(),
                Content = chatMessage.Content
            };
        }

        public class ChatMessageActivityResult
        {
            public (string Name, string Id, KernelArguments Arguments)[] FunctionCalls { get; set;}
            public string Content {get; set;}
        }

        [FunctionName("SupervisorPostFunctionResult")]
        public static async Task PostFunctionResultActivity([ActivityTrigger] IDurableActivityContext context)
        {
            var (content, funcName, funcId) = context.GetInput<(string, string, string)>();
 
            var functionResult = new FunctionResultContent(
                functionName: funcName,
                id: funcId,
                result: content);
 
            ChatHistory.Add(functionResult.ToChatMessage());
        }

        private static async Task<string> GetAgentResponse(IDurableOrchestrationContext context, ILogger log)
        {
            var result = string.Empty;

            do
            {

                var chatMessage = await context.CallActivityAsync<ChatMessageActivityResult>("SupervisorGetChatHistory", null);
                log.LogInformation("Chat message: {message}", JsonConvert.SerializeObject(chatMessage));
                if (chatMessage.FunctionCalls.Any())
                {
                    foreach (var functionCall in chatMessage.FunctionCalls)
                    {
                        var reportContent = await WriterAgentFunction.GetReport(functionCall.Name, functionCall.Arguments, context: context, log: log);
                        await context.CallActivityAsync("SupervisorPostFunctionResult", (reportContent, functionCall.Name, functionCall.Id));
                    }
                }
                else
                {
                    result = chatMessage.Content;
                    break;
                }
            } while (true);

            return result;
        }

        private static void InitializeAgentKernel(string endpoint, string connectionKey, string deploymentId)
        {
            var builder = Kernel.CreateBuilder();

            builder.AddAzureOpenAIChatCompletion(
                deploymentName: deploymentId,
                endpoint: endpoint,
                apiKey: connectionKey);

            AgentKernel = builder.Build();

            ChatHistory = new ChatHistory();

            ChatHistory.AddSystemMessage(@"You are a supervisor who  cordinates the work between different agents for producing a sales performance report. \n\nYou will be asked to produce a sales performance report for a given time period such as January 2025.\n\nYou will first ask the writer agent who can gather data from enterprise systems and draft a narrative report with business insights by passing time range and any specific instructions to produce a draft.\n\nOnce the writer produce a draft, you will send the draft to the reviewer agent who will either approve the report and request updates providing review feedback to be addressed. \n\nIf the report is approved by the reviewer you will send it to the publisher agent for publication.\n\nif the updates are requested, you will send this feedback to the reviewer and the reviewer will produce and updated draft which you will send to the reviwer.  You will continue this process until the report is approved. \n\n\n\n ");

            AgentKernel.ImportPluginFromFunctions(
                pluginName: WriterAgentFunction.GetSalesDataByProduct.Name,
                functions: new[] { WriterAgentFunction.GetSalesDataByProduct.KernelFunction });

            AgentKernel.ImportPluginFromFunctions(
                pluginName: WriterAgentFunction.GetSalesDataByCountry.Name,
                functions: new[] { WriterAgentFunction.GetSalesDataByCountry.KernelFunction });
        }

        public static async Task<string> GetReport(string functionName, KernelArguments arguments, IDurableOrchestrationContext context, ILogger log)
        {

            string orchName;

            switch (functionName)
            {
                case "writer_agent":
                    orchName = "Product";
                    break;

                case "reviewer_agent":
                    orchName = "Country";
                    break;

                default:
                    throw new InvalidOperationException();
            }

            return await context.CallSubOrchestratorAsync<string>(orchName, arguments[functionName]);

/*
            var dateInfo = JsonConvert.DeserializeObject<DateInfo>(arguments[functionName].ToString());
            var kusto = await context.ListKustoResultsPostAsync(connectionId: "kusto-1", body: new QueryAndListSchema
            {
                Cluster = "https://logicapps.eastus.kusto.windows.net/",
                Db = "process",
                Csl = "let y = "+dateInfo.Year+";\nlet m = "+dateInfo.Month+";\nlet startDateString=strcat(y-1, \"-\", m, \"-01 00:00:00.0000000\");\nlet startDate=todatetime(startDateString);\nlet enddate=datetime_add('month', 1, startDate);\nSalesRecords\n|where DATETIME >= startDate  and DATETIME <= enddate\n| summarize sum(Sales) by "+aggName+""
            });
            log.LogInformation("Kusto result: {result}", kusto.ToString());

            return kusto.ToString();
            */
        }

        public static readonly AgentTool WriterAgent = new AgentTool(
            name: "writer_agent",
            description: "The writer agent that can gather data from enterprise systems and draft a narrative report with business insights",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""report_period"": {
                        ""type"": ""string"",
                        ""description"": """"
                    },
                    ""previous_draft_feedback"": {
                        ""type"": ""string"",
                        ""description"": ""Feedback from the previous draft. If there is no previois draft, indicate that there is no feedback since there is no draft yet""
                    }
                }
            }");

        public static readonly AgentTool ReviewerAgent = new AgentTool(
            name: "reviewer_agent",
            description: "The reviewer agent who will either approve the report and request updates providing review feedback to be addressed",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""report_period"": {
                        ""type"": ""string"",
                        ""description"": ""The requested report period (for example january 2025)""
                    },
                    ""report_draft"": {
                        ""type"": ""string"",
                        ""description"": ""Draft of the report""
                    }
                }
            }");

        public static readonly AgentTool PublisherAgent = new AgentTool(
            name: "reviewer_agent",
            description: "The reviewer agent who will either approve the report and request updates providing review feedback to be addressed",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""final_draft"": {
                        ""type"": ""string"",
                        ""description"": ""Final draft of the report to be published""
                    }
                }
            }");
    }
}