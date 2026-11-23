using HoboLibris.Assistant;
using HoboLibris.ViewModels;

namespace HoboLibris.Tests
{
    public class ModelListTests
    {
        [Fact]
        public void ReadsAnOpenAiStyleModelList()
        {
            var json = """{"object":"list","data":[{"id":"qwen3:8b","object":"model"},{"id":"llama3.1:latest"},{"id":"qwen3:8b"},{"name":"no id"}]}""";
            Assert.Equal(new[] { "llama3.1:latest", "qwen3:8b" }, ChatProviders.ParseModels(json));
            Assert.Empty(ChatProviders.ParseModels("not json"));
            Assert.Empty(ChatProviders.ParseModels("{}"));
        }

        [Theory]
        [InlineData(ModelProvider.OpenAICompatible, "http://localhost:11434/v1", "http://localhost:11434/v1/models")]
        [InlineData(ModelProvider.OpenAICompatible, "http://localhost:1234/v1/chat/completions", "http://localhost:1234/v1/models")]
        [InlineData(ModelProvider.OpenAI, "", "https://api.openai.com/v1/models")]
        [InlineData(ModelProvider.Anthropic, "", "https://api.anthropic.com/v1/models?limit=100")]
        [InlineData(ModelProvider.Anthropic, "https://proxy.example.org/v1/messages", "https://proxy.example.org/v1/models?limit=100")]
        public void AsksEachServiceWhereItListsItsModels(ModelProvider provider, string endpoint, string expected)
        {
            Assert.Equal(expected, ChatProviders.ModelsUrl(provider, endpoint)!.AbsoluteUri);
            Assert.Null(ChatProviders.ModelsUrl(ModelProvider.OpenAICompatible, ""));
        }

        [Theory]
        [InlineData("gpt-5", true)]
        [InlineData("gpt-4o-mini", true)]
        [InlineData("o3", true)]
        [InlineData("gpt-4o-realtime-preview", false)]
        [InlineData("gpt-image-1", false)]
        [InlineData("text-embedding-3-small", false)]
        [InlineData("whisper-1", false)]
        [InlineData("tts-1", false)]
        public void OffersOnlyOpenAisChatModels(string id, bool offered)
        {
            Assert.Equal(offered, ChatProviders.IsOpenAiChatModel(id));
        }

        [Fact]
        public async Task WithoutAKeyAHostedServiceIsntAsked()
        {
            var (reached, models) = await ChatProviders.ListModelsAsync(ModelProvider.Anthropic, "", "");
            Assert.False(reached);
            Assert.Empty(models);
        }

        [Fact]
        public void AMissingModelNamesTheOnesThere()
        {
            Assert.Null(AssistantViewModel.MissingModel("qwen3:8b", new[] { "qwen3:8b" }));
            Assert.Null(AssistantViewModel.MissingModel("QWEN3:8B", new[] { "qwen3:8b" }));
            Assert.Null(AssistantViewModel.MissingModel("anything", Array.Empty<string>()));
            Assert.Contains("qwen3:8b", AssistantViewModel.MissingModel("qwen2.5", new[] { "qwen3:8b" }));
            var several = AssistantViewModel.MissingModel("qwen2.5", new[] { "llama3.1", "qwen3:8b" })!;
            Assert.Contains("llama3.1, qwen3:8b", several);
            Assert.NotNull(AssistantViewModel.MissingModel("", new[] { "qwen3:8b" }));
        }
    }
}
