using System.Diagnostics;
using HoboLibris.Assistant;

namespace HoboLibris.Tests
{
    public class OllamaTests
    {
        [Theory]
        [InlineData(ModelProvider.OpenAICompatible, "http://localhost:11434/v1", true)]
        [InlineData(ModelProvider.OpenAICompatible, "http://127.0.0.1:11434/v1", true)]
        [InlineData(ModelProvider.OpenAICompatible, "http://localhost:1234/v1", false)]
        [InlineData(ModelProvider.OpenAICompatible, "https://api.mistral.ai/v1", false)]
        [InlineData(ModelProvider.OpenAICompatible, "http://192.168.1.10:11434/v1", false)]
        [InlineData(ModelProvider.Anthropic, "http://localhost:11434/v1", false)]
        [InlineData(ModelProvider.OpenAICompatible, "", false)]
        public void KnowsOllamaOnThisComputer(ModelProvider provider, string endpoint, bool expected)
        {
            Assert.Equal(expected, OllamaServer.IsOllamaEndpoint(provider, endpoint));
        }

        [Fact]
        public void FindsTheFirstInstalledCopy()
        {
            var folder = TestFolder.Fresh("ollama-find");
            var missing = Path.Combine(folder, "a", "ollama.exe");
            var present = Path.Combine(folder, "ollama.exe");
            File.WriteAllText(present, "");
            Assert.Equal(present, OllamaServer.FindExecutable(new[] { missing, present }));
            Assert.Null(OllamaServer.FindExecutable(new[] { missing }));
            Assert.Contains(OllamaServer.Candidates(), c => c.EndsWith(Path.Combine("Programs", "Ollama", "ollama.exe")));
        }

        [Fact]
        public async Task StopsOnlyWhatItStarted()
        {
            using var server = new OllamaServer();
            Assert.False(server.StartedHere);
            server.Stop();

            // A stand-in for "ollama serve": a hidden program that would run for half a minute.
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            server.StartProcess(cmd, "/c ping -n 30 127.0.0.1 >nul");
            Assert.True(server.StartedHere);
            var children = ChildrenOf(Process.GetCurrentProcess().Id, "cmd.exe");
            Assert.NotEmpty(children);

            server.Stop();
            Assert.False(server.StartedHere);
            await Task.Delay(500);
            Assert.All(children, id => Assert.True(Exited(id)));
            // Nothing started here: nothing to stop, and another program's server is never touched.
            server.Stop();
        }

        [Fact]
        public async Task NothingAnswersOnAnUnusedPort()
        {
            Assert.False(await OllamaServer.IsRunningAsync("http://localhost:1/v1"));
            Assert.False(await OllamaServer.IsRunningAsync("not an address"));
        }

        [Fact]
        public async Task ARequestThatCantBeReadiedIsNotSent()
        {
            var host = new TestHost();
            var model = new ScriptedModel(ScriptedModel.Text("Hello"));
            var session = new AssistantSession(host, null)
            {
                ProviderFactory = _ => model,
                Prepare = _ => Task.FromResult<string?>("Ollama isn't running."),
            };

            await session.SendAsync("Hello");

            Assert.Equal(0, model.Calls);
            Assert.Equal("Ollama isn't running.", Assert.IsType<NoticeMessage>(session.Items[1]).Text);
            Assert.Empty(session.Turns);
            Assert.False(session.IsBusy);
        }

        private static List<int> ChildrenOf(int parent, string name)
        {
            var ids = new List<int>();
            using var search = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parent} AND Name = '{name}'");
            foreach (var p in search.Get())
            {
                ids.Add(Convert.ToInt32(p["ProcessId"]));
            }
            return ids;
        }

        private static bool Exited(int id)
        {
            try
            {
                using var p = Process.GetProcessById(id);
                return p.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}
