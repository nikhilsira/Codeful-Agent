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
using Kusto.Cloud.Platform.Utils;

namespace LogicApps.Agent
{
    public static class ReviewAgentFunction
    {
        private static Kernel AgentKernel { get; set; }
        private static ChatHistory ChatHistory { get; set; }

        [FunctionName("InvokeReviewAgent")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {

            var requestStream = await req.Content.ReadAsStringAsync();
            var reviewAgentInput = JsonConvert.DeserializeObject<ReviewAgentInput>(requestStream);

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("ReviewAgentOrchestrator", input: reviewAgentInput);

            //log.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
            
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

        [FunctionName("ReviewAgentOrchestrator")]
        public static async Task<string> AgentOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var reviewAgentInput = context.GetInput<ReviewAgentInput>();

            log.LogInformation("Start Review agent orchestrator with input." + reviewAgentInput.ToString());

            return await ReviewAgentFunction.ReviewAgentLoop(
                connectionName: "agent",
                deploymentId: "gpt-4o",
                agentTools: new[] { ReviewAgentFunction.GetSalesDataByCountry, ReviewAgentFunction.GetSalesDataByProduct, ReviewAgentFunction.GetReportingPolicyDoc },
                reviewAgentInput: reviewAgentInput,
                context: context,
                log: log);
        }

        private static async Task<string> ReviewAgentLoop(
            string connectionName,
            string deploymentId,
            AgentTool[] agentTools,
            ReviewAgentInput reviewAgentInput,
            IDurableOrchestrationContext context,
            ILogger log)
        {
            var agentConnection = ConnectionFileParser.GetAgentConnection(connectionName);

            var userMessage = "Below is the draft of the sales performance report for the time period "+reviewAgentInput.ReportPeriod+"\n\n``\n"+reviewAgentInput.BodyDraftReport+"\n``";

            int iteration = await context.CallActivityAsync<int>("InitializeReviewAgentKernelAndQueueUserMessage", (deploymentId, agentConnection.Endpoint, agentConnection.ApiKey, userMessage));

            var result = await ReviewAgentFunction
                .GetAgentResponse(context: context, log: log, iteration: iteration);

            //log.LogInformation("Agent response: {response}", result);
            return result;
        }

        [FunctionName("InitializeReviewAgentKernelAndQueueUserMessage")]
        public static async Task<int> QueueUserMessageActivity(
            [ActivityTrigger] IDurableActivityContext context)
        {
            var (deploymentId, endpoint, apiKey, userMessage) = context.GetInput<(string, string, string, string)>();

            int iteration = GlobalChatHistory.GetNextIteration();

            ReviewAgentFunction.InitializeAgentKernel(
                deploymentId: deploymentId,
                endpoint: endpoint,
                connectionKey: apiKey,
                iteration: iteration);

            ChatHistory.AddUserMessage(userMessage);

            GlobalChatHistory.AddMessage(
                type: "Content",
                role: "User",
                message: userMessage,
                iteration: iteration);

            return iteration;
        }

        [FunctionName("GetReviewerChatHistory")]
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

        [FunctionName("PostReviewerFunctionResult")]
        public static async Task PostFunctionResultActivity([ActivityTrigger] IDurableActivityContext context)
        {
            var (content, funcName, funcId, iteration) = context.GetInput<(string, string, string, int)>();
 
            var functionResult = new FunctionResultContent(
                functionName: funcName,
                id: funcId,
                result: content);
 
            ChatHistory.Add(functionResult.ToChatMessage());

            GlobalChatHistory.AddMessage(
                type: "Content",
                role: "Assistant",
                message: content,
                iteration: iteration);
        }

        private static async Task<string> GetAgentResponse(IDurableOrchestrationContext context, ILogger log, int iteration)
        {
            var result = string.Empty;

            do
            {

                var chatMessage = await context.CallActivityAsync<ChatMessageActivityResult>("GetReviewerChatHistory", null);
                //log.LogInformation("Chat message: {message}", JsonConvert.SerializeObject(chatMessage));
                if (chatMessage.FunctionCalls.Any())
                {
                    foreach (var functionCall in chatMessage.FunctionCalls)
                    {
                        var reportContent = await ReviewAgentFunction.GetReport(functionCall.Name, functionCall.Arguments, context: context, log: log);
                        await context.CallActivityAsync("PostReviewerFunctionResult", (reportContent, functionCall.Name, functionCall.Id, iteration));
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

        private static void InitializeAgentKernel(string endpoint, string connectionKey, string deploymentId, int iteration)
        {
            var builder = Kernel.CreateBuilder();

            builder.AddAzureOpenAIChatCompletion(
                deploymentName: deploymentId,
                endpoint: endpoint,
                apiKey: connectionKey);

            AgentKernel = builder.Build();

            ChatHistory = new ChatHistory();

            string systemMessage = @"You are a report reviewer and is reponble for revewing salees perforance report. \n\nYou will be given a draft report and your responsibilities are the following\n\n1. \tVerify the accuracy of key figures (spot-check top-line revenue, margin, etc.).\n2.\tEnsures brand/legal compliance (e.g., disclaimers for forward-looking statements, correct tone, no unauthorized claims).\n3. Provides feedback if issues are found or missing disclaimers.\n4. Approves the report if everything is correct.\n\nyou are expected to provide the following :\n\nReview state: Approved if the draft require no changes\n\nOtherwise,\n\nReview state: Require Updates\nReview Feedback: <detailed feedback>\n\n\n\n";

            ChatHistory.AddSystemMessage(systemMessage);

            AgentKernel.ImportPluginFromFunctions(
                pluginName: ReviewAgentFunction.GetSalesDataByProduct.Name,
                functions: new[] { ReviewAgentFunction.GetSalesDataByProduct.KernelFunction });

            AgentKernel.ImportPluginFromFunctions(
                pluginName: ReviewAgentFunction.GetSalesDataByCountry.Name,
                functions: new[] { ReviewAgentFunction.GetSalesDataByCountry.KernelFunction });
            
            AgentKernel.ImportPluginFromFunctions(
                pluginName: ReviewAgentFunction.GetReportingPolicyDoc.Name,
                functions: new[] { ReviewAgentFunction.GetReportingPolicyDoc.KernelFunction });
        }

        public static async Task<string> GetReport(string functionName, KernelArguments arguments, IDurableOrchestrationContext context, ILogger log)
        {

            string aggName ="";
            string result = "";
            string blobName = "";

            switch (functionName)
            {
                case "get_sales_data_by_product":
                    aggName = "Product";
                    break;

                case "get_sales_data_by_country":
                    aggName = "Country";
                    break;

                case "get_reporting_policy_list":
                    blobName = "Sales_Reporting_Policy.md";
                    break;

                default:
                    throw new InvalidOperationException();
            }

            if(aggName.IsNotNullOrEmpty())
            {
                var dateInfo = JsonConvert.DeserializeObject<DateInfo>(arguments[functionName].ToString());
                var kusto = await context.ListKustoResultsPostAsync(connectionId: "kusto-1", body: new QueryAndListSchema
                {
                    Cluster = "https://logicapps.eastus.kusto.windows.net/",
                    Db = "process",
                    Csl = "let y = "+dateInfo.Year+";\nlet m = "+dateInfo.Month+";\nlet startDateString=strcat(y-1, \"-\", m, \"-01 00:00:00.0000000\");\nlet startDate=todatetime(startDateString);\nlet enddate=datetime_add('month', 1, startDate);\nSalesRecords\n|where DATETIME >= startDate  and DATETIME <= enddate\n| summarize sum(Sales) by "+aggName+""
                });
                //log.LogInformation("Kusto result: {result}", kusto.ToString());
                result = kusto.ToString();
            }
            if (blobName.IsNotNullOrEmpty())
            {
                var blob = await context.ReadBlobAsync(connectionId: "azureblob", input: new ReadBlobInput{
                    ContainerName = "policydocs",
                    BlobName = blobName,
                });
                //log.LogInformation("Blob result: {result}", blob.Content);
                result = blob.Content;
            }
            return result;
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

        public static readonly AgentTool GetReportingPolicyDoc = new AgentTool(
            name: "get_reporting_policy_list",
            description: "Get sales forecast report writing policy. The pociies are written in md format",
            schema: @"{}");
    }
}