using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Serialization;
using System.Threading;

namespace int512.qraa
{
    static class Program
    {
        private static readonly string ProgramName = "qraa";
        private static readonly string ServerMutexName = "qraa-server-mutex";
        private static readonly string PipeName = "qraa";
        private static readonly int PipeTimeout = 20 * 1000; // 20secs
        
        private static readonly Dictionary<string, Func<Result>> Commands = new Dictionary<string, Func<Result>>
        {
            {"net-session", () => Run("net", "session")}
        };

        [STAThread]
        static void Main()
        {
            var opts = CommandlineOptions.Parse(Environment.GetCommandLineArgs());
            if (opts.Shutdown)
            {
                if (IsServerStarted())
                {
                    SendCommand("shutdown");
                }
                return;
            }
            if (opts.Server)
            {
                StartServer();
                return;
            }

            if (opts.Command == null)
            {
                ShowMessage("No command is specified", ProgramName, MessageBoxIcon.Error);
                return;
            }
            if (!Commands.ContainsKey(opts.Command))
            {
                ShowMessage("No command named '" + opts.Command + "'", ProgramName, MessageBoxIcon.Error);
                return;
            }

            // Start if server isn't up
            if (!IsServerStarted())
            {
                StartServerAsAdmin();
            }

            var result = SendCommand(opts.Command);
            if (result == null)
            {
                ShowMessage("Fatal error", "Error - " + ProgramName, MessageBoxIcon.Error);
                return;
            }

            var resultText = PrettyResult(result);
            if (result.IsExitSuccessfully())
            {
                ShowMessage("Exit successfully\n" + resultText, ProgramName, MessageBoxIcon.Information);
            }
            else
            {
                ShowMessage(resultText, "Error - " + ProgramName, MessageBoxIcon.Error);
            }
        }

        private static string PrettyResult(Result result)
        {
            var messages = new List<string>();
            messages.Add("code:" + result.ExitCode);
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                messages.Add("stdout:\n" + result.Stdout);
            }
            if (!string.IsNullOrWhiteSpace(result.Stderr))
            {
                messages.Add("stderr:\n" + result.Stderr);
            }
            return string.Join("\n--------------------\n", messages);
        }

        private static Result SendCommand(string cmd)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
            {
                pipe.Connect(PipeTimeout);
                
                using (var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                {
                    writer.WriteLine(cmd);
                }
                pipe.WaitForPipeDrain();
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    var resultJson = reader.ReadToEnd().Trim();
                    return resultJson.Length == 0 ? null : Result.FromJson(resultJson);
                }
            }
        }

        private static bool IsServerStarted()
        {
            Mutex mutex;
            var ret = Mutex.TryOpenExisting(ServerMutexName, out mutex);
            if (mutex != null)
            {
                mutex.Dispose();
            }
            return ret;
        }

        private static void StartServerAsAdmin()
        {
            var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "/server")
            {
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "RunAs",
            };
            Process.Start(psi);
        }

        private static void StartServer()
        {
            using (var mutex = new Mutex(false, ServerMutexName))
            {
                var mutexAcquired = mutex.WaitOne(0, false);
                if (!mutexAcquired)
                {
                    ShowMessage("Server is already running", ProgramName, MessageBoxIcon.Error);
                    return;
                }

                while (ServerLoop()) ;
                mutex.ReleaseMutex();
            }
        }

        private static bool ServerLoop()
        {
            var ps = new PipeSecurity();
            var authorizedUser = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            ps.SetAccessRule(new PipeAccessRule(authorizedUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.WriteThrough, 1024 * 100, 1024 * 100, ps))
            {
                pipe.WaitForConnection();
                string cmd;
                using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                {
                    cmd = reader.ReadLine();
                }
                if (cmd == "shutdown") return false;
                if (!Commands.ContainsKey(cmd)) return true;

                var result = Commands[cmd]();
                using (var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                {
                    writer.WriteLine(result == null ? null : result.ToJsonString());
                }
                pipe.WaitForPipeDrain();
            }
            return true;
        }

        private static Result Run(string filename, string args = "", string stdin = null)
        {
            var psi = new ProcessStartInfo(filename, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
            };

            var proc = new Process()
            {
                StartInfo = psi
            };
            proc.Start();
            if (stdin != null)
            {
                proc.StandardInput.Write(stdin);
            }
            proc.WaitForExit();

            return new Result()
            {
                ExitCode = proc.ExitCode,
                Stdout = proc.StandardOutput.ReadToEnd(),
                Stderr = proc.StandardError.ReadToEnd(),
            };
        }

        private static void ShowMessage(string text, string caption, MessageBoxIcon icon)
        {
            MessageBox.Show(text, caption, MessageBoxButtons.OK, icon);
        }
    }

    /// <summary>
    /// Commandline option
    /// </summary>
    public class CommandlineOptions
    {
        private static readonly string DefaultName = "qraa";

        /// <summary>
        /// The command to run
        /// </summary>
        public string Command { set; get; }

        /// <summary>
        /// Is /server passed
        /// </summary>
        public bool Server { set; get; }

        /// <summary>
        /// Is /shutdown passed
        /// </summary>
        public bool Shutdown { set; get; }

        /// <summary>
        /// Commandline option parser
        /// </summary>
        /// <param name="args">Arguments</param>
        /// <returns>An instance of CommandlineOptions</returns>
        public static CommandlineOptions Parse(string[] args)
        {
            var exename = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location);
            string command;
            if (exename != DefaultName)
            {
                command = exename;
            }
            else
            {
                command = args.Skip(1).FirstOrDefault(x => x.IndexOf("/") != 0);
            }

            return new CommandlineOptions()
            {
                Command = command,
                Server = args.Skip(1).Any(x => x.ToLower() == "/server"),
                Shutdown = args.Skip(1).Any(x => x.ToLower() == "/shutdown")
            };
        }
    }
    
    [DataContract]
    public class Result
    {
        [DataMember]
        public int ExitCode { set; get; }
        
        [DataMember]
        public string Stdout { get; set; }
        
        [DataMember]
        public string Stderr { get; set; }
        
        public bool IsExitSuccessfully()
        {
            return ExitCode == 0;
        }

        public static Result FromJson(string json)
        {
            var strbytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(strbytes))
            {
                var serializer = new DataContractJsonSerializer(typeof(Result));
                return (Result)serializer.ReadObject(stream);
            }
        }
        
        public string ToJsonString()
        {
            using (var stream = new MemoryStream())
            using (var reader = new StreamReader(stream))
            {
                var serializer = new DataContractJsonSerializer(typeof(Result));
                serializer.WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
