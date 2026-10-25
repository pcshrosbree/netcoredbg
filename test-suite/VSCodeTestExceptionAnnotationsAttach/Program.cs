using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

using NetcoreDbgTest;
using NetcoreDbgTest.VSCode;
using NetcoreDbgTest.Script;

using Newtonsoft.Json;

namespace NetcoreDbgTest.Script
{
    class Context
    {
        public void PrepareStart(string caller_trace)
        {
            InitializeRequest initializeRequest = new InitializeRequest();
            initializeRequest.arguments.clientID = "vscode";
            initializeRequest.arguments.clientName = "Visual Studio Code";
            initializeRequest.arguments.adapterID = "coreclr";
            initializeRequest.arguments.pathFormat = "path";
            initializeRequest.arguments.linesStartAt1 = true;
            initializeRequest.arguments.columnsStartAt1 = true;
            initializeRequest.arguments.supportsVariableType = true;
            initializeRequest.arguments.supportsVariablePaging = true;
            initializeRequest.arguments.supportsRunInTerminalRequest = true;
            initializeRequest.arguments.locale = "en-us";
            Assert.True(VSCodeDebugger.Request(initializeRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            testProcess = new Process();
            testProcess.StartInfo.UseShellExecute = false;
            testProcess.StartInfo.FileName = ControlInfo.CorerunPath;
            testProcess.StartInfo.Arguments = ControlInfo.TargetAssemblyPath;
            testProcess.StartInfo.CreateNoWindow = true;
            Assert.True(testProcess.Start(), @"__FILE__:__LINE__"+"\n"+caller_trace);

            AttachRequest attachRequest = new AttachRequest();
            attachRequest.arguments.processId = testProcess.Id;
            Assert.True(VSCodeDebugger.Request(attachRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void PrepareEnd(string caller_trace)
        {
            ConfigurationDoneRequest configurationDoneRequest = new ConfigurationDoneRequest();
            Assert.True(VSCodeDebugger.Request(configurationDoneRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void SetExceptionBreakpointsAll(string caller_trace)
        {
            SetExceptionBreakpointsRequest setExceptionBreakpointsRequest = new SetExceptionBreakpointsRequest();
            setExceptionBreakpointsRequest.arguments.filters.Add("all");
            Assert.True(VSCodeDebugger.Request(setExceptionBreakpointsRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasExceptionStop(string caller_trace)
        {
            Func<string, bool> filter = (resJSON) => {
                if (VSCodeDebugger.isResponseContainProperty(resJSON, "event", "stopped")
                    && VSCodeDebugger.isResponseContainProperty(resJSON, "reason", "exception")) {
                    threadId = Convert.ToInt32(VSCodeDebugger.GetResponsePropertyValue(resJSON, "threadId"));
                    return true;
                }
                return false;
            };

            Assert.True(VSCodeDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void TestAnnotations(string caller_trace, string excName, string excMessage, params string[] annotations)
        {
            ExceptionInfoRequest exceptionInfoRequest = new ExceptionInfoRequest();
            exceptionInfoRequest.arguments.threadId = threadId;
            var ret = VSCodeDebugger.Request(exceptionInfoRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            ExceptionInfoResponse exceptionInfoResponse =
                JsonConvert.DeserializeObject<ExceptionInfoResponse>(ret.ResponseStr);

            string expected = "Exception thrown: '" + excName + "' in VSCodeTestExceptionAnnotationsAttach.dll: '" + excMessage + "'";
            foreach (string annotation in annotations)
                expected += "\n " + annotation;
            Assert.Equal(expected, exceptionInfoResponse.body.description, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Continue(string caller_trace)
        {
            ContinueRequest continueRequest = new ContinueRequest();
            continueRequest.arguments.threadId = threadId;
            Assert.True(VSCodeDebugger.Request(continueRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Detach(string caller_trace)
        {
            DisconnectRequest disconnectRequest = new DisconnectRequest();
            disconnectRequest.arguments = new DisconnectArguments();
            disconnectRequest.arguments.restart = false;
            disconnectRequest.arguments.terminateDebuggee = false;
            Assert.True(VSCodeDebugger.Request(disconnectRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        // The debuggee exits with 0 only if its ring was cleared by its first annotation after the detach.
        public void WasDebuggeeExitWithCode0(string caller_trace)
        {
            Assert.True(testProcess.WaitForExit(60000), @"__FILE__:__LINE__"+"\n"+caller_trace);
            Assert.Equal(0, testProcess.ExitCode, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public int StoppedThreadId()
        {
            return threadId;
        }

        public Context(ControlInfo controlInfo, NetcoreDbgTestCore.DebuggerClient debuggerClient)
        {
            ControlInfo = controlInfo;
            VSCodeDebugger = new VSCodeDebugger(debuggerClient);
        }

        ControlInfo ControlInfo;
        VSCodeDebugger VSCodeDebugger;
        Process testProcess;
        int threadId = -1;
    }
}

#nullable enable

namespace System.Diagnostics
{
    // Debugger annotations, contract 1: this test carries its own library copy of the store. No static constructor
    // and no static field initializer.
    internal static class DebuggerAnnotationStore
    {
        private const int RingSize = 8;

        [ThreadStatic] private static Entry?[]? t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal sealed class Entry
        {
            internal readonly object Value;
            internal readonly Exception? Exception;

            internal Entry(object value, Exception? exception)
            {
                Value = value;
                Exception = exception;
            }
        }

        internal static void Annotate(string annotation, Exception exception)
        {
            if (annotation == null)
                throw new ArgumentNullException(nameof(annotation));
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));
            if (!Debugger.IsAttached)
            {
                ClearIfPresent();
                return;
            }
            Record(annotation, exception);
        }

        internal static void Record(object value, Exception? exception)
        {
            Entry?[] ring = t_annotations ??= new Entry?[RingSize];
            unchecked
            {
                uint count = (uint)t_annotationCount;
                ring[count % RingSize] = new Entry(value, exception);
                t_annotationCount = (int)(count + 1);
            }
        }

        // The first annotation after a detach drops the thread's old entries (and with them the exceptions they hold).
        internal static void ClearIfPresent()
        {
            if (t_annotationCount != 0 && t_annotations != null)
                t_annotations = null;
        }
    }
}

#nullable restore

namespace VSCodeTestExceptionAnnotationsAttach
{
    class Program
    {
        static bool WaitForAttachState(bool attached)
        {
            for (int i = 0; i < 600; i++)
            {
                if (Debugger.IsAttached == attached)
                    return true;
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        static int Main(string[] args)
        {
            Label.Checkpoint("init", "attached", (Object context) => {
                Context Context = (Context)context;
                Context.PrepareStart(@"__FILE__:__LINE__");
                Context.SetExceptionBreakpointsAll(@"__FILE__:__LINE__");
                Context.PrepareEnd(@"__FILE__:__LINE__");
            });

            if (!WaitForAttachState(true))
                return 2;

            // The reader works on an attached process.
            var exception = new InvalidOperationException("attach cell");
            try
            {
                DebuggerAnnotationStore.Annotate("attach cell annotation", exception);
                throw exception;
            }
            catch (Exception) {}

            Label.Checkpoint("attached", "detach", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "attach cell", "Annotation: attach cell annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell R: fill the ring while attached, stop once more, and detach there.
            for (int i = 0; i < 8; i++)
                DebuggerAnnotationStore.Annotate("cell R " + i, new InvalidOperationException("cell R filler"));
            try
            {
                throw new ArgumentException("cell R detach here");
            }
            catch (Exception) {}

            Label.Checkpoint("detach", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                // Nine writes: the ring has wrapped, so the reader can't rule out a displaced entry.
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell R detach here",
                                        "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + ")");
                Context.Detach(@"__FILE__:__LINE__");
            });

            if (!WaitForAttachState(false))
                return 3;

            // The first annotation that observes the detach clears this thread's ring.
            DebuggerAnnotationStore.Annotate("cell R after detach", new InvalidOperationException("cell R after detach"));
            object ring = typeof(DebuggerAnnotationStore)
                .GetField("t_annotations", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Console.WriteLine("cell R: ring after detach is " + (ring == null ? "null" : "not null"));

            Label.Checkpoint("finish", "", (Object context) => {
                Context Context = (Context)context;
                Context.WasDebuggeeExitWithCode0(@"__FILE__:__LINE__");
            });

            return ring == null ? 0 : 1;
        }
    }
}
