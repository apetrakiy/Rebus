﻿using System;
using System.Linq;
using System.Threading;
using Rebus2.Logging;
using Rebus2.Pipeline;
using Rebus2.Transport;

namespace Rebus2.Bus
{
    public class ThreadWorker : IWorker
    {
        static ILog _log;

        static ThreadWorker()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly ITransport _transport;
        readonly IPipeline _pipeline;
        readonly ThreadWorkerSynchronizationContext _threadWorkerSynchronizationContext;
        readonly Thread _workerThread;
        readonly IPipelineInvoker _pipelineInvoker;

        volatile bool _keepWorking = true;

        public ThreadWorker(ITransport transport, IPipeline pipeline, IPipelineInvoker pipelineInvoker, string workerName, ThreadWorkerSynchronizationContext threadWorkerSynchronizationContext)
        {
            _transport = transport;
            _pipeline = pipeline;
            _pipelineInvoker = pipelineInvoker;
            _threadWorkerSynchronizationContext = threadWorkerSynchronizationContext;
            _workerThread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(_threadWorkerSynchronizationContext);

                while (_keepWorking)
                {
                    DoWork();
                }
            })
            {
                Name = workerName
            };
            _log.Debug("Starting worker {0}", workerName);
            _workerThread.Start();
        }

        void DoWork()
        {
            try
            {
                var nextContinuationOrNull = _threadWorkerSynchronizationContext.GetNextContinuationOrNull();

                if (nextContinuationOrNull != null)
                {
                    nextContinuationOrNull();
                    return;
                }

                TryProcessMessage();
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Error while attempting to do work");
            }
        }

        async void TryProcessMessage()
        {
            using (var transactionContext = new DefaultTransactionContext())
            {
                try
                {
                    AmbientTransactionContext.Current = transactionContext;

                    var message = await _transport.Receive(transactionContext);

                    if (message == null)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(0.1));
                        return;
                    }

                    var context = new StepContext(message, transactionContext);
                    transactionContext.Items[StepContext.StepContextKey] = context;

                    var stagedReceiveSteps = _pipeline.ReceivePipeline();
                    await _pipelineInvoker.Invoke(context, stagedReceiveSteps.Select(s => s.Step));

                    transactionContext.Complete();
                }
                catch (Exception exception)
                {
                    _log.Error(exception, "Unhandled exception in thread worker");
                }
                finally
                {
                    //AmbientTransactionContext.Current = null;
                }
            }
        }

        public void Stop()
        {
            _keepWorking = false;
        }

        public void Dispose()
        {
            _keepWorking = false;
            _workerThread.Join();
        }
    }
}