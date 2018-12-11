﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.Sdk
{
    public interface IAgentLogPlugin
    {
        string FriendlyName { get; }

        Task<bool> InitializeAsync(IAgentLogPluginContext context);

        Task ProcessLineAsync(IAgentLogPluginContext context, Pipelines.TaskStepDefinitionReference step, string line);

        Task FinalizeAsync(IAgentLogPluginContext context);
    }

    public interface IAgentLogPluginTrace
    {
        // agent log
        void Trace(string message);

        // user log (job log)
        void Output(string message);
    }

    public interface IAgentLogPluginContext
    {
        // default SystemConnection back to service use the job oauth token
        VssConnection VssConnection { get; }

        // task info for all steps
        IList<Pipelines.TaskStepDefinitionReference> Steps { get; }

        // all endpoints
        IList<ServiceEndpoint> Endpoints { get; }

        // all repositories
        IList<Pipelines.RepositoryResource> Repositories { get; }

        // all variables
        IDictionary<string, VariableValue> Variables { get; }

        // agent log
        void Trace(string message);

        // user log (job log)
        void Output(string message);
    }

    public class AgentLogPluginTrace : IAgentLogPluginTrace
    {
        // agent log
        public void Trace(string message)
        {
            Console.WriteLine($"##[plugin.trace]{message}");
        }

        // user log (job log)
        public void Output(string message)
        {
            Console.WriteLine(message);
        }
    }

    public class AgentLogPluginContext : IAgentLogPluginContext
    {
        private string _pluginName;
        private IAgentLogPluginTrace _trace;


        // default SystemConnection back to service use the job oauth token
        public VssConnection VssConnection { get; }

        // task info for all steps
        public IList<Pipelines.TaskStepDefinitionReference> Steps { get; }

        // all endpoints
        public IList<ServiceEndpoint> Endpoints { get; }

        // all repositories
        public IList<Pipelines.RepositoryResource> Repositories { get; }

        // all variables
        public IDictionary<string, VariableValue> Variables { get; }

        public AgentLogPluginContext(
            string pluginNme,
            VssConnection connection,
            IList<Pipelines.TaskStepDefinitionReference> steps,
            IList<ServiceEndpoint> endpoints,
            IList<Pipelines.RepositoryResource> repositories,
            IDictionary<string, VariableValue> variables,
            IAgentLogPluginTrace trace)
        {
            _pluginName = pluginNme;
            VssConnection = connection;
            Steps = steps;
            Endpoints = endpoints;
            Repositories = repositories;
            Variables = variables;
            _trace = trace;
        }

        // agent log
        public void Trace(string message)
        {
            _trace.Trace($"{_pluginName}: {message}");
        }

        // user log (job log)
        public void Output(string message)
        {
            _trace.Output($"{_pluginName}: {message}");
        }
    }

    public class AgentLogPluginHostContext
    {
        private VssConnection _connection;

        public List<String> PluginAssemblies { get; set; }
        public List<ServiceEndpoint> Endpoints { get; set; }
        public List<Pipelines.RepositoryResource> Repositories { get; set; }
        public Dictionary<string, VariableValue> Variables { get; set; }
        public Dictionary<string, Pipelines.TaskStepDefinitionReference> Steps { get; set; }

        [JsonIgnore]
        public VssConnection VssConnection
        {
            get
            {
                if (_connection == null)
                {
                    _connection = InitializeVssConnection();
                }
                return _connection;
            }
        }

        private VssConnection InitializeVssConnection()
        {
            var headerValues = new List<ProductInfoHeaderValue>();
            headerValues.Add(new ProductInfoHeaderValue($"VstsAgentCore-Plugin", Variables.GetValueOrDefault("agent.version")?.Value ?? "Unknown"));
            headerValues.Add(new ProductInfoHeaderValue($"({RuntimeInformation.OSDescription.Trim()})"));

            if (VssClientHttpRequestSettings.Default.UserAgent != null && VssClientHttpRequestSettings.Default.UserAgent.Count > 0)
            {
                headerValues.AddRange(VssClientHttpRequestSettings.Default.UserAgent);
            }

            VssClientHttpRequestSettings.Default.UserAgent = headerValues;

            var certSetting = GetCertConfiguration();
            if (certSetting != null)
            {
                if (!string.IsNullOrEmpty(certSetting.ClientCertificateArchiveFile))
                {
                    VssClientHttpRequestSettings.Default.ClientCertificateManager = new AgentClientCertificateManager(certSetting.ClientCertificateArchiveFile, certSetting.ClientCertificatePassword);
                }

                if (certSetting.SkipServerCertificateValidation)
                {
                    VssClientHttpRequestSettings.Default.ServerCertificateValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
            }

            var proxySetting = GetProxyConfiguration();
            if (proxySetting != null)
            {
                if (!string.IsNullOrEmpty(proxySetting.ProxyAddress))
                {
                    VssHttpMessageHandler.DefaultWebProxy = new AgentWebProxy(proxySetting.ProxyAddress, proxySetting.ProxyUsername, proxySetting.ProxyPassword, proxySetting.ProxyBypassList);
                }
            }

            ServiceEndpoint systemConnection = this.Endpoints.FirstOrDefault(e => string.Equals(e.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            ArgUtil.NotNull(systemConnection, nameof(systemConnection));
            ArgUtil.NotNull(systemConnection.Url, nameof(systemConnection.Url));

            VssCredentials credentials = VssUtil.GetVssCredential(systemConnection);
            ArgUtil.NotNull(credentials, nameof(credentials));
            return VssUtil.CreateConnection(systemConnection.Url, credentials);
        }

        private AgentCertificateSettings GetCertConfiguration()
        {
            bool skipCertValidation = StringUtil.ConvertToBoolean(this.Variables.GetValueOrDefault("Agent.SkipCertValidation")?.Value);
            string caFile = this.Variables.GetValueOrDefault("Agent.CAInfo")?.Value;
            string clientCertFile = this.Variables.GetValueOrDefault("Agent.ClientCert")?.Value;

            if (!string.IsNullOrEmpty(caFile) || !string.IsNullOrEmpty(clientCertFile) || skipCertValidation)
            {
                var certConfig = new AgentCertificateSettings();
                certConfig.SkipServerCertificateValidation = skipCertValidation;
                certConfig.CACertificateFile = caFile;

                if (!string.IsNullOrEmpty(clientCertFile))
                {
                    certConfig.ClientCertificateFile = clientCertFile;
                    string clientCertKey = this.Variables.GetValueOrDefault("Agent.ClientCertKey")?.Value;
                    string clientCertArchive = this.Variables.GetValueOrDefault("Agent.ClientCertArchive")?.Value;
                    string clientCertPassword = this.Variables.GetValueOrDefault("Agent.ClientCertPassword")?.Value;

                    certConfig.ClientCertificatePrivateKeyFile = clientCertKey;
                    certConfig.ClientCertificateArchiveFile = clientCertArchive;
                    certConfig.ClientCertificatePassword = clientCertPassword;

                    certConfig.VssClientCertificateManager = new AgentClientCertificateManager(clientCertArchive, clientCertPassword);
                }

                return certConfig;
            }
            else
            {
                return null;
            }
        }

        private AgentWebProxySettings GetProxyConfiguration()
        {
            string proxyUrl = this.Variables.GetValueOrDefault("Agent.ProxyUrl")?.Value;
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                string proxyUsername = this.Variables.GetValueOrDefault("Agent.ProxyUsername")?.Value;
                string proxyPassword = this.Variables.GetValueOrDefault("Agent.ProxyPassword")?.Value;
                List<string> proxyBypassHosts = StringUtil.ConvertFromJson<List<string>>(this.Variables.GetValueOrDefault("Agent.ProxyBypassList")?.Value ?? "[]");
                return new AgentWebProxySettings()
                {
                    ProxyAddress = proxyUrl,
                    ProxyUsername = proxyUsername,
                    ProxyPassword = proxyPassword,
                    ProxyBypassList = proxyBypassHosts,
                    WebProxy = new AgentWebProxy(proxyUrl, proxyUsername, proxyPassword, proxyBypassHosts)
                };
            }
            else
            {
                return null;
            }
        }
    }

    public class AgentLogPluginHost
    {
        private readonly TaskCompletionSource<int> _jobFinished = new TaskCompletionSource<int>();
        private readonly Dictionary<string, ConcurrentQueue<string>> _outputQueue = new Dictionary<string, ConcurrentQueue<string>>();
        private readonly Dictionary<string, IAgentLogPluginContext> _pluginContexts = new Dictionary<string, IAgentLogPluginContext>();
        private readonly Dictionary<string, TaskCompletionSource<int>> _shortCircuited = new Dictionary<string, TaskCompletionSource<int>>();
        private Dictionary<string, Pipelines.TaskStepDefinitionReference> _steps;
        private List<IAgentLogPlugin> _plugins;
        private IAgentLogPluginTrace _trace;
        private int _shortCircuitThreshold;
        private int _shortCircuitMonitorFrequency;

        public AgentLogPluginHost(
            AgentLogPluginHostContext hostContext,
            List<IAgentLogPlugin> plugins,
            IAgentLogPluginTrace trace = null,
            int shortCircuitThreshold = 1000, // output queue depth >= 1000 lines
            int shortCircuitMonitorFrequency = 10000) // check all output queues every 10 sec
        {
            _steps = hostContext.Steps;
            _plugins = plugins;
            _trace = trace ?? new AgentLogPluginTrace();
            _shortCircuitThreshold = shortCircuitThreshold;
            _shortCircuitMonitorFrequency = shortCircuitMonitorFrequency;

            foreach (var plugin in _plugins)
            {
                string typeName = plugin.GetType().FullName;
                _outputQueue[typeName] = new ConcurrentQueue<string>();
                _pluginContexts[typeName] = new AgentLogPluginContext(plugin.FriendlyName, hostContext.VssConnection, hostContext.Steps.Values.ToList(), hostContext.Endpoints, hostContext.Repositories, hostContext.Variables, _trace);
                _shortCircuited[typeName] = new TaskCompletionSource<int>();
            }
        }

        public async Task Run()
        {
            using (CancellationTokenSource tokenSource = new CancellationTokenSource())
            using (CancellationTokenSource monitorSource = new CancellationTokenSource())
            {
                Task memoryUsageMonitor = StartMemoryUsageMonitor(monitorSource.Token);

                Dictionary<string, Task> processTasks = new Dictionary<string, Task>();
                foreach (var plugin in _plugins)
                {
                    // start process plugins background
                    _trace.Trace($"Start process task for plugin '{plugin.FriendlyName}'");
                    var task = RunAsync(plugin, tokenSource.Token);
                    processTasks[plugin.FriendlyName] = task;
                }

                // waiting for job finish event
                await _jobFinished.Task;
                tokenSource.Cancel();

                _trace.Trace($"Wait for all plugins finish process outputs.");
                await Task.WhenAll(processTasks.Values);

                foreach (var task in processTasks)
                {
                    try
                    {
                        await task.Value;
                        _trace.Trace($"Plugin '{task.Key}' finished log process.");
                    }
                    catch (Exception ex)
                    {
                        _trace.Output($"Plugin '{task.Key}' failed with: {ex}");
                    }
                }

                // Stop monitor
                monitorSource.Cancel();
                await memoryUsageMonitor;

                // job has finished, all log plugins should start their finalize process
                Dictionary<string, Task> finalizeTasks = new Dictionary<string, Task>();
                foreach (var plugin in _plugins)
                {
                    string typeName = plugin.GetType().FullName;
                    if (!_shortCircuited[typeName].Task.IsCompleted)
                    {
                        _trace.Trace($"Start finalize for plugin '{plugin.FriendlyName}'");
                        var finalize = plugin.FinalizeAsync(_pluginContexts[typeName]);
                        finalizeTasks[plugin.FriendlyName] = finalize;
                    }
                    else
                    {
                        _trace.Trace($"Skip finalize for short circuited plugin '{plugin.FriendlyName}'");
                    }
                }

                _trace.Trace($"Wait for all plugins finish finalization.");
                await Task.WhenAll(finalizeTasks.Values);

                foreach (var task in finalizeTasks)
                {
                    try
                    {
                        await task.Value;
                        _trace.Trace($"Plugin '{task.Key}' finished job finalize.");
                    }
                    catch (Exception ex)
                    {
                        _trace.Output($"Plugin '{task.Key}' failed with: {ex}");
                    }
                }
            }
        }

        public void EnqueueOutput(string output)
        {
            if (!string.IsNullOrEmpty(output))
            {
                foreach (var plugin in _plugins)
                {
                    string typeName = plugin.GetType().FullName;
                    if (!_shortCircuited[typeName].Task.IsCompleted)
                    {
                        _outputQueue[typeName].Enqueue(output);
                    }
                }
            }
        }

        public void Finish()
        {
            _jobFinished.TrySetResult(0);
        }

        private async Task StartMemoryUsageMonitor(CancellationToken token)
        {
            Dictionary<string, Int32> flag = new Dictionary<string, int>();
            foreach (var queue in _outputQueue)
            {
                flag[queue.Key] = 0;
            }

            _trace.Trace($"Start output buffer monitor.");
            while (!token.IsCancellationRequested)
            {
                foreach (var queue in _outputQueue)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (queue.Value.Count > _shortCircuitThreshold)
                    {
                        _trace.Trace($"Plugin '{queue.Key}' has too many buffered outputs.");
                        flag[queue.Key]++;
                        if (flag[queue.Key] >= 10)
                        {
                            _trace.Trace($"Short circuit plugin '{queue.Key}'.");
                            _shortCircuited[queue.Key].TrySetResult(0);
                        }
                    }
                    else
                    {
                        _trace.Trace($"Plugin '{queue.Key}' has cleared out buffered outputs.");
                        flag[queue.Key] = 0;
                    }
                }

                await Task.WhenAny(Task.Delay(_shortCircuitMonitorFrequency), Task.Delay(-1, token));
            }
        }

        private async Task RunAsync(IAgentLogPlugin plugin, CancellationToken token)
        {
            List<string> errors = new List<string>();
            string typeName = plugin.GetType().FullName;
            var context = _pluginContexts[typeName];

            bool initialized = false;
            try
            {
                initialized = await plugin.InitializeAsync(context);
            }
            catch (Exception ex)
            {
                errors.Add($"Fail to initialize: {ex.Message}.");
                context.Trace(ex.ToString());
            }
            finally
            {
                if (!initialized)
                {
                    context.Output("Skip process outputs base on plugin initialize result.");
                    _shortCircuited[typeName].TrySetResult(0);
                }
            }

            using (var registration = token.Register(() =>
                                      {
                                          var depth = _outputQueue[typeName].Count;
                                          if (depth > 0)
                                          {
                                              context.Output($"Pending process {depth} log lines.");
                                          }
                                      }))
            {
                while (!_shortCircuited[typeName].Task.IsCompleted && !token.IsCancellationRequested)
                {
                    while (!_shortCircuited[typeName].Task.IsCompleted && _outputQueue[typeName].TryDequeue(out string line))
                    {
                        try
                        {
                            var id = line.Substring(0, line.IndexOf(":"));
                            var message = line.Substring(line.IndexOf(":") + 1);
                            var processLineTask = plugin.ProcessLineAsync(context, _steps[id], message);
                            var completedTask = await Task.WhenAny(_shortCircuited[typeName].Task, processLineTask);
                            if (completedTask == processLineTask)
                            {
                                await processLineTask;
                            }
                        }
                        catch (Exception ex)
                        {
                            // ignore exception
                            // only trace the first 10 errors.
                            if (errors.Count < 10)
                            {
                                errors.Add(ex.ToString());
                            }
                        }
                    }

                    // back-off before pull output queue again.
                    await Task.Delay(500);
                }
            }

            // process all remaining outputs
            context.Trace("Process remaining outputs after job finished.");
            while (!_shortCircuited[typeName].Task.IsCompleted && _outputQueue[typeName].TryDequeue(out string line))
            {
                try
                {
                    var id = line.Substring(0, line.IndexOf(":"));
                    var message = line.Substring(line.IndexOf(":") + 1);
                    var processLineTask = plugin.ProcessLineAsync(context, _steps[id], message);
                    var completedTask = await Task.WhenAny(_shortCircuited[typeName].Task, processLineTask);
                    if (completedTask == processLineTask)
                    {
                        await processLineTask;
                    }
                }
                catch (Exception ex)
                {
                    // ignore exception
                    // only trace the first 10 errors.
                    if (errors.Count < 10)
                    {
                        errors.Add(ex.ToString());
                    }
                }
            }

            // print out the plugin has been short circuited.
            if (_shortCircuited[typeName].Task.IsCompleted)
            {
                if (initialized)
                {
                    context.Output($"Plugin has been short circuited due to exceed memory usage limit.");
                }

                _outputQueue[typeName].Clear();
            }

            // print out error to user.
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    context.Output($"Fail to process output: {error}");
                }
            }
        }
    }
}