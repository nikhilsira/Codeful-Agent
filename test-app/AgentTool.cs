namespace LogicApps.Agent
{
    using Microsoft.SemanticKernel;
    using Microsoft.SemanticKernel.ChatCompletion;
    using Microsoft.SemanticKernel.Connectors.OpenAI;

    public class AgentTool
    {
        public string Name { get; private set; }
        private string Schema { get; set; }
        private string Description { get; set; }
        public KernelFunction KernelFunction { get; private set; }

        public AgentTool(string name, string description, string schema)
        {
            this.Name = name;
            this.Schema = schema;
            this.Description = description;

            var parameter = new KernelParameterMetadata(name: this.Name)
            {
                Description = this.Description,
                ParameterType = typeof(string),
                Schema = KernelJsonSchema.Parse(this.Schema),
            };

            this.KernelFunction = KernelFunctionFactory.CreateFromMethod(
                method: () => { },
                description: this.Description,
                functionName: this.Name,
                parameters: new[] { parameter },
                returnParameter: null);
        }
    }
}