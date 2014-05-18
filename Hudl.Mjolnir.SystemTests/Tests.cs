﻿using System.IO;
using System.Linq;
using System.Net.Mime;
using Hudl.Common.Extensions;
using Hudl.Config;
using Hudl.Mjolnir.Command;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hudl.Riemann;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Hudl.Mjolnir.SystemTests
{
    public class Tests
    {
        private const ushort ServerPort = 22222;
        private const string MetricsFile = @"c:\hudl\logs\mjolnir-system-test-metrics.txt";
        private const string ReportFile = @"c:\hudl\logs\mjolnir-system-test-report.html";
        private static readonly ILog Log = LogManager.GetLogger(typeof (Tests));

        [Fact]
        public async Task TestMethod1()
        {
            File.Delete(MetricsFile);
            File.Delete(ReportFile);

            ConfigProvider.UseProvider(new SystemTestConfigProvider());
            InitializeLogging();
            CommandContext.Riemann = new Log4NetRiemann();

            using (var server = new HttpServer(1))
            {
                server.Start(ServerPort);
                server.ProcessRequest += ServerBehavior.Immediate200();

                for(var i = 0; i < 10; i++)
                {
                    var url = string.Format("http://localhost:{0}/", ServerPort);
                    var command = new HttpClientCommand(url, TimeSpan.FromSeconds(10));

                    var status = await command.InvokeAsync();

                    Thread.Sleep(1000);
                }

                server.Stop();
            }

            // Create report

            var metrics = File.ReadAllLines(MetricsFile).ToList().Select(line =>
            {
                var parts = line.Split(',');
                try
                {
                    return new Metric(float.Parse(parts[0]), parts[1], parts[2], parts[3].Length > 0 ? float.Parse(parts[3]) : (float?)null);
                }
                catch (Exception)
                {
                    Log.ErrorFormat("Couldn't parse line [{0}]", line);
                    throw;
                }
            }).ToList();

            var charts = new List<Chart>
            {
                //Chart.Create(metrics, "Circuit breaker state", "open/closed", "mjolnir breaker system-test IsAllowing", m => (m.Status == "Allowed" ? 1 : 0)),
                Chart.Create("Circuit breaker state", new List<object>
                {
                    new
                    {
                        name = "state (open/closed)",
                        data = metrics
                            .Where(m => m.Service == "mjolnir breaker system-test IsAllowing")
                            .Select(m => new
                            {
                                x = m.OffsetSeconds,
                                y = (m.Status == "Allowed" ? 1 : 0),
                                color = (m.Status == "Allowed" ? "#00FF00" : "#FF0000"),
                            })
                            .ToArray(),
                        color = "#CCCCCC",
                    }
                }),
                Chart.Create(metrics, "InvokeAsync() elapsed ms", "elapsed", "mjolnir command system-test.HttpClient InvokeAsync"),
                Chart.Create("Thread pool use", new List<object>
                {
                    new
                    {
                        name = "active",
                        data = metrics
                            .Where(m => m.Service == "mjolnir pool system-test activeThreads")
                            .Select(m => new object[] { m.OffsetSeconds, m.Value })
                            .ToArray(),
                    },
                    new
                    {
                        name = "in use",
                        data = metrics
                            .Where(m => m.Service == "mjolnir pool system-test inUseThreads")
                            .Select(m => new object[] { m.OffsetSeconds, m.Value })
                            .ToArray(),
                    }
                }),
                Chart.Create(metrics, "Breaker total observed operations", "total", "mjolnir breaker system-test total"),
                Chart.Create(metrics, "Breaker observed error percent", "error %", "mjolnir breaker system-test error"),
            };

            var output = @"<!doctype html>
<html>
  <head>
    <script src='http://ajax.googleapis.com/ajax/libs/jquery/1.11.1/jquery.min.js'></script>
    <script src='http://cdnjs.cloudflare.com/ajax/libs/highcharts/4.0.1/highcharts.js'></script>
    <style type='text/css'>
html, body { margin: 0; padding: 0; }
    </style>
  </head>
  <body>
";
            var count = 0;
            foreach (var chart in charts)
            {
                var id = "chart" + count;
                output += string.Format(@"<div>
<div id='{0}' style='width: 600px; height: 300px;'></div>
<script type='text/javascript'>$('#{1}').highcharts({2});</script>
</div>", id, id, JObject.FromObject(chart.HighchartsOptions));

                count++;
            }

            output += @" </body>
</html>
";

            File.WriteAllText(ReportFile, output);
        }

        private static void InitializeLogging()
        {
            var appender = new FileAppender
            {
                Threshold = Level.Debug,
                AppendToFile = true,
                File = @"c:\hudl\logs\mjolnir-system-test-log.txt",
                Layout = new PatternLayout("%utcdate %property{log4net:HostName} [%-5level] [%logger] %message%newline"),
                LockingModel = new FileAppender.MinimalLock(),
            };
            appender.ActivateOptions();

            var metrics = new FileAppender
            {
                Name = "metrics",
                Threshold = Level.Debug,
                AppendToFile = true,
                File = MetricsFile,
                Layout = new PatternLayout("%message%newline"), // Date should be in the message as a UTC unix timestamp in millis.
                LockingModel = new FileAppender.MinimalLock(),
            };
            metrics.ActivateOptions();

            BasicConfigurator.Configure(appender);
            ((Logger)LogManager.GetLogger("metrics").Logger).AddAppender(metrics);
        }
    }

    internal class Chart
    {
        public object HighchartsOptions { get; set; }

        public static Chart Create(List<Metric> metrics, string title, string seriesLabel, string service)
        {
            return Create(metrics, title, seriesLabel, service, m => m.Value);
        }

        public static Chart Create(List<Metric> metrics, string title, string seriesLabel, string service, Func<Metric, float?> valueSelector)
        {
            var series = new List<object>
            {
                new
                {
                    data = metrics
                        .Where(m => m.Service == service)
                        .Select(m => new object[] { m.OffsetSeconds, valueSelector(m) })
                        .ToArray(),
                    name = seriesLabel,
                },
            };
            return Create(title, series);
        }

        public static Chart Create(string title, List<object> series)
        {
            return new Chart
            {
                HighchartsOptions = new
                {
                    chart = new
                    {
                        marginLeft = 50,
                    },
                    title = new
                    {
                        text = title,
                        align = "left",
                        style = "color: #333333; fontSize: 12px;"
                    },
                    legend = new
                    {
                        enabled = true,
                        verticalAlign = "top",
                        align = "right",
                        floating = true,
                    },
                    yAxis = new
                    {
                        title = (string)null,
                        //categories = new [] {"open", "closed"},
                    },
                    series = series,
                }
            };
        }
    }

    internal class HttpClientCommand : Command<HttpStatusCode>
    {
        private readonly string _url;

        public HttpClientCommand(string url, TimeSpan timeout) : base("system-test", "system-test", timeout)
        {
            _url = url;
        }

        protected override async Task<HttpStatusCode> ExecuteAsync(CancellationToken cancellationToken)
        {
            var client = new HttpClient();
            var response = await client.GetAsync(_url, cancellationToken);
            var status = response.StatusCode;

            Debug.WriteLine("Status {0}", status);
            return status;
        }
    }

    internal static class ServerBehavior
    {
        public static Action<HttpListenerContext> Immediate200()
        {
            return context =>
            {
                context.Response.StatusCode = (int) HttpStatusCode.OK;
                context.Response.Close();
            };
        }

        public static Action<HttpListenerContext> Delayed200(TimeSpan sleep)
        {
            return context =>
            {
                Thread.Sleep(sleep);
                context.Response.StatusCode = (int) HttpStatusCode.OK;
                context.Response.Close();
            };
        }

        public static Action<HttpListenerContext> Percentage500(int percent)
        {
            return context =>
            {
                var success = (new Random().Next(0, 100)) < percent;
                context.Response.StatusCode = (int) (success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
                context.Response.Close();
            };
        }
    }

    // From http://stackoverflow.com/a/4673210/29995
    internal class HttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly Thread[] _workers;
        private readonly ManualResetEvent _stop, _ready;
        private Queue<HttpListenerContext> _queue;

        public HttpServer(int maxThreads)
        {
            _workers = new Thread[maxThreads];
            _queue = new Queue<HttpListenerContext>();
            _stop = new ManualResetEvent(false);
            _ready = new ManualResetEvent(false);
            _listener = new HttpListener();
            _listenerThread = new Thread(HandleRequests);
        }

        public void Start(int port)
        {
            _listener.Prefixes.Add(String.Format(@"http://+:{0}/", port));
            _listener.Start();
            _listenerThread.Start();

            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(Worker);
                _workers[i].Start();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public void Stop()
        {
            _stop.Set();
            _listenerThread.Join();
            foreach (Thread worker in _workers)
                worker.Join();
            _listener.Stop();
        }

        private void HandleRequests()
        {
            while (_listener.IsListening)
            {
                var context = _listener.BeginGetContext(ContextReady, null);

                if (0 == WaitHandle.WaitAny(new[] { _stop, context.AsyncWaitHandle }))
                    return;
            }
        }

        private void ContextReady(IAsyncResult ar)
        {
            try
            {
                lock (_queue)
                {
                    _queue.Enqueue(_listener.EndGetContext(ar));
                    _ready.Set();
                }
            }
            catch
            {
                return;
            }
        }

        private void Worker()
        {
            WaitHandle[] wait = new[] { _ready, _stop };
            while (0 == WaitHandle.WaitAny(wait))
            {
                HttpListenerContext context;
                lock (_queue)
                {
                    if (_queue.Count > 0)
                        context = _queue.Dequeue();
                    else
                    {
                        _ready.Reset();
                        continue;
                    }
                }

                try
                {
                    ProcessRequest(context);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                }
            }
        }

        public event Action<HttpListenerContext> ProcessRequest;
    }

    internal class SystemTestConfigProvider : IConfigurationProvider
    {
        private static readonly Dictionary<string, object> Values = new Dictionary<string, object>
        {
            { "mjolnir.useCircuitBreakers", true },
            //{ "stats.riemann.isEnabled", false },
            //{ "command.system-test.HttpClient.Timeout", 15000 },
            { "mjolnir.gaugeIntervalMillis", 1000 },
            
            //{ "mjolnir.pools.system-test.threadCount", 10 },
            //{ "mjolnir.pools.system-test.queueLength", 10 },
            /*{ "", false },
            { "", false },
            { "", false },
            { "", false },
            { "", false },
            { "", false },
            { "", false },*/
        }; 

        public T Get<T>(string configKey)
        {
            return ConvertValue<T>(Values.ContainsKey(configKey) ? Values[configKey] : null);
        }

        public object Get(string configKey)
        {
            return Values.ContainsKey(configKey) ? Values[configKey] : null;
        }

        public void Set(string configKey, object value)
        {
            throw new NotImplementedException();
        }

        public void Delete(string configKey)
        {
            throw new NotImplementedException();
        }

        public string[] GetKeys(string prefix)
        {
            return Values.Keys.ToArray();
        }

        public T ConvertValue<T>(object value)
        {
            return DefaultValueConverter.ConvertValue<T>(value);
        }

        public event ConfigurationChangedHandler ConfigurationChanged;
    }

    internal class Log4NetRiemann : IRiemann
    {
        private static readonly ILog Log = LogManager.GetLogger("metrics");
        //private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly DateTime _startTime = DateTime.UtcNow;

        //private static long UnixTimestamp()
        //{
        //    return (long) (DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
        //}
        private double OffsetMillis()
        {
            return (DateTime.UtcNow - _startTime).TotalMilliseconds / 1000;
        }

        private void WriteLog(string service, string state, object metric)
        {
            Log.InfoFormat("{0},{1},{2},{3}", OffsetMillis(), service, state, metric);
        }

        public void Event(string service, string state, long? metric = null, ISet<string> tags = null, string description = null, int? ttl = null)
        {
            WriteLog(service, state, metric);
        }

        public void Event(string service, string state, float? metric = null, ISet<string> tags = null, string description = null, int? ttl = null)
        {
            WriteLog(service, state, metric);
        }

        public void Event(string service, string state, double? metric = null, ISet<string> tags = null, string description = null, int? ttl = null)
        {
            WriteLog(service, state, metric);
        }

        public void Elapsed(string service, string state, TimeSpan elapsed, ISet<string> tags = null, string description = null, int? ttl = null)
        {
            WriteLog(service, state, elapsed.TotalMilliseconds);
        }

        public void Gauge(string service, string state, long? metric = null, ISet<string> tags = null, string description = null, int? ttl = null)
        {
            WriteLog(service, state, metric);
        }

        public void ConfigGauge(string service, long metric)
        {
            WriteLog(service, null, metric);
        }
    }

    internal class Metric
    {
        public float OffsetSeconds { get; private set; }
        public string Service { get; private set; }
        public string Status { get; private set; }
        public float? Value { get; private set; }

        public Metric(float offsetSeconds, string service, string status, float? value)
        {
            OffsetSeconds = offsetSeconds;
            Service = service;
            Status = status;
            Value = value;
        }
    }
}
