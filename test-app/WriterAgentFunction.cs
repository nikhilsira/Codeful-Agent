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
    public static class WriterAgentFunction
    {
        private static Kernel AgentKernel { get; set; }
        private static ChatHistory ChatHistory { get; set; }

        [FunctionName("Function1_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {

            var requestStream = await req.Content.ReadAsStringAsync();
            var writerAgentInput = JsonConvert.DeserializeObject<WriterAgentInput>(requestStream);

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("AgentOrchestrator", input: writerAgentInput);

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

        [FunctionName("AgentOrchestrator")]
        public static async Task<string> AgentOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log.LogInformation("Start agent orchestrator.");

            var writerAgentInput = context.GetInput<WriterAgentInput>();

            return await WriterAgentFunction.WriterAgentLoop(
                connectionName: "agent",
                deploymentId: "gpt-4o",
                agentTools: new[] { WriterAgentFunction.GetSalesDataByCountry, WriterAgentFunction.GetSalesDataByProduct },
                writerAgentInput: writerAgentInput,
                context: context,
                log: log);
        }

        private static async Task<string> WriterAgentLoop(
            string connectionName,
            string deploymentId,
            AgentTool[] agentTools,
            WriterAgentInput writerAgentInput,
            IDurableOrchestrationContext context,
            ILogger log)
        {
            var agentConnection = ConnectionFileParser.GetAgentConnection(connectionName);

            var userMessage = "*****Date range****\n"+writerAgentInput.ReportDateRange+"\n\nif there was a previous draft, they will be provided below. if they are present please incorporate the feedback while adhering to the original guidance.\n\n*****Previous Draft****\n``\n"+writerAgentInput.CurrentDraft+"\n``\n\n**** review feedback ***\n``\n"+writerAgentInput.CurrentDraftFeedback+"\n``";

            await context.CallActivityAsync("InitializeKernelAndQueueUserMessage", (deploymentId, agentConnection.Endpoint, agentConnection.ApiKey, userMessage));

            var result = await WriterAgentFunction
                .GetAgentResponse(context: context, log: log);

            log.LogInformation("Agent response: {response}", result);

            return result;
        }

        [FunctionName("InitializeKernelAndQueueUserMessage")]
        public static async Task QueueUserMessageActivity(
            [ActivityTrigger] IDurableActivityContext context)
        {
            var (deploymentId, endpoint, apiKey, userMessage) = context.GetInput<(string, string, string, string)>();
 
            WriterAgentFunction.InitializeAgentKernel(
                deploymentId: deploymentId,
                endpoint: endpoint,
                connectionKey: apiKey);

            ChatHistory.AddUserMessage(userMessage);
        }

        [FunctionName("GetChatHistory")]
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

        [FunctionName("PostFunctionResult")]
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

                var chatMessage = await context.CallActivityAsync<ChatMessageActivityResult>("GetChatHistory", null);
                log.LogInformation("Chat message: {message}", JsonConvert.SerializeObject(chatMessage));
                if (chatMessage.FunctionCalls.Any())
                {
                    foreach (var functionCall in chatMessage.FunctionCalls)
                    {
                        var reportContent = await WriterAgentFunction.GetReport(functionCall.Name, functionCall.Arguments, context: context, log: log);
                        await context.CallActivityAsync("PostFunctionResult", (reportContent, functionCall.Name, functionCall.Id));
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

            ChatHistory.AddSystemMessage(
                @"You are being tasked to write a detailed sales performance report.  You have access to monthly sales records for the last two years. \n\nYou may also be provided with a previous draft of a report with detailed feedback and you will update the previous draft to incorporate the feedback while adhering to the original guidance.\n\nYour responsibility\n\n•\tPulls raw data from the tools provided\n•\t Generate a structured report draft, providing:\n1.\tTextual summary of month-over-month changes, highlight bullet points.\n2.\tData references (e.g., top-line revenue figures).\n3.\t(Optional) Basic chart suggestions or placeholders.\n\nYou will also be provide date range for the report. example March 2025 and any special instructions (e.g., “Focus on new product line,” “Compare to last year’s forecast”).\n\nYour draft report should contain the following\n1.\tText narrative (“April revenue reached $2.1M, up 5% from March...”).\n2.\tKey metrics (tables, bullet points).\n3.\tReferences to raw data (so the Reviewer can spot-check if needed).\n");

            AgentKernel.ImportPluginFromFunctions(
                pluginName: WriterAgentFunction.GetSalesDataByProduct.Name,
                functions: new[] { WriterAgentFunction.GetSalesDataByProduct.KernelFunction });

            AgentKernel.ImportPluginFromFunctions(
                pluginName: WriterAgentFunction.GetSalesDataByCountry.Name,
                functions: new[] { WriterAgentFunction.GetSalesDataByCountry.KernelFunction });
        }

        public static async Task<string> GetReport(string functionName, KernelArguments arguments, IDurableOrchestrationContext context, ILogger log)
        {

            string aggName;

            switch (functionName)
            {
                case "get_sales_data_by_product":
                    aggName = "Product";
                    break;

                case "get_sales_data_by_country":
                    aggName = "Country";
                    break;

                default:
                    throw new InvalidOperationException();
            }

            var dateInfo = JsonConvert.DeserializeObject<DateInfo>(arguments[functionName].ToString());
            var kusto = await context.ListKustoResultsPostAsync(connectionId: "kusto-1", body: new QueryAndListSchema
            {
                Cluster = "https://logicapps.eastus.kusto.windows.net/",
                Db = "process",
                Csl = "let y = "+dateInfo.Year+";\nlet m = "+dateInfo.Month+";\nlet startDateString=strcat(y-1, \"-\", m, \"-01 00:00:00.0000000\");\nlet startDate=todatetime(startDateString);\nlet enddate=datetime_add('month', 1, startDate);\nSalesRecords\n|where DATETIME >= startDate  and DATETIME <= enddate\n| summarize sum(Sales) by "+aggName+""
            });
            log.LogInformation("Kusto result: {result}", kusto.ToString());

            return kusto.ToString();
        }
        public class DateInfo
        {
            public string Year { get; set; }
            public string Month { get; set; }
        }


        public static readonly AgentTool GetSalesDataByProduct = new AgentTool(
            name: "get_sales_data_by_product",
            description: "Get sales numbers aggregated by product for a given month and a year. ",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""Year"": {
                        ""type"": ""integer"",
                        ""description"": ""The year of the month to get the sales numbers""
                    },
                    ""month"": {
                        ""type"": ""integer"",
                        ""description"": ""month to get the sales number. The number should be between 1 and 12 where 1 is for January, 2 for February and so on""
                    }
                }
            }");

        public static readonly AgentTool GetSalesDataByCountry = new AgentTool(
            name: "get_sales_data_by_country",
            description: "Get sales numbers aggregated by country for a given month and a year.",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""Year"": {
                        ""type"": ""integer"",
                        ""description"": ""The year of the month to get the sales numbers""
                    },
                    ""month"": {
                        ""type"": ""integer"",
                        ""description"": ""month to get the sales number. The number should be between 1 and 12 where 1 is for January, 2 for February and so on""
                    }
                }
            }");
    }
}