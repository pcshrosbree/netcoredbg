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
            // The default name and one extra name are listed twice: each store must still be read once.
            launchRequest.arguments.exceptionAnnotationStores = new List<string> {
                "System.Diagnostics.DebuggerAnnotationStore",
                "VSCodeTestExceptionAnnotationsStores.ExtraNamedStore",
                "VSCodeTestExceptionAnnotationsStores.ExtraNamedStore",
                "VSCodeTestExceptionAnnotationsStores.CctorStore",
                "VSCodeTestExceptionAnnotationsStores.ObjectRingStore",
                "VSCodeTestExceptionAnnotationsStores.ShortRingStore",
                "VSCodeTestExceptionAnnotationsStores.SharedStaticStore",
                "VSCodeTestExceptionAnnotationsStores.UnsealedEntryStore",
                "VSCodeTestExceptionAnnotationsStores.UnusedStore"
            };
            launchRequest.arguments.exceptionAnnotationThreadLimit = 1;
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

        public string GetDescription(string caller_trace, string excName)
        {
            ExceptionInfoRequest exceptionInfoRequest = new ExceptionInfoRequest();
            exceptionInfoRequest.arguments.threadId = threadId;
            var ret = VSCodeDebugger.Request(exceptionInfoRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            ExceptionInfoResponse exceptionInfoResponse =
                JsonConvert.DeserializeObject<ExceptionInfoResponse>(ret.ResponseStr);
            Assert.Equal("always", exceptionInfoResponse.body.breakMode, @"__FILE__:__LINE__"+"\n"+caller_trace);
            Assert.Equal("CLR/" + excName, exceptionInfoResponse.body.exceptionId, @"__FILE__:__LINE__"+"\n"+caller_trace);
            return exceptionInfoResponse.body.description;
        }

        public string ExpectedDescription(string excName, string excMessage, string[] annotations)
        {
            string expected = "Exception thrown: '" + excName + "' in VSCodeTestExceptionAnnotationsStores.dll: '" + excMessage + "'";
            foreach (string annotation in annotations)
                expected += "\n " + annotation;
            return expected;
        }

        // At the current exception stop, the exceptionInfo description must be exactly netcoredbg's own text for the
        // exception, followed by exactly the expected annotation lines, in order.
        public void TestAnnotations(string caller_trace, string excName, string excMessage, params string[] annotations)
        {
            string description = GetDescription(caller_trace, excName);
            Assert.Equal(ExpectedDescription(excName, excMessage, annotations), description, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        // The description must be one of two exact texts.
        public void TestAnnotationsEither(string caller_trace, string excName, string excMessage, string annotation1, string annotation2)
        {
            string description = GetDescription(caller_trace, excName);
            string expected1 = ExpectedDescription(excName, excMessage, new string[] { annotation1 });
            string expected2 = ExpectedDescription(excName, excMessage, new string[] { annotation2 });
            if (description == expected1 || description == expected2)
                return;
            Assert.Equal(expected1, description, @"__FILE__:__LINE__"+"\n"+caller_trace);
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

namespace VSCodeTestExceptionAnnotationsStores
{
    // Positive control (cell X): a conforming store under a name other than the default one.
    internal static class ExtraNamedStore
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
            Entry[] ring = t_annotations ?? (t_annotations = new Entry[8]);
            uint count = unchecked((uint)t_annotationCount);
            ring[count % 8] = new Entry(value, exception);
            t_annotationCount = unchecked((int)(count + 1));
        }
    }

    // A conforming store that no code uses, so the runtime never loads the class: nothing to read on any thread.
    internal static class UnusedStore
    {
        [ThreadStatic] private static Entry[] t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal sealed class Entry
        {
            internal readonly object Value;
            internal readonly Exception Exception;
            internal Entry(object value, Exception exception) { Value = value; Exception = exception; }
        }
    }

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

    // Decoy (cell TS): the contract's names and types, but ordinary statics (no [ThreadStatic]): one ring for the whole
    // process, so an entry recorded on one thread would read as recorded on every thread.
    internal static class SharedStaticStore
    {
        private static Entry[] t_annotations;
        private static int t_annotationCount;

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

    // Decoy (cell U): valid except that Entry is not sealed.
    internal static class UnsealedEntryStore
    {
        [ThreadStatic] private static Entry[] t_annotations;
        [ThreadStatic] private static int t_annotationCount;

        internal class Entry
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

        // Cell TS: the shared-static decoy is written on another thread, then the exception is thrown here.
        static void CellTS()
        {
            var exception = new InvalidOperationException("cell TS");
            var writer = new System.Threading.Thread(() => SharedStaticStore.Record("cell TS annotation", exception));
            writer.Start();
            writer.Join();
            ThrowUnannotated(exception);
        }

        // Cell LIM: two other threads hold an annotation of the exception thrown here; with a thread limit of 1, the
        // reader reads one of them only.
        static void CellLimit()
        {
            var exception = new InvalidOperationException("cell LIM");
            var ready = new CountdownEvent(2);
            var release = new ManualResetEvent(false);
            var first = new System.Threading.Thread(() => {
                DebuggerAnnotationStore.Annotate("cell LIM thread 1 annotation", exception);
                ready.Signal();
                release.WaitOne();
            });
            first.Name = "cell LIM thread 1";
            var second = new System.Threading.Thread(() => {
                DebuggerAnnotationStore.Annotate("cell LIM thread 2 annotation", exception);
                ready.Signal();
                release.WaitOne();
            });
            second.Name = "cell LIM thread 2";
            first.Start();
            second.Start();
            ready.Wait();

            ThrowUnannotated(exception);
            release.Set();
            first.Join();
            second.Join();
        }

        static void Main(string[] args)
        {
            Label.Checkpoint("init", "cell_X", (Object context) => {
                Context Context = (Context)context;
                Context.PrepareStart(@"__FILE__:__LINE__");
                Context.SetExceptionBreakpointsAll(@"__FILE__:__LINE__");
                Context.PrepareEnd(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell X: the exception is annotated in the default store and in the extra-named store; each listed twice
            // in the options, each read once, the default store first (scan order within a module).
            var exceptionX = new InvalidOperationException("cell X");
            DebuggerAnnotationStore.Annotate("cell X default store annotation", exceptionX);
            ExtraNamedStore.Record("cell X extra-named store annotation", exceptionX);
            ThrowUnannotated(exceptionX);

            Label.Checkpoint("cell_X", "cell_K", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell X",
                                        "Annotation: cell X default store annotation",
                                        "Annotation: cell X extra-named store annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell K: the only entry for this exception is in a store with a static constructor.
            var exceptionK = new InvalidOperationException("cell K");
            CctorStore.Record("cell K annotation", exceptionK);
            ThrowUnannotated(exceptionK);

            Label.Checkpoint("cell_K", "cell_Q", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell K");
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

            Label.Checkpoint("cell_Q", "cell_TS", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell Q");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell Q2");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellTS();

            Label.Checkpoint("cell_TS", "cell_U", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell TS");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell U: the only entry for this exception is in a store whose Entry is not sealed.
            var exceptionU = new InvalidOperationException("cell U");
            UnsealedEntryStore.Record("cell U annotation", exceptionU);
            ThrowUnannotated(exceptionU);

            Label.Checkpoint("cell_U", "cell_LIM", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell U");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellLimit();

            Label.Checkpoint("cell_LIM", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int first = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell LIM thread 1");
                int second = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell LIM thread 2");
                Context.TestAnnotationsEither(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell LIM",
                                              "Annotation (recorded on thread " + first + "): cell LIM thread 1 annotation",
                                              "Annotation (recorded on thread " + second + "): cell LIM thread 2 annotation");
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
