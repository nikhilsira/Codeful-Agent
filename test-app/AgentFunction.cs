using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs;
using System.Net.Http;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using System.Net;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System;
using LogicApps.Connectors.Managed.Kusto;
using Newtonsoft.Json;
using LogicApps.Connectors.ServiceProviders.AzureBlob;

namespace LogicApps.Agent
{
    public static class AgentFunction
    {
        private static Kernel AgentKernel { get; set; }
        private static ChatHistory ChatHistory { get; set; }

        [FunctionName("Function1_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {

            var data = await req.Content.ReadAsStringAsync();
            ////var data = "Get me a report for the HyperCharge Batteries sales. Next, filter the data for United States and India.";

            var input = new AgentInput
            {
                Content = data
            };

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("AgentOrchestrator", input: input);

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
                    Content = new StringContent(status.Output.ToString(), System.Text.Encoding.UTF8, "application/json")
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

            var input = context.GetInput<AgentInput>();

            return await AgentFunction.AgentLoop(
                connectionName: "agent",
                deploymentId: "gpt-4o",
                agentTools: new[] { AgentFunction.GetSalesDataByCountry, AgentFunction.GetSalesDataByProduct },
                input: input,
                context: context,
                log: log);
        }

        private static async Task<string> AgentLoop(
            string connectionName,
            string deploymentId,
            AgentTool[] agentTools,
            AgentInput input,
            IDurableOrchestrationContext context,
            ILogger log)
        {
            var agentConnection = ConnectionFileParser.GetAgentConnection(connectionName);

            await context.CallActivityAsync("InitializeKernelAndQueueUserMessage", (deploymentId, agentConnection.Endpoint, agentConnection.ApiKey, input.Content));

            var result = await AgentFunction
                .GetAgentResponse(context: context, log: log);

            log.LogInformation("Agent response: {response}", result);

            return result;
        }

        [FunctionName("InitializeKernelAndQueueUserMessage")]
        public static async Task QueueUserMessageActivity(
            [ActivityTrigger] IDurableActivityContext context)
        {
            var (deploymentId, endpoint, apiKey, content) = context.GetInput<(string, string, string, string)>();
 
            AgentFunction.InitializeAgentKernel(
                deploymentId: deploymentId,
                endpoint: endpoint,
                connectionKey: apiKey);
 
            ChatHistory.AddUserMessage(content);
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
                FunctionCalls = calls.Select(c => (c.FunctionName, c.Id)).ToArray(),
                Content = chatMessage.Content
            };
        }

        public class ChatMessageActivityResult
        {
            public (string Name, string Id)[] FunctionCalls { get; set;}
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
                        var reportContent = await AgentFunction.GetReport(functionCall.Name, context: context, log: log);
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
                @"You are being tasked to write a detailed sales performance report.  You have access to monthly sales records for the last two years. \n\nYou may also be provided with a previous draft of a report with detailed feedback and you will update the previous draft to incorporate the feedback while adhering to the original guidance.\n\nYour responsibility\n\n�\tPulls raw data from the tools provided\n�\t Generate a structured report draft, providing:\n1.\tTextual summary of month-over-month changes, highlight bullet points.\n2.\tData references (e.g., top-line revenue figures).\n3.\t(Optional) Basic chart suggestions or placeholders.\n\nYou will also be provide date range for the report. example March 2025 and any special instructions (e.g., �Focus on new product line,� �Compare to last year�s forecast�).\n\nYour draft report should contain the following\n1.\tText narrative (�April revenue reached $2.1M, up 5% from March...�).\n2.\tKey metrics (tables, bullet points).\n3.\tReferences to raw data (so the Reviewer can spot-check if needed).\n");

            AgentKernel.ImportPluginFromFunctions(
                pluginName: AgentFunction.GetSalesDataByProduct.Name,
                functions: new[] { AgentFunction.GetSalesDataByProduct.KernelFunction });

            AgentKernel.ImportPluginFromFunctions(
                pluginName: AgentFunction.GetSalesDataByCountry.Name,
                functions: new[] { AgentFunction.GetSalesDataByCountry.KernelFunction });
        }

        public static async Task<string> GetReport(string functionName,IDurableOrchestrationContext context, ILogger log)
        {

            string blobName;

            switch (functionName)
            {
                case "get_sales_data_by_product":
                    blobName = "getSalesDataByProduct.json";
                    break;

                case "get_sales_data_by_country":
                    blobName = "getSalesDataByCountry.json";
                    break;

                default:
                    throw new InvalidOperationException();
            }

            var blob = await context.ReadBlobAsync(connectionId: "azureblob", input: new ReadBlobInput
            {
                ContainerName = "salesdata",
                BlobName = blobName,
            });

            log.LogInformation("Blob content: {content}", blob.Content);

            return blob.Content;
        }

        public static readonly AgentTool GetSalesDataByProduct = new AgentTool(
            name: "get_sales_data_by_product",
            description: "Get the sales data by product",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""query"": {
                        ""type"": ""string"",
                        ""description"": ""The Kusto query""
                    }
                }
            }");

        public static readonly AgentTool GetSalesDataByCountry = new AgentTool(
            name: "get_sales_data_by_country",
            description: "Get the sales data by country",
            schema: @"{
                ""type"": ""object"",
                ""properties"": {
                    ""query"": {
                        ""type"": ""string"",
                        ""description"": ""The Kusto query""
                    }
                }
            }");
    }
}
