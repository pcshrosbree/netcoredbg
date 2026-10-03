using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
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

            LaunchRequest launchRequest = new LaunchRequest();
            launchRequest.arguments.name = ".NET Core Launch (console) with pipeline";
            launchRequest.arguments.type = "coreclr";
            launchRequest.arguments.preLaunchTask = "build";
            launchRequest.arguments.program = ControlInfo.TargetAssemblyPath;
            launchRequest.arguments.cwd = "";
            launchRequest.arguments.console = "internalConsole";
            launchRequest.arguments.stopAtEntry = true;
            launchRequest.arguments.justMyCode = true;
            // Decoy holder types, read under the same contract rules as System.Diagnostics.DebuggerAnnotationStore.
            launchRequest.arguments.exceptionAnnotationStores = new List<string> {
                "VSCodeTestExceptionAnnotations.CctorStore",
                "VSCodeTestExceptionAnnotations.ObjectRingStore",
                "VSCodeTestExceptionAnnotations.ShortRingStore"
            };
            launchRequest.arguments.internalConsoleOptions = "openOnSessionStart";
            launchRequest.arguments.__sessionId = Guid.NewGuid().ToString();
            Assert.True(VSCodeDebugger.Request(launchRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void PrepareEnd(string caller_trace)
        {
            ConfigurationDoneRequest configurationDoneRequest = new ConfigurationDoneRequest();
            Assert.True(VSCodeDebugger.Request(configurationDoneRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasEntryPointHit(string caller_trace)
        {
            Func<string, bool> filter = (resJSON) => {
                if (VSCodeDebugger.isResponseContainProperty(resJSON, "event", "stopped")
                    && VSCodeDebugger.isResponseContainProperty(resJSON, "reason", "entry")) {
                    threadId = Convert.ToInt32(VSCodeDebugger.GetResponsePropertyValue(resJSON, "threadId"));
                    return true;
                }
                return false;
            };

            Assert.True(VSCodeDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasExit(string caller_trace)
        {
            bool wasExited = false;
            int ?exitCode = null;
            bool wasTerminated = false;

            Func<string, bool> filter = (resJSON) => {
                if (VSCodeDebugger.isResponseContainProperty(resJSON, "event", "exited")) {
                    wasExited = true;
                    ExitedEvent exitedEvent = JsonConvert.DeserializeObject<ExitedEvent>(resJSON);
                    exitCode = exitedEvent.body.exitCode;
                }
                if (VSCodeDebugger.isResponseContainProperty(resJSON, "event", "terminated")) {
                    wasTerminated = true;
                }
                if (wasExited && exitCode == 0 && wasTerminated)
                    return true;

                return false;
            };

            Assert.True(VSCodeDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void DebuggerExit(string caller_trace)
        {
            DisconnectRequest disconnectRequest = new DisconnectRequest();
            disconnectRequest.arguments = new DisconnectArguments();
            disconnectRequest.arguments.restart = false;
            Assert.True(VSCodeDebugger.Request(disconnectRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Continue(string caller_trace)
        {
            ContinueRequest continueRequest = new ContinueRequest();
            continueRequest.arguments.threadId = threadId;
            Assert.True(VSCodeDebugger.Request(continueRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
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

        public int ThreadIdByName(string caller_trace, string name)
        {
            ThreadsRequest threadsRequest = new ThreadsRequest();
            var ret = VSCodeDebugger.Request(threadsRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            ThreadsResponse threadsResponse = JsonConvert.DeserializeObject<ThreadsResponse>(ret.ResponseStr);
            foreach (NetcoreDbgTest.VSCode.Thread thread in threadsResponse.body.threads)
            {
                if (thread.name == name)
                {
                    Assert.True(thread.id != threadId, @"__FILE__:__LINE__"+"\n"+caller_trace);
                    return thread.id;
                }
            }

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        // At the current exception stop, the exceptionInfo description must be exactly netcoredbg's own text for the
        // exception, followed by exactly the expected annotation lines, in order.
        public void TestAnnotations(string caller_trace, string excName, string excMessage, params string[] annotations)
        {
            ExceptionInfoRequest exceptionInfoRequest = new ExceptionInfoRequest();
            exceptionInfoRequest.arguments.threadId = threadId;
            var ret = VSCodeDebugger.Request(exceptionInfoRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            ExceptionInfoResponse exceptionInfoResponse =
                JsonConvert.DeserializeObject<ExceptionInfoResponse>(ret.ResponseStr);
            Assert.Equal("always", exceptionInfoResponse.body.breakMode, @"__FILE__:__LINE__"+"\n"+caller_trace);
            Assert.Equal("CLR/" + excName, exceptionInfoResponse.body.exceptionId, @"__FILE__:__LINE__"+"\n"+caller_trace);

            string expected = "Exception thrown: '" + excName + "' in VSCodeTestExceptionAnnotations.dll: '" + excMessage + "'";
            foreach (string annotation in annotations)
                expected += "\n " + annotation;
            Assert.Equal(expected, exceptionInfoResponse.body.description, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public Context(ControlInfo controlInfo, NetcoreDbgTestCore.DebuggerClient debuggerClient)
        {
            ControlInfo = controlInfo;
            VSCodeDebugger = new VSCodeDebugger(debuggerClient);
        }

        ControlInfo ControlInfo;
        VSCodeDebugger VSCodeDebugger;
        int threadId = -1;
    }
}

#nullable enable

namespace System.Diagnostics
{
    // Debugger annotations, contract 1: this test carries its own library copy of the store. Debuggers read these
    // fields by name at a stop without running code. No static constructor and no static field initializer.
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

        internal static void ClearIfPresent()
        {
            if (t_annotationCount != 0 && t_annotations != null)
                t_annotations = null;
        }
    }
}

#nullable restore

namespace VSCodeTestExceptionAnnotations
{
    // Decoy (cell K): the contract's fields, but a static field initializer gives the type a static constructor, so a
    // debugger must not read it.
    internal static class CctorStore
    {
        internal static readonly int Seed = Environment.TickCount;

        [ThreadStatic] private static Entry[] t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal sealed class Entry
        {
            internal readonly object Value;
            internal readonly Exception Exception;
            internal Entry(object value, Exception exception) { Value = value; Exception = exception; }
        }

        internal static void Record(object value, Exception exception)
        {
            Entry[] ring = t_annotations ?? (t_annotations = new Entry[8]);
            uint count = unchecked((uint)t_annotationCount);
            ring[count % 8] = new Entry(value, exception);
            t_annotationCount = unchecked((int)(count + 1));
        }
    }

    // Decoy (cell Q): t_annotations is declared object[] (not Entry[]) and holds valid instances of this type's Entry.
    internal static class ObjectRingStore
    {
        [ThreadStatic] private static object[] t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal sealed class Entry
        {
            internal readonly object Value;
            internal readonly Exception Exception;
            internal Entry(object value, Exception exception) { Value = value; Exception = exception; }
        }

        internal static void Record(object value, Exception exception)
        {
            object[] ring = t_annotations ?? (t_annotations = new object[8]);
            uint count = unchecked((uint)t_annotationCount);
            ring[count % 8] = new Entry(value, exception);
            t_annotationCount = unchecked((int)(count + 1));
        }
    }

    // Decoy (cell Q'): valid in every way except that the ring has 3 slots, not a power of two.
    internal static class ShortRingStore
    {
        [ThreadStatic] private static Entry[] t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal sealed class Entry
        {
            internal readonly object Value;
            internal readonly Exception Exception;
            internal Entry(object value, Exception exception) { Value = value; Exception = exception; }
        }

        internal static void Record(object value, Exception exception)
        {
            Entry[] ring = t_annotations ?? (t_annotations = new Entry[3]);
            uint count = unchecked((uint)t_annotationCount);
            ring[count % 3] = new Entry(value, exception);
            t_annotationCount = unchecked((int)(count + 1));
        }
    }

    // A non-string annotation (cell P). A debugger shows its type name and must not call ToString().
    class CustomPayload
    {
        public override string ToString()
        {
            return "cell P ToString ran";
        }
    }

    class Program
    {
        static void ThrowAnnotated(Exception exception, string annotation)
        {
            try
            {
                DebuggerAnnotationStore.Annotate(annotation, exception);
                throw exception;
            }
            catch (Exception) {}
        }

        static void ThrowUnannotated(Exception exception)
        {
            try
            {
                throw exception;
            }
            catch (Exception) {}
        }

        // Cell C: two threads each throw their own annotated exception; each stop shows its own thread's annotation.
        static void CellC()
        {
            var firstDone = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var first = new System.Threading.Thread(() => {
                ThrowAnnotated(new InvalidOperationException("cell C thread 1"), "cell C thread 1 annotation");
                firstDone.Set();
                release.WaitOne();
            });
            first.Start();
            firstDone.WaitOne();

            var second = new System.Threading.Thread(() => {
                ThrowAnnotated(new InvalidOperationException("cell C thread 2"), "cell C thread 2 annotation");
            });
            second.Start();
            second.Join();
            release.Set();
            first.Join();
        }

        // Cell D: one exception object thrown at two sites, each annotated; the newest annotation wins.
        static void CellD()
        {
            var exception = new InvalidOperationException("cell D");
            ThrowAnnotated(exception, "cell D site 1 annotation");
            ThrowAnnotated(exception, "cell D site 2 annotation");
        }

        // Cell E: the ring keeps 8 entries per thread; 7 later annotations keep the entry, 8 evict it.
        static void CellE(int later, string name)
        {
            var exception = new InvalidOperationException(name);
            DebuggerAnnotationStore.Annotate(name + " annotation", exception);
            for (int i = 0; i < later; i++)
                DebuggerAnnotationStore.Annotate(name + " filler " + i, new InvalidOperationException("filler"));
            ThrowUnannotated(exception);
        }

        // Cell L: a thread that never touched the store stops while another thread holds entries.
        static void CellL()
        {
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var first = new System.Threading.Thread(() => {
                ThrowAnnotated(new InvalidOperationException("cell L thread 1"), "cell L thread 1 annotation");
                ready.Set();
                release.WaitOne();
            });
            first.Start();
            ready.WaitOne();

            var second = new System.Threading.Thread(() => {
                ThrowUnannotated(new ArgumentException("cell L thread 2"));
            });
            second.Start();
            second.Join();
            release.Set();
            first.Join();
        }

        // Cell M: the count wraps. Seeded to 0xFFFFFFFE, the target goes to slot 6, two more to slots 7 and 0, and the
        // count ends at 1.
        static void CellM()
        {
            var thread = new System.Threading.Thread(() => {
                typeof(DebuggerAnnotationStore).GetField("t_annotationCount", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, unchecked((int)0xFFFFFFFE));
                var target = new InvalidOperationException("cell M");
                DebuggerAnnotationStore.Annotate("cell M target annotation", target);
                DebuggerAnnotationStore.Annotate("cell M other 1", new InvalidOperationException("other 1"));
                DebuggerAnnotationStore.Annotate("cell M other 2", new InvalidOperationException("other 2"));
                ThrowUnannotated(target);
            });
            thread.Start();
            thread.Join();
        }

        // Cell N: the recording thread hands the exception over and blocks in a native wait; another thread rethrows it
        // with ExceptionDispatchInfo. The annotation is found on the recording thread and labelled with it.
        static void CellN()
        {
            ExceptionDispatchInfo captured = null;
            var handover = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var recorder = new System.Threading.Thread(() => {
                var exception = new InvalidOperationException("cell N");
                try
                {
                    DebuggerAnnotationStore.Annotate("cell N annotation", exception);
                    throw exception;
                }
                catch (Exception e)
                {
                    captured = ExceptionDispatchInfo.Capture(e);
                }
                handover.Set();
                release.WaitOne();
            });
            recorder.Name = "cell N recording thread";
            recorder.Start();
            handover.WaitOne();

            try
            {
                captured.Throw();
            }
            catch (Exception) {}
            release.Set();
            recorder.Join();
        }

        // Cell O: another thread holds an annotation of its own exception; an unrelated exception here shows nothing.
        static void CellO()
        {
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var other = new System.Threading.Thread(() => {
                ThrowAnnotated(new InvalidOperationException("cell O thread"), "cell O annotation");
                ready.Set();
                release.WaitOne();
            });
            other.Start();
            ready.WaitOne();

            ThrowUnannotated(new ArgumentException("cell O"));
            release.Set();
            other.Join();
        }

        // Cell T: two stores with the same name in two modules. The second is this assembly loaded again, from bytes,
        // into its own AssemblyLoadContext (a library copy per load context is a separate store). This module's store
        // matches on the stopped thread, the copy's (loaded later) only on another thread: both are shown, this
        // module's first.
        static void CellT()
        {
            var copy = new AssemblyLoadContext("cell T copy", false)
                .LoadFromStream(new MemoryStream(File.ReadAllBytes(typeof(Program).Assembly.Location)));
            MethodInfo annotateInCopy = copy.GetType("System.Diagnostics.DebuggerAnnotationStore")
                .GetMethod("Annotate", BindingFlags.NonPublic | BindingFlags.Static);

            var exception = new InvalidOperationException("cell T");
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var other = new System.Threading.Thread(() => {
                annotateInCopy.Invoke(null, new object[] { "cell T copy store annotation", exception });
                ready.Set();
                release.WaitOne();
            });
            other.Name = "cell T other thread";
            other.Start();
            ready.WaitOne();

            ThrowAnnotated(exception, "cell T test assembly store annotation");
            release.Set();
            other.Join();
        }

        static void Main(string[] args)
        {
            Label.Checkpoint("init", "cell_A", (Object context) => {
                Context Context = (Context)context;
                Context.PrepareStart(@"__FILE__:__LINE__");
                Context.SetExceptionBreakpointsAll(@"__FILE__:__LINE__");
                Context.PrepareEnd(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell A: an explicit throw, annotated at the throw site.
            ThrowAnnotated(new InvalidOperationException("cell A"), "cell A annotation");

            Label.Checkpoint("cell_A", "cell_B", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell A", "Annotation: cell A annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell B: A was caught; an unrelated, unannotated exception on the same thread shows nothing.
            ThrowUnannotated(new ArgumentException("cell B"));

            Label.Checkpoint("cell_B", "cell_C", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell B");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellC();

            Label.Checkpoint("cell_C", "cell_D", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell C thread 1", "Annotation: cell C thread 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell C thread 2", "Annotation: cell C thread 2 annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellD();

            Label.Checkpoint("cell_D", "cell_E", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell D", "Annotation: cell D site 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell D", "Annotation: cell D site 2 annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellE(7, "cell E7");
            CellE(8, "cell E8");

            Label.Checkpoint("cell_E", "cell_K", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell E7", "Annotation: cell E7 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell E8");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell K: the only entry for this exception is in a store with a static constructor.
            var exceptionK = new InvalidOperationException("cell K");
            CctorStore.Record("cell K annotation", exceptionK);
            ThrowUnannotated(exceptionK);

            Label.Checkpoint("cell_K", "cell_L", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell K");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellL();

            Label.Checkpoint("cell_L", "cell_M", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell L thread 1", "Annotation: cell L thread 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell L thread 2");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellM();

            Label.Checkpoint("cell_M", "cell_N", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell M", "Annotation: cell M target annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellN();

            Label.Checkpoint("cell_N", "cell_O", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell N", "Annotation: cell N annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int recorder = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell N recording thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell N",
                                        "Annotation (recorded on thread " + recorder + "): cell N annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellO();

            Label.Checkpoint("cell_O", "cell_P", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell O thread", "Annotation: cell O annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell O");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell P: a non-string annotation is shown as its type name, never through ToString().
            var exceptionP = new InvalidOperationException("cell P");
            DebuggerAnnotationStore.Record(new CustomPayload(), exceptionP);
            ThrowUnannotated(exceptionP);

            Label.Checkpoint("cell_P", "cell_Q", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell P", "Annotation: {VSCodeTestExceptionAnnotations.CustomPayload}");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell Q: the only entry for this exception is in a store whose ring is declared object[].
            var exceptionQ = new InvalidOperationException("cell Q");
            ObjectRingStore.Record("cell Q annotation", exceptionQ);
            ThrowUnannotated(exceptionQ);

            // Cell Q': the only entry for this exception is in a store whose ring has 3 slots.
            var exceptionQ2 = new InvalidOperationException("cell Q2");
            ShortRingStore.Record("cell Q2 annotation", exceptionQ2);
            ThrowUnannotated(exceptionQ2);

            Label.Checkpoint("cell_Q", "cell_T", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell Q");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell Q2");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellT();

            Label.Checkpoint("cell_T", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int other = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell T other thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell T",
                                        "Annotation: cell T test assembly store annotation",
                                        "Annotation (recorded on thread " + other + "): cell T copy store annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            Label.Checkpoint("finish", "", (Object context) => {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
        }
    }
}
