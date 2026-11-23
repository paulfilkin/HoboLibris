using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using HoboLibris.Resources;

namespace HoboLibris.Assistant
{
    /// <summary>
    /// Starts and stops Ollama's server for the Assistant, with no window.  A server HoboLibris starts
    /// is put in a Windows job object that ends it when HoboLibris ends, even after a crash; a server
    /// started any other way (the Ollama app, a terminal) is never stopped.
    /// </summary>
    public sealed class OllamaServer : IDisposable
    {
        public const int Port = 11434;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

        private Process? _process;
        private IntPtr _job;

        /// <summary>Whether the address is Ollama's on this computer.</summary>
        public static bool IsOllamaEndpoint(ModelProvider provider, string endpoint) =>
            provider == ModelProvider.OpenAICompatible
            && Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            && uri.Port == Port
            && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

        /// <summary>Where Ollama may be installed, most likely first.</summary>
        public static IEnumerable<string> Candidates()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe");
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string path;
                try
                {
                    path = Path.Combine(dir.Trim().Trim('"'), "ollama.exe");
                }
                catch (ArgumentException)
                {
                    continue;
                }
                yield return path;
            }
        }

        /// <summary>The first candidate that exists, or null when Ollama isn't installed.</summary>
        public static string? FindExecutable(IEnumerable<string>? candidates = null) =>
            (candidates ?? Candidates()).FirstOrDefault(File.Exists);

        /// <summary>Whether a server answers on the address's host and port.</summary>
        public static async Task<bool> IsRunningAsync(string endpoint, CancellationToken cancel = default)
        {
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }
            try
            {
                using var response = await Http.GetAsync(new Uri($"{uri.Scheme}://{uri.Authority}/api/version"), cancel);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        /// <summary>A server HoboLibris started is still running.</summary>
        public bool StartedHere => _process is { HasExited: false };

        /// <summary>
        /// Starts "ollama serve" with no window and waits until it answers, at most 30 seconds.
        /// Returns null when it is running, or why it couldn't start.
        /// </summary>
        public async Task<string?> StartAsync(string endpoint, CancellationToken cancel = default)
        {
            if (await IsRunningAsync(endpoint, cancel))
            {
                return null;
            }
            var exe = FindExecutable();
            if (exe == null)
            {
                return Strings.Asst_OllamaMissing;
            }
            try
            {
                StartProcess(exe, "serve");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return ex.Message;
            }
            for (int i = 0; i < 60; i++)
            {
                if (_process!.HasExited)
                {
                    return string.Format(Strings.Asst_OllamaExited, _process.ExitCode);
                }
                if (await IsRunningAsync(endpoint, cancel))
                {
                    return null;
                }
                await Task.Delay(500, cancel);
            }
            return Strings.Asst_OllamaNoAnswer;
        }

        /// <summary>Starts a program with no window, tied to HoboLibris by the job object.  Used for tests too.</summary>
        public void StartProcess(string exe, string arguments)
        {
            Stop();
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            _process = Process.Start(info) ?? throw new InvalidOperationException("Windows didn't start the program.");
            TieToThisProcess(_process);
        }

        /// <summary>Stops a server HoboLibris started, with the model runners it started; anything else is left alone.</summary>
        public void Stop()
        {
            var process = _process;
            _process = null;
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Services.LogManager.Warn($"Ollama couldn't be stopped: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        public void Dispose()
        {
            Stop();
            if (_job != IntPtr.Zero)
            {
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
        }

        // ---- The job object: the server and its runners end when HoboLibris ends --------------------

        private void TieToThisProcess(Process process)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JobObjectExtendedLimitInformation
                    {
                        BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose },
                    };
                    var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, buffer, false);
                        SetInformationJobObject(_job, ExtendedLimitInformation, buffer, (uint)size);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                AssignProcessToJobObject(_job, process.Handle);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Without the job the server still stops when HoboLibris closes normally.
                Services.LogManager.Warn($"Ollama couldn't be tied to HoboLibris: {ex.Message}");
            }
        }

        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
