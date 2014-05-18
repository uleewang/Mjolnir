﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Hudl.Common.Extensions;
using Hudl.Config;
using Hudl.Mjolnir.Breaker;
using Hudl.Mjolnir.Key;
using Hudl.Mjolnir.ThreadPool;
using Hudl.Riemann;
using Hudl.Stats;
using log4net;

namespace Hudl.Mjolnir.Command
{
    public interface ICommand<TResult>
    {
        TResult Invoke();
        Task<TResult> InvokeAsync();
    }

    public abstract class Command
    {
        protected static readonly ILog Log = LogManager.GetLogger(typeof(Command<>));
        protected static readonly ConfigurableValue<bool> UseCircuitBreakers = new ConfigurableValue<bool>("mjolnir.useCircuitBreakers");

        /// <summary>
        /// Cache of known command names, keyed by Type and group key. Helps
        /// avoid repeatedly generating the same Name for every distinct command
        /// instance.
        /// </summary>
        protected static readonly ConcurrentDictionary<Tuple<Type, GroupKey>, string> GeneratedNameCache = new ConcurrentDictionary<Tuple<Type, GroupKey>, string>();

        /// <summary>
        /// Cache of known command names, keyed by provided name and group key. Helps
        /// avoid repeatedly generating the same Name for every distinct command.
        /// </summary>
        protected static readonly ConcurrentDictionary<Tuple<string, GroupKey>, string> ProvidedNameCache = new ConcurrentDictionary<Tuple<string, GroupKey>, string>();
    }

    /// <summary>
    /// Protection layer for operations that might fail.
    /// 
    /// Provides isolation and fail-fast behavior around dangerous operations using timeouts,
    /// circuit breakers, and thread pools.
    /// </summary>
    /// <typeparam name="TResult">The type of the result returned by this command's execution.</typeparam>
    public abstract class Command<TResult> : Command, ICommand<TResult>
    {
        internal readonly TimeSpan Timeout;

        private readonly GroupKey _group;
        private readonly string _name;
        private readonly GroupKey _breakerKey;
        private readonly GroupKey _poolKey;

        // Setters should be used for testing only.

        private IRiemann _riemann;
        internal IRiemann Riemann
        {
            private get { return _riemann ?? CommandContext.Riemann; }
            set { _riemann = value; }
        }

        private ICircuitBreaker _breaker;
        internal ICircuitBreaker CircuitBreaker
        {
            private get { return _breaker ?? CommandContext.GetCircuitBreaker(_breakerKey); }
            set { _breaker = value; }
        }

        private IIsolationThreadPool _pool;
        internal IIsolationThreadPool ThreadPool
        {
            private get { return _pool ?? CommandContext.GetThreadPool(_poolKey); }
            set { _pool = value; }
        }

        private IIsolationSemaphore _fallbackSemaphore;
        internal IIsolationSemaphore FallbackSemaphore
        {
            // TODO Consider isolating these per-command instead of per-pool.
            private get { return _fallbackSemaphore ?? CommandContext.GetFallbackSemaphore(_poolKey); }
            set { _fallbackSemaphore = value; }
        }

        // 0 == not yet invoked, > 0 == invoked
        private int _hasInvoked = 0;

        /// <summary>
        /// Constructs the Command.
        /// 
        /// The group is used as part of the Command's <see cref="Name">Name</see>.
        /// If the group contains dots, they'll be converted to dashes.
        /// 
        /// The provided <code>isolationKey</code> will be used as both the
        /// breaker and pool keys.
        /// 
        /// Command timeouts can be configured at runtime. Configuration keys
        /// follow the form: <code>mjolnir.group-key.CommandClassName.Timeout</code>
        /// (i.e. <code>mjolnir.[Command.Name].Timeout</code>). If not
        /// configured, the provided <code>defaultTimeout</code> will be used.
        /// 
        /// </summary>
        /// <param name="group">Logical grouping for the command, usually the owning team. Avoid using dots.</param>
        /// <param name="isolationKey">Breaker and pool key to use.</param>
        /// <param name="defaultTimeout">Timeout to enforce if not otherwise configured.</param>
        protected Command(string group, string isolationKey, TimeSpan defaultTimeout)
            : this(group, null, isolationKey, isolationKey, defaultTimeout) {}

        /// <summary>
        /// Constructs the Command.
        /// 
        /// The group is used as part of the Command's <see cref="Name">Name</see>.
        /// If the group contains dots, they'll be converted to dashes.
        /// 
        /// Command timeouts can be configured at runtime. Configuration keys
        /// follow the form: <code>mjolnir.group-key.CommandClassName.Timeout</code>
        /// (i.e. <code>mjolnir.[Command.Name].Timeout</code>). If not
        /// configured, the provided <code>defaultTimeout</code> will be used.
        /// 
        /// </summary>
        /// <param name="group">Logical grouping for the command, usually the owning team. Avoid using dots.</param>
        /// <param name="breakerKey">Breaker to use for this command.</param>
        /// <param name="poolKey">Pool to use for this command.</param>
        /// <param name="defaultTimeout">Timeout to enforce if not otherwise configured.</param>
        protected Command(string group, string breakerKey, string poolKey, TimeSpan defaultTimeout)
            : this(group, null, breakerKey, poolKey, defaultTimeout) {}

        internal Command(string group, string name, string breakerKey, string poolKey, TimeSpan defaultTimeout)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                throw new ArgumentNullException("group");
            }

            if (string.IsNullOrWhiteSpace(breakerKey))
            {
                throw new ArgumentNullException("breakerKey");
            }

            if (string.IsNullOrWhiteSpace(poolKey))
            {
                throw new ArgumentNullException("poolKey");
            }

            if (defaultTimeout.TotalMilliseconds <= 0)
            {
                throw new ArgumentException("Positive default timeout is required", "defaultTimeout");
            }

            _group = GroupKey.Named(group);
            _name = string.IsNullOrWhiteSpace(name) ? GenerateAndCacheName(Group) : CacheProvidedName(Group, name);
            _breakerKey = GroupKey.Named(breakerKey);
            _poolKey = GroupKey.Named(poolKey);

            var timeout = new ConfigurableValue<long>("command." + Name + ".Timeout").Value;
            if (timeout <= 0)
            {
                timeout = (long)defaultTimeout.TotalMilliseconds;
            }

            Timeout = TimeSpan.FromMilliseconds(timeout);
        }

        private string CacheProvidedName(GroupKey group, string name)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var cacheKey = new Tuple<string, GroupKey>(name, group);
                return ProvidedNameCache.GetOrAdd(cacheKey, t => cacheKey.Item2.Name.Replace(".", "-") + "." + name.Replace(".", "-"));
            }
            finally
            {
                // Not using _riemann here because it may not be initialized in tests before we call Name in the constructor.
                CommandContext.Riemann.Elapsed("mjolnir command mjolnir-meta.CacheProvidedName", null, stopwatch.Elapsed);
            }
        }

        private string GenerateAndCacheName(GroupKey group)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var type = GetType();
                var cacheKey = new Tuple<Type, GroupKey>(type, group);
                return GeneratedNameCache.GetOrAdd(cacheKey, t =>
                {
                    var className = cacheKey.Item1.Name;
                    if (className.EndsWith("Command", StringComparison.InvariantCulture))
                    {
                        className = className.Substring(0, className.LastIndexOf("Command", StringComparison.InvariantCulture));
                    }

                    return cacheKey.Item2.Name.Replace(".", "-") + "." + className;
                });
            }
            finally
            {
                // Not using _riemann here because it may not be initialized in tests before we call Name in the constructor.
                CommandContext.Riemann.Elapsed("mjolnir command mjolnir-meta.GenerateAndCacheName", null, stopwatch.Elapsed);
            }
        }

        internal string Name
        {
            get { return _name; }
        }

        internal GroupKey Group
        {
            get { return _group; }
        }

        internal GroupKey BreakerKey
        {
            get { return _breakerKey; }
        }

        internal GroupKey PoolKey
        {
            get { return _poolKey; }
        }

        private string RiemannKeyPrefix
        {
            get { return "mjolnir command " + Name; }
        }

        /// <summary>
        /// Synchronous pass-through to <see cref="InvokeAsync()"/>.
        /// 
        /// Prefer <see cref="InvokeAsync()"/>, but use this when integrating commands into
        /// synchronous code that's difficult to port to async.
        /// </summary>
        /// <returns></returns>
        public TResult Invoke()
        {
            try
            {
                return InvokeAsync().Result;
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }

            throw new InvalidOperationException("Unexpectedly reached the end of Invoke() without returning or throwing");
        }

        /// <summary>
        /// Runs this command, returning the result or throwing an exception if the command failed
        /// or couldn't be completed.
        /// 
        /// Note that this uses <code>async/await</code>. ASP.NET MVC callers that require
        /// SynchronizationContext to be retained should make sure that httpRuntime.targetFramework
        /// is set to 4.5 in their web.config. If not, context (e.g. <code>HttpContext.Current</code>)
        /// may be null when executing code that occurs after <code>await</code>ing the Task returned
        /// by this method.
        /// </summary>
        public async Task<TResult> InvokeAsync()
        {
            if (Interlocked.CompareExchange(ref _hasInvoked, 1, 0) > 0)
            {
                throw new InvalidOperationException("A command instance may only be invoked once");
            }

            var invokeStopwatch = Stopwatch.StartNew();
            var executeStopwatch = Stopwatch.StartNew();
            var status = CommandCompletionStatus.RanToCompletion;
            try
            {
                Log.InfoFormat("InvokeAsync Command={0} Breaker={1} Pool={2} Timeout={3}", Name, BreakerKey, PoolKey, Timeout.TotalMilliseconds);

                var cancellationTokenSource = new CancellationTokenSource(Timeout);
                // Note: this actually awaits the *enqueueing* of the task, not the task execution itself.
                var result = await ExecuteInIsolation(cancellationTokenSource.Token).ConfigureAwait(false);
                executeStopwatch.Stop();
                return result;
            }
            catch (Exception e)
            {
                executeStopwatch.Stop();
                status = StatusFromException(e);

                var instigator = new CommandFailedException(e, status).WithData(new
                {
                    Command = Name,
                    Timeout = Timeout.TotalMilliseconds,
                    Status = status,
                    Breaker = BreakerKey,
                    Pool = PoolKey,
                });

                // We don't log the exception here - that's intentional.
                
                // If a fallback is not implemented, the exception will get re-thrown and (hopefully) caught
                // and logged by an upstream container. This is the majority of cases, so logging here
                // results in a lot of extra, unnecessary logs and stack traces.

                // If a fallback is implemented, the burden is on the implementation to log or rethrow the
                // exception. Otherwise it'll be eaten. This is documented on the Fallback() method.

                return TryFallback(instigator);
            }
            finally
            {
                invokeStopwatch.Stop();

                Riemann.Elapsed(RiemannKeyPrefix + " ExecuteInIsolation", status.ToString(), executeStopwatch.Elapsed);
                Riemann.Elapsed(RiemannKeyPrefix + " InvokeAsync", status.ToString(), invokeStopwatch.Elapsed);

                StatsDClient.ManualTime("mjolnir.command." + Name + ".execute." + status, invokeStopwatch.ElapsedMilliseconds);
                StatsDClient.ManualTime("mjolnir.command." + Name + ".invoke." + status, invokeStopwatch.ElapsedMilliseconds);
            }
        }

        private Task<TResult> ExecuteInIsolation(CancellationToken cancellationToken)
        {
            // Note: Thread pool rejections shouldn't count as failures to the breaker.
            // If a downstream dependency is slow, the pool will fill up, but the
            // breaker + timeouts will already be providing protection against that.
            // If the pool is filling up because of a surge of requests, the rejections
            // will just be a way of shedding load - the breaker and downstream
            // dependency may be just fine, and we want to keep them that way.

            // We'll neither mark these as success *nor* failure, since they really didn't
            // even execute as far as the breaker and downstream dependencies are
            // concerned.

            var workItem = ThreadPool.Enqueue(() =>
            {
                // Since we may have been on the thread pool queue for a bit, see if we
                // should have canceled by now.
                cancellationToken.ThrowIfCancellationRequested();

                return UseCircuitBreakers.Value
                    ? ExecuteWithBreaker(cancellationToken)
                    : ExecuteAsync(cancellationToken);
            });

            // We could avoid passing both the token and timeout if either:
            // A. SmartThreadPool.GetResult() took a CancellationToken.
            // B. The CancellationToken provided an accessor for its Timeout.
            // C. We wrapped CancellationToken and Timeout in another class and passed it.
            // For now, this works, if a little janky.
            return workItem.Get(cancellationToken, Timeout);
        }

        private Task<TResult> ExecuteWithBreaker(CancellationToken cancellationToken)
        {
            if (!CircuitBreaker.IsAllowing())
            {
                throw new CircuitBreakerRejectedException();
            }

            Task<TResult> result;

            try
            {
                var stopwatch = Stopwatch.StartNew();
                result = ExecuteAsync(cancellationToken);
                CircuitBreaker.MarkSuccess(stopwatch.ElapsedMilliseconds);
                CircuitBreaker.Metrics.MarkCommandSuccess();
            }
            catch (Exception)
            {
                CircuitBreaker.Metrics.MarkCommandFailure();
                throw;
            }

            return result;
        }

        private static CommandCompletionStatus StatusFromException(Exception e)
        {
            if (IsCancellationException(e))
            {
                return CommandCompletionStatus.Canceled;
            }

            if (e is CircuitBreakerRejectedException || e is IsolationThreadPoolRejectedException)
            {
                return CommandCompletionStatus.Rejected;
            }

            return CommandCompletionStatus.Faulted;
        }

        private static bool IsCancellationException(Exception e)
        {
            return (e is TaskCanceledException || e is OperationCanceledException);
        }

        private TResult TryFallback(CommandFailedException instigator)
        {
            var stopwatch = Stopwatch.StartNew();
            var fallbackStatus = FallbackStatus.Success;

            var semaphore = FallbackSemaphore; // Locally reference in case the property gets updated (highly unlikely).
            if (!semaphore.TryEnter())
            {
                Riemann.Elapsed(RiemannKeyPrefix + " TryFallback", FallbackStatus.Rejected.ToString(), stopwatch.Elapsed);
                StatsDClient.ManualTime("mjolnir.command." + Name + ".fallback." + FallbackStatus.Rejected, stopwatch.ElapsedMilliseconds);

                instigator.FallbackStatus = FallbackStatus.Rejected;
                throw instigator;
            }
            
            try
            {
                return Fallback(instigator);
            }
            catch (Exception e)
            {
                var cfe = e as CommandFailedException;

                if (cfe != null && !cfe.IsFallbackImplemented)
                {
                    // This was rethrown from the default Fallback() implementation (here in the Command class).
                    fallbackStatus = FallbackStatus.NotImplemented;
                }
                else
                {
                    fallbackStatus = FallbackStatus.Failure;
                }

                if (cfe != null)
                {
                    cfe.FallbackStatus = fallbackStatus;
                }

                throw;
            }
            finally
            {
                semaphore.Release();

                stopwatch.Stop();
                Riemann.Elapsed(RiemannKeyPrefix + " TryFallback", fallbackStatus.ToString(), stopwatch.Elapsed);
                StatsDClient.ManualTime("mjolnir.command." + Name + ".fallback." + fallbackStatus, stopwatch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// The operation that should be performed when this command is invoked.
        /// </summary>
        /// <param name="cancellationToken">Token used to cancel and detect cancellation of the command.</param>
        /// <returns>A Task that will provide the command's result.</returns>
        protected abstract Task<TResult> ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>
        /// May be optionally implemented. Will be invoked if <see cref="ExecuteAsync(CancellationToken)"/> fails
        /// (for any reason: timeout, fault, rejected, etc.).
        /// 
        /// If you need to make another service (or other potentially-latent) call in the fallback, make sure
        /// to do it via a Command.
        /// 
        /// Although the triggering Exception (<see cref="instigator"/>) is provided, you don't have to use it. You
        /// may ignore it, rethrow it, wrap it, etc. If you decide not to rethrow the exception, it's recommended
        /// that you log it here; it won't be logged anywhere else.
        /// 
        /// Any exception thrown from this method will propagate up to the <code>Command</code> caller.
        /// </summary>
        /// <param name="instigator">The exception that triggered the fallback.</param>
        /// <returns>Result, likely from an alternative source (cache, solr, etc.).</returns>
        protected virtual TResult Fallback(CommandFailedException instigator)
        {
            instigator.IsFallbackImplemented = false;
            throw instigator;
        }
    }
}
