﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using System.Security.Principal;
using System.ServiceModel;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using WcfClientProxyGenerator.Async;
using WcfClientProxyGenerator.Policy;
using WcfClientProxyGenerator.Util;

namespace WcfClientProxyGenerator
{
    internal class RetryingWcfActionInvoker<TServiceInterface> : IActionInvoker<TServiceInterface> 
        where TServiceInterface : class
    {
        private static ConcurrentDictionary<Type, Lazy<MethodInfo>> PredicateCache
            = new ConcurrentDictionary<Type, Lazy<MethodInfo>>();
    
        private static ConcurrentDictionary<Type, Lazy<IEnumerable<Type>>> TypeHierarchyCache
            = new ConcurrentDictionary<Type, Lazy<IEnumerable<Type>>>();

        private static ConcurrentDictionary<Type, Lazy<MethodInfo>> ResponseHandlerPredicateCache
            = new ConcurrentDictionary<Type, Lazy<MethodInfo>>();
        
        private static ConcurrentDictionary<Type, Lazy<MethodInfo>> ResponseHandlerCache
            = new ConcurrentDictionary<Type, Lazy<MethodInfo>>();

        private readonly Type _originalServiceInterfaceType;

        private readonly IDictionary<Type, object> _retryPredicates;
        private readonly IDictionary<Type, ResponseHandlerHolder> _responseHandlers;
        
        /// <summary>
        /// The method that initializes new WCF action providers
        /// </summary>
        private readonly Func<TServiceInterface> _wcfActionProviderCreator;

        public RetryingWcfActionInvoker(
            Func<TServiceInterface> wcfActionProviderCreator, 
            Func<IDelayPolicy> delayPolicyFactory = null,
            int retryCount = 4)
        {
            RetryCount = retryCount;
            DelayPolicyFactory = delayPolicyFactory ?? DefaultProxyConfigurator.DefaultDelayPolicyFactory;

            _wcfActionProviderCreator = wcfActionProviderCreator;
            _retryPredicates = new Dictionary<Type, object>
            {
                { typeof(ChannelTerminatedException), null },
                { typeof(EndpointNotFoundException), null },
                { typeof(ServerTooBusyException), null }
            };

            _responseHandlers = new Dictionary<Type, ResponseHandlerHolder>();

            _originalServiceInterfaceType = GetOriginalServiceInterface();
        }

        /// <summary>
        /// Number of times the client will attempt to retry
        /// calls to the service in the event of some known WCF
        /// exceptions occurring
        /// </summary>
        public int RetryCount { get; set; }

        public Func<IDelayPolicy> DelayPolicyFactory { get; set; }

        /// <summary>
        /// Event that is fired immediately before the service method will be called. This event
        /// is called only once per request.
        /// </summary>
        public event OnCallBeginHandler OnCallBegin = delegate { };

        /// <summary>
        /// Event that is fired immediately after the request successfully or unsuccessfully completes.
        /// </summary>
        public event OnCallSuccessHandler OnCallSuccess = delegate { };

        /// <summary>
        /// Fires before the invocation of a service method, at every retry.
        /// </summary>
        public event OnInvokeHandler OnBeforeInvoke = delegate { };

        /// <summary>
        /// Fires after the successful invocation of a method.
        /// </summary>
        public event OnInvokeHandler OnAfterInvoke = delegate { };  

        /// <summary>
        /// Fires when an exception happens during the invocation of a service method, at every retry.
        /// </summary>
        public event OnExceptionHandler OnException = delegate { }; 

        public void AddExceptionToRetryOn<TException>(Predicate<TException> where = null)
            where TException : Exception
        {
            if (where == null)
            {
                where = _ => true;
            }

            _retryPredicates.Add(typeof(TException), where);
        }

        public void AddExceptionToRetryOn(Type exceptionType, Predicate<Exception> where = null)
        {
            if (where == null)
            {
                where = _ => true;
            }

            _retryPredicates.Add(exceptionType, where);
        }

        public void AddResponseToRetryOn<TResponse>(Predicate<TResponse> where)
        {
            _retryPredicates.Add(typeof(TResponse), where);
        }

        class ResponseHandlerHolder
        {
            public object Predicate { get; set; }
            public object ResponseHandler { get; set; }
        }

        public void AddResponseHandler<TResponse>(Func<TResponse, TResponse> handler, Predicate<TResponse> @where)
        {
            _responseHandlers.Add(typeof(TResponse), new ResponseHandlerHolder { Predicate = @where, ResponseHandler = handler });
        }

        /// <summary>
        /// Used to identify void return types in the Invoke() methods below.
        /// </summary>
        private struct VoidReturnType { }

        private Type GetOriginalServiceInterface()
        {
            Type serviceType = typeof(TServiceInterface);
            if (serviceType.HasAttribute<GeneratedAsyncInterfaceAttribute>())
                serviceType = serviceType.GetInterfaces()[0];

            return serviceType;
        }

        /// <summary>
        /// This function is called when a proxy's method is called that should return void.
        /// </summary>
        /// <param name="method">Method implementing the service call using WCF</param>
        /// <param name="invokeInfo"></param>
        public void Invoke(Action<TServiceInterface> method, InvokeInfo invokeInfo = null)
        {
            Invoke(provider =>
            {
                method(provider);
                return new VoidReturnType();
            }, invokeInfo);
        }

        /// <summary>
        /// This function is called when a proxy's method is called that should return something.
        /// </summary>
        /// <param name="method">Method implementing the service call using WCF</param>
        /// <param name="invokeInfo"></param>
        public TResponse Invoke<TResponse>(Func<TServiceInterface, TResponse> method, InvokeInfo invokeInfo = null)
        {
            TServiceInterface provider = this.RefreshProvider(null);
            TResponse lastResponse = default(TResponse);
            IDelayPolicy delayPolicy = this.DelayPolicyFactory();

            var sw = Stopwatch.StartNew();

            try
            {
                this.HandleOnCallBegin(invokeInfo);

                Exception mostRecentException = null;
                for (int i = 0; i < this.RetryCount + 1; i++)
                {
                    try
                    {
                        this.HandleOnBeforeInvoke(i, invokeInfo);

                        // make the service call
                        TResponse response = method(provider);

                        this.HandleOnAfterInvoke(i, response, invokeInfo);

                        if (this.ResponseInRetryable(response))
                        {
                            lastResponse = response;
                            provider = this.Delay(i, delayPolicy, provider);
                            continue;
                        }

                        sw.Stop();

                        response = this.ExecuteResponseHandlers(response);

                        this.HandleOnCallSuccess(sw.Elapsed, response, (i + 1), invokeInfo);
    
                        return response;
                    }
                    catch (Exception ex)
                    {
                        this.HandleOnException(ex, i, invokeInfo);

                        if (this.ExceptionIsRetryable(ex))
                        {
                            mostRecentException = ex;
                            provider = this.Delay(i, delayPolicy, provider);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }

                if (mostRecentException != null)
                {
                    if (RetryCount == 0)
                        throw mostRecentException;

                    throw new WcfRetryFailedException(
                        string.Format("WCF call failed after {0} retries.", this.RetryCount),
                        mostRecentException);
                }
            }
            finally
            {
                this.DisposeProvider(provider);
            }

            return lastResponse;
        }

        public Task InvokeAsync(Func<TServiceInterface, Task> method, InvokeInfo invokeInfo = null)
        {
            return this.InvokeAsync(async provider =>
            {
                await method(provider);
                return Task.FromResult(true);
            }, invokeInfo);
        }

        public async Task<TResponse> InvokeAsync<TResponse>(Func<TServiceInterface, Task<TResponse>> method, InvokeInfo invokeInfo = null)
        {
            TServiceInterface provider = RefreshProvider(null);
            TResponse lastResponse = default(TResponse);
            IDelayPolicy delayPolicy = DelayPolicyFactory();

            var sw = Stopwatch.StartNew();

            try
            {
                this.HandleOnCallBegin(invokeInfo);

                Exception mostRecentException = null;
                for (int i = 0; i < RetryCount + 1; i++)
                {
                    try
                    {
                        this.HandleOnBeforeInvoke(i, invokeInfo);

                        TResponse response = await method(provider);

                        this.HandleOnAfterInvoke(i, response, invokeInfo);

                        if (ResponseInRetryable(response))
                        {
                            lastResponse = response;
                            provider = await DelayAsync(i, delayPolicy, provider);
                            continue;
                        }

                        sw.Stop();

                        response = this.ExecuteResponseHandlers(response);

                        this.HandleOnCallSuccess(sw.Elapsed, response, (i + 1), invokeInfo);

                        return response;
                    }
                    catch (Exception ex)
                    {
                        this.HandleOnException(ex, i, invokeInfo);

                        // determine whether to retry the service call
                        if (ExceptionIsRetryable(ex))
                        {
                            mostRecentException = ex;
#if CSHARP60
                            provider = await DelayAsync(i, delayPolicy, provider);
#else
                            provider = Delay(i, delayPolicy, provider);
#endif
                        }
                        else
                        {
                            throw;
                        }                    
                    }
                }

                if (mostRecentException != null)
                {
                    if (RetryCount == 0)
                        throw mostRecentException;

                    throw new WcfRetryFailedException(
                        string.Format("WCF call failed after {0} attempts.", RetryCount),
                        mostRecentException);
                }
            }
            finally
            {
                DisposeProvider(provider);
            }
            
            return lastResponse;
        }

        private void HandleOnCallBegin(InvokeInfo invokeInfo)
        {
            this.OnCallBegin(this, new OnCallBeginHandlerArguments
            {
                ServiceType = _originalServiceInterfaceType,
                InvokeInfo = invokeInfo
            });
        }

        private void HandleOnBeforeInvoke(int retryCounter, InvokeInfo invokeInfo)
        {
            this.OnBeforeInvoke(this, new OnInvokeHandlerArguments
            {
                ServiceType = _originalServiceInterfaceType,
                RetryCounter = retryCounter,
                InvokeInfo = invokeInfo
            });
        }

        private void HandleOnAfterInvoke(int retryCounter, object response, InvokeInfo invokeInfo)
        {
            // set return value if non-void
            if (invokeInfo != null && response.GetType() != typeof(VoidReturnType))
            {
                invokeInfo.MethodHasReturnValue = true;
                invokeInfo.ReturnValue = response;
            }

            this.OnAfterInvoke(this, new OnInvokeHandlerArguments
            {
                ServiceType = _originalServiceInterfaceType,
                RetryCounter = retryCounter,
                InvokeInfo = invokeInfo,
            });            
        }

        private void HandleOnCallSuccess(TimeSpan callDuration, object response, int requestAttempts, InvokeInfo invokeInfo)
        {
            if (invokeInfo != null && response.GetType() != typeof (VoidReturnType))
            {
                invokeInfo.MethodHasReturnValue = true;
                invokeInfo.ReturnValue = response;
            }

            this.OnCallSuccess(this, new OnCallSuccessHandlerArguments
            {
                ServiceType = _originalServiceInterfaceType,
                InvokeInfo = invokeInfo,
                CallDuration = callDuration,
                RequestAttempts = requestAttempts
            });
        }

        private void HandleOnException(Exception exception, int retryCounter, InvokeInfo invokeInfo)
        {
            this.OnException(this, new OnExceptionHandlerArguments
            {
                Exception = exception,
                ServiceType = _originalServiceInterfaceType,
                RetryCounter = retryCounter,
                InvokeInfo = invokeInfo,
            });            
        }

        private TServiceInterface Delay(int iteration, IDelayPolicy delayPolicy, TServiceInterface provider)
        {
            Thread.Sleep(delayPolicy.GetDelay(iteration));
            return RefreshProvider(provider);
        }

        private async Task<TServiceInterface> DelayAsync(int iteration, IDelayPolicy delayPolicy, TServiceInterface provider)
        {
            await Task.Delay(delayPolicy.GetDelay(iteration));
            return RefreshProvider(provider);
        }

        private bool ExceptionIsRetryable(Exception ex)
        {
            return EvaluatePredicate(ex.GetType(), ex);
        }

        private TResponse ExecuteResponseHandlers<TResponse>(TResponse response)
        {
            Type @type = typeof(TResponse);
            var baseTypes = TypeHierarchyCache.GetOrAddSafe(@type, _ =>
            {
                return @type.GetAllInheritedTypes();
            });

            foreach (var baseType in baseTypes)
                response = this.ExecuteResponseHandlers(response, baseType);

            return response;
        }

        private TResponse ExecuteResponseHandlers<TResponse>(TResponse response, Type type)
        {
            if (!this._responseHandlers.ContainsKey(@type))
                return response;

            var responseHandlerHolder = this._responseHandlers[@type];

            MethodInfo predicateInvokeMethod = ResponseHandlerPredicateCache.GetOrAddSafe(@type, _ =>
            {
                Type predicateType = typeof(Predicate<>).MakeGenericType(@type);
                return predicateType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            });

            bool responseIsHandleable = responseHandlerHolder.Predicate == null
                                        || (bool) predicateInvokeMethod.Invoke(responseHandlerHolder.Predicate, new object[] { response });
            
            if (!responseIsHandleable)
                return response;

            MethodInfo handlerMethod = ResponseHandlerCache.GetOrAddSafe(@type, _ =>
            {
                Type actionType = typeof(Func<,>).MakeGenericType(@type, @type);
                return actionType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            });

            try
            {
                return (TResponse) handlerMethod.Invoke(responseHandlerHolder.ResponseHandler, new object[] { response });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        private bool ResponseInRetryable<TResponse>(TResponse response)
        {
            Type @type = typeof(TResponse);
            var baseTypes = TypeHierarchyCache.GetOrAddSafe(@type, _ =>
            {
                return @type.GetAllInheritedTypes();
            });

            return baseTypes.Any(t => EvaluatePredicate(t, response));
        }

        private bool EvaluatePredicate<TInstance>(Type key, TInstance instance)
        {
            if (!_retryPredicates.ContainsKey(key))
                return false;

            object predicate = _retryPredicates[key];

            if (predicate == null)
                return true;

            MethodInfo invokeMethod = PredicateCache.GetOrAddSafe(key, _ =>
            {
                Type predicateType = typeof(Predicate<>).MakeGenericType(key);
                return predicateType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            });

            return (bool) invokeMethod.Invoke(predicate, new object[] { instance });
        }

        /// <summary>
        /// Refreshes the proxy by disposing and recreating it if it's faulted.
        /// </summary>
        /// <param name="provider">The provider.</param>
        /// <returns></returns>
        private TServiceInterface RefreshProvider(TServiceInterface provider)
        {
            var communicationObject = provider as ICommunicationObject;
            if (communicationObject == null)
            {
                return _wcfActionProviderCreator();
            }

            if (communicationObject.State == CommunicationState.Opened)
            {
                return provider;
            }

            DisposeProvider(provider);
            return _wcfActionProviderCreator();
        }

        private void DisposeProvider(TServiceInterface provider)
        {
            var communicationObject = provider as ICommunicationObject;
            if (communicationObject == null)
            {
                return;
            }

            bool success = false;

            try
            {
                if (communicationObject.State != CommunicationState.Faulted)
                {
                    communicationObject.Close();
                    success = true;
                }
            }
            finally
            {
                if (!success)
                {
                    communicationObject.Abort();
                }
            }
        }
    }
}
