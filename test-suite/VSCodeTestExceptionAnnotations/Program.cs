using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
            string expected = "Exception thrown: '" + excName + "' in VSCodeTestExceptionAnnotations.dll: '" + excMessage + "'";
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

        // As TestAnnotations, for an exception with an inner exception: netcoredbg's own text names its type first.
        public void TestInnerAnnotations(string caller_trace, string excName, string excMessage, string innerName, params string[] annotations)
        {
            var lines = new List<string> {
                "Inner exceptions found, see $exception in variables window for more details.",
                "Innermost exception: " + innerName
            };
            lines.AddRange(annotations);
            TestAnnotations(caller_trace, excName, excMessage, lines.ToArray());
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

        // A second store with the same name in another module: this assembly loaded again, from bytes, into its own
        // AssemblyLoadContext (a library copy per load context is a separate store). Loaded at cell L, after this
        // module, so it comes second in store order.
        static MethodInfo annotateInCopy;

        static void LoadCopy()
        {
            var copy = new AssemblyLoadContext("annotation store copy", false)
                .LoadFromStream(new MemoryStream(File.ReadAllBytes(typeof(Program).Assembly.Location)));
            annotateInCopy = copy.GetType("System.Diagnostics.DebuggerAnnotationStore")
                .GetMethod("Annotate", BindingFlags.NonPublic | BindingFlags.Static);
        }

        static void AnnotateInCopy(string annotation, Exception exception)
        {
            annotateInCopy.Invoke(null, new object[] { annotation, exception });
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

        // Runs a cell on a thread of its own, so the stopping thread's ring starts empty.
        static void OnNewThread(Action cell)
        {
            var thread = new System.Threading.Thread(() => cell());
            thread.Start();
            thread.Join();
        }

        // Starts a thread that runs `record` and then waits, alive with a managed frame, until the returned action is
        // called.
        static Action StartWaitingThread(string name, Action record)
        {
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var thread = new System.Threading.Thread(() => {
                record();
                ready.Set();
                release.WaitOne();
            });
            thread.Name = name;
            thread.Start();
            ready.WaitOne();
            return () => { release.Set(); thread.Join(); };
        }

        // links[0] wraps links[1], which wraps links[2], and so on; links[depth] has no inner exception.
        static Exception[] Chain(string name, int depth)
        {
            var links = new Exception[depth + 1];
            links[depth] = new ArgumentException(name + " inner " + depth);
            for (int i = depth - 1; i >= 0; i--)
                links[i] = new InvalidOperationException(i == 0 ? name : name + " inner " + i, links[i + 1]);
            return links;
        }

        static void Fill(int count)
        {
            for (int i = 0; i < count; i++)
                DebuggerAnnotationStore.Annotate("filler " + i, new InvalidOperationException("filler"));
        }

        // Cell I: the stopped exception has no entry, but an exception in its inner-exception chain does.
        static void CellI()
        {
            // I1: the inner exception, annotated where it was thrown, then wrapped and thrown again.
            OnNewThread(() => {
                var inner = new ArgumentException("cell I1 inner");
                try
                {
                    DebuggerAnnotationStore.Annotate("cell I1 inner annotation", inner);
                    throw inner;
                }
                catch (Exception e)
                {
                    ThrowUnannotated(new InvalidOperationException("cell I1", e));
                }
            });

            // I2: the inner exception's entry is on another thread.
            Exception[] i2 = Chain("cell I2", 1);
            Action endI2 = StartWaitingThread("cell I2 recording thread", () => DebuggerAnnotationStore.Annotate("cell I2 inner annotation", i2[1]));
            OnNewThread(() => ThrowUnannotated(i2[0]));
            endI2();

            // I3: two links down; of two entries for that link, the newer.
            OnNewThread(() => {
                Exception[] links = Chain("cell I3", 2);
                DebuggerAnnotationStore.Annotate("cell I3 inner 2 older annotation", links[2]);
                DebuggerAnnotationStore.Annotate("cell I3 inner 2 annotation", links[2]);
                ThrowUnannotated(links[0]);
            });

            // I4: the nearest link wins over a newer entry for a farther one.
            OnNewThread(() => {
                Exception[] links = Chain("cell I4", 1);
                DebuggerAnnotationStore.Annotate("cell I4 outer annotation", links[0]);
                DebuggerAnnotationStore.Annotate("cell I4 inner 1 annotation", links[1]);
                ThrowUnannotated(links[0]);
            });
            OnNewThread(() => {
                Exception[] links = Chain("cell I4b", 2);
                DebuggerAnnotationStore.Annotate("cell I4b inner 1 annotation", links[1]);
                DebuggerAnnotationStore.Annotate("cell I4b inner 2 annotation", links[2]);
                ThrowUnannotated(links[0]);
            });

            // I5: the exception's own entry on another thread wins over its inner exception's on the stopped thread.
            Exception[] i5 = Chain("cell I5", 1);
            Action endI5 = StartWaitingThread("cell I5 recording thread", () => DebuggerAnnotationStore.Annotate("cell I5 outer annotation", i5[0]));
            OnNewThread(() => {
                DebuggerAnnotationStore.Annotate("cell I5 inner annotation", i5[1]);
                ThrowUnannotated(i5[0]);
            });
            endI5();

            // I6: at the same link, the stopped thread's entry wins over another thread's.
            Exception[] i6 = Chain("cell I6", 1);
            Action endI6 = StartWaitingThread("cell I6 recording thread", () => DebuggerAnnotationStore.Annotate("cell I6 other thread annotation", i6[1]));
            OnNewThread(() => {
                DebuggerAnnotationStore.Annotate("cell I6 stopped thread annotation", i6[1]);
                ThrowUnannotated(i6[0]);
            });
            endI6();

            // I7: two other threads, one with an entry for the exception, one for its inner exception: only the first.
            Exception[] i7 = Chain("cell I7", 1);
            Action endI7a = StartWaitingThread("cell I7 inner thread", () => DebuggerAnnotationStore.Annotate("cell I7 inner annotation", i7[1]));
            Action endI7b = StartWaitingThread("cell I7 outer thread", () => DebuggerAnnotationStore.Annotate("cell I7 outer annotation", i7[0]));
            OnNewThread(() => ThrowUnannotated(i7[0]));
            endI7a();
            endI7b();
        }

        // Cell ID: the walk follows 16 inner exceptions. At 16 the entry is found; at 17 the search is incomplete; a
        // chain of exactly 16 with no entry shows nothing.
        static void CellID()
        {
            OnNewThread(() => {
                Exception[] links = Chain("cell ID16", 16);
                DebuggerAnnotationStore.Annotate("cell ID16 inner 16 annotation", links[16]);
                ThrowUnannotated(links[0]);
            });
            OnNewThread(() => {
                Exception[] links = Chain("cell ID17", 17);
                DebuggerAnnotationStore.Annotate("cell ID17 inner 17 annotation", links[17]);
                ThrowUnannotated(links[0]);
            });
            OnNewThread(() => ThrowUnannotated(Chain("cell ID16n", 16)[0]));
            // ID17n: a chain of 17 and no entry on any thread: still incomplete.
            OnNewThread(() => ThrowUnannotated(Chain("cell ID17n", 17)[0]));
        }

        // Cell W: no entry for the exception, and a ring searched has had more writes than it holds, so the entry may
        // have been displaced. W2 has exactly 8 writes (nothing displaced); in W4 the ring has wrapped but the entry
        // is there.
        static void CellW()
        {
            OnNewThread(() => {
                var exception = new InvalidOperationException("cell W1");
                DebuggerAnnotationStore.Annotate("cell W1 annotation", exception);
                Fill(8);
                ThrowUnannotated(exception);
            });
            OnNewThread(() => {
                Fill(8);
                ThrowUnannotated(new InvalidOperationException("cell W2"));
            });
            Action endW3 = StartWaitingThread("cell W3 wrapped thread", () => Fill(9));
            OnNewThread(() => ThrowUnannotated(new InvalidOperationException("cell W3")));
            endW3();
            OnNewThread(() => {
                var exception = new InvalidOperationException("cell W4");
                Fill(8);
                DebuggerAnnotationStore.Annotate("cell W4 annotation", exception);
                ThrowUnannotated(exception);
            });
            // W5: the stopped thread's ring and another thread's have wrapped. W6: a wrapped ring and a chain longer
            // than the walk follows.
            Action endW5 = StartWaitingThread("cell W5 wrapped thread", () => Fill(9));
            OnNewThread(() => {
                Fill(9);
                ThrowUnannotated(new InvalidOperationException("cell W5"));
            });
            endW5();
            OnNewThread(() => {
                Fill(9);
                ThrowUnannotated(Chain("cell W6", 17)[0]);
            });
        }

        // Cell E: the ring keeps 8 entries per thread; 7 later annotations keep the entry, 8 evict it, and the reader
        // says the entry may not be retained.
        static void CellE(int later, string name)
        {
            var exception = new InvalidOperationException(name);
            DebuggerAnnotationStore.Annotate(name + " annotation", exception);
            for (int i = 0; i < later; i++)
                DebuggerAnnotationStore.Annotate(name + " filler " + i, new InvalidOperationException("filler"));
            ThrowUnannotated(exception);
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

        // Cell M2: the count has rolled over to 2 (seeded to 0xFFFFFFFC, six entries in slots 4..7, 0, 1) and nothing
        // matches: entries past slot 2 show the ring has wrapped. Cell M3: three entries and the count set back to 2, as
        // on a thread stopped between the slot store and the count store: slot 2 is occupied, nothing has wrapped.
        static void CellM2()
        {
            FieldInfo countField = typeof(DebuggerAnnotationStore).GetField("t_annotationCount", BindingFlags.NonPublic | BindingFlags.Static);
            OnNewThread(() => {
                countField.SetValue(null, unchecked((int)0xFFFFFFFC));
                Fill(6);
                ThrowUnannotated(new InvalidOperationException("cell M2"));
            });
            OnNewThread(() => {
                Fill(3);
                countField.SetValue(null, 2);
                ThrowUnannotated(new InvalidOperationException("cell M3"));
            });
        }

        // Cell N: the recording thread hands the exception over and blocks in a wait; another thread rethrows it with
        // ExceptionDispatchInfo. The annotation is found on the recording thread and labelled with it.
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

        // Cell O: another thread holds an annotation of its own exception; an unrelated exception here does not get it.
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

        // Cell L: a thread that never touched this module's store stops (its read of that store finds no storage,
        // which must mean "empty"); its exception is annotated in the copy's store, which must still be shown.
        // Every other thread alive at that stop has storage in both stores, so the only "no storage" read is the one
        // on the stopping thread.
        static void CellL()
        {
            LoadCopy();
            AnnotateInCopy("cell L main thread filler", new InvalidOperationException("filler"));
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var first = new System.Threading.Thread(() => {
                AnnotateInCopy("cell L thread 1 filler", new InvalidOperationException("filler"));
                ThrowAnnotated(new InvalidOperationException("cell L thread 1"), "cell L thread 1 annotation");
                ready.Set();
                release.WaitOne();
            });
            first.Start();
            ready.WaitOne();

            var second = new System.Threading.Thread(() => {
                var exception = new InvalidOperationException("cell L thread 2");
                AnnotateInCopy("cell L copy store annotation", exception);
                ThrowUnannotated(exception);
            });
            second.Start();
            second.Join();
            release.Set();
            first.Join();
        }

        // Cell T: two stores with the same name in two modules. This module's store matches on the stopped thread, the
        // copy's (loaded later) only on another thread: both are shown, this module's first.
        static void CellT()
        {
            var exception = new InvalidOperationException("cell T");
            var ready = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var other = new System.Threading.Thread(() => {
                AnnotateInCopy("cell T copy store annotation", exception);
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

        // A native wait entered from a DynamicMethod: the thread's leaf managed frame is the dynamic method, which the
        // debugger does not report as the thread's active IL frame, so a reader finds the thread's managed frame only by
        // walking its stack. (A wait reached through an ordinary P/Invoke still reports an IL active frame on Linux.)
        static Action NativeWaitFromDynamicMethod()
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string library = windows ? "kernel32.dll" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libSystem.dylib" : "libc.so.6";
            IntPtr wait = NativeLibrary.GetExport(NativeLibrary.Load(library), windows ? "Sleep" : "sleep");
            Type returnType = windows ? typeof(void) : typeof(uint);
            uint duration = windows ? 600000u : 600u; // milliseconds on Windows, seconds elsewhere

            var method = new System.Reflection.Emit.DynamicMethod("AnnotationsNativeWait", typeof(void), new[] { typeof(IntPtr), typeof(uint) }, typeof(Program).Module);
            System.Reflection.Emit.ILGenerator il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.EmitCalli(System.Reflection.Emit.OpCodes.Calli, windows ? CallingConvention.StdCall : CallingConvention.Cdecl, returnType, new[] { typeof(uint) });
            if (returnType != typeof(void))
                il.Emit(System.Reflection.Emit.OpCodes.Pop);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            var call = (Action<IntPtr, uint>)method.CreateDelegate(typeof(Action<IntPtr, uint>));
            return () => call(wait, duration);
        }

        // Cell N2: as N, but the recording thread then blocks in native code called from a DynamicMethod, so the only way
        // to read its ring is through a managed frame found by walking its stack. It is a background thread, left blocked
        // until the process exits, so this cell comes last.
        static void CellN2()
        {
            Action block = NativeWaitFromDynamicMethod();
            ExceptionDispatchInfo captured = null;
            var handover = new ManualResetEvent(false);
            var recorder = new System.Threading.Thread(() => {
                var exception = new InvalidOperationException("cell N2");
                try
                {
                    DebuggerAnnotationStore.Annotate("cell N2 annotation", exception);
                    throw exception;
                }
                catch (Exception e)
                {
                    captured = ExceptionDispatchInfo.Capture(e);
                }
                handover.Set();
                block();
            });
            recorder.Name = "cell N2 recording thread";
            recorder.IsBackground = true;
            recorder.Start();
            handover.WaitOne();
            System.Threading.Thread.Sleep(1000); // let the recorder enter the native wait

            try
            {
                captured.Throw();
            }
            catch (Exception) {}
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

            Label.Checkpoint("cell_D", "cell_I", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell D", "Annotation: cell D site 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell D", "Annotation: cell D site 2 annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellI();

            Label.Checkpoint("cell_I", "cell_ID", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell I1 inner", "Annotation: cell I1 inner annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I1", "System.ArgumentException",
                                             "Annotation (inner exception 1): cell I1 inner annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int recorderI2 = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell I2 recording thread");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I2", "System.ArgumentException",
                                             "Annotation (inner exception 1, recorded on thread " + recorderI2 + "): cell I2 inner annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I3", "System.InvalidOperationException",
                                             "Annotation (inner exception 2): cell I3 inner 2 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I4", "System.ArgumentException",
                                             "Annotation: cell I4 outer annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I4b", "System.InvalidOperationException",
                                             "Annotation (inner exception 1): cell I4b inner 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int recorderI5 = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell I5 recording thread");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I5", "System.ArgumentException",
                                             "Annotation (recorded on thread " + recorderI5 + "): cell I5 outer annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I6", "System.ArgumentException",
                                             "Annotation (inner exception 1): cell I6 stopped thread annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int outerI7 = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell I7 outer thread");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell I7", "System.ArgumentException",
                                             "Annotation (recorded on thread " + outerI7 + "): cell I7 outer annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellID();

            Label.Checkpoint("cell_ID", "cell_W", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell ID16", "System.InvalidOperationException",
                                             "Annotation (inner exception 16): cell ID16 inner 16 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell ID17", "System.InvalidOperationException",
                                             "Annotation: search incomplete (inner exception chain longer than 16)");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell ID16n", "System.InvalidOperationException");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell ID17n", "System.InvalidOperationException",
                                             "Annotation: search incomplete (inner exception chain longer than 16)");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellW();

            Label.Checkpoint("cell_W", "cell_M2", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W1",
                                        "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + ")");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W2");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int wrappedW3 = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell W3 wrapped thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W3",
                                        "Annotation: possibly not retained (ring wrapped on thread " + wrappedW3 + ")");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W4", "Annotation: cell W4 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int wrappedW5 = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell W5 wrapped thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W5",
                                        "Annotation: possibly not retained (rings wrapped on threads " + Context.StoppedThreadId() + ", " + wrappedW5 + ")");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestInnerAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell W6", "System.InvalidOperationException",
                                             "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + "); " +
                                             "search incomplete (inner exception chain longer than 16)");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Here rather than beside cell M: after cell E this thread's own ring has wrapped, which every no-match
            // line would name.
            CellM2();

            Label.Checkpoint("cell_M2", "cell_E", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell M2",
                                        "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + ")");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell M3");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellE(7, "cell E7");
            CellE(8, "cell E8");

            Label.Checkpoint("cell_E", "cell_M", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell E7", "Annotation: cell E7 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell E8",
                                        "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + ")");
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
                // This thread's ring has wrapped since cell E.
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.ArgumentException", "cell O",
                                        "Annotation: possibly not retained (ring wrapped on thread " + Context.StoppedThreadId() + ")");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Cell P: a non-string annotation is shown as its type name, never through ToString().
            var exceptionP = new InvalidOperationException("cell P");
            DebuggerAnnotationStore.Record(new CustomPayload(), exceptionP);
            ThrowUnannotated(exceptionP);

            Label.Checkpoint("cell_P", "strings", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell P", "Annotation: {VSCodeTestExceptionAnnotations.CustomPayload}");
                Context.Continue(@"__FILE__:__LINE__");
            });

            // Strings a debugger must render without failing: an unpaired surrogate (shown as U+FFFD), an embedded NUL
            // (kept), and a long string whose truncation point falls inside a surrogate pair (cut before the pair).
            ThrowAnnotated(new InvalidOperationException("cell S1"), "\uDC00");
            ThrowAnnotated(new InvalidOperationException("cell S2"), "before\0after");
            ThrowAnnotated(new InvalidOperationException("cell S3"), new string('x', 4095) + "\uD83D\uDE00" + "tail");
            // Surrogate endpoints: a lone DFFF (U+FFFD); pairs at both ends of each range, which must arrive as the
            // same code points (U+103FF, U+10FFFF, U+10FC00, U+10000); a pair ending in DFFF straddling the limit.
            ThrowAnnotated(new InvalidOperationException("cell S4"), "\uDFFF");
            ThrowAnnotated(new InvalidOperationException("cell S5"), "\uD800\uDFFF");
            ThrowAnnotated(new InvalidOperationException("cell S6"), "\uDBFF\uDFFF");
            ThrowAnnotated(new InvalidOperationException("cell S7"), "\uDBFF\uDC00");
            ThrowAnnotated(new InvalidOperationException("cell S8"), "\uD800\uDC00");
            ThrowAnnotated(new InvalidOperationException("cell S9"), new string('x', 4095) + "\uD800\uDFFF" + "tail");

            Label.Checkpoint("strings", "cell_L", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S1", "Annotation: \uFFFD");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S2", "Annotation: before\0after");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S3", "Annotation: " + new string('x', 4095) + "...");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S4", "Annotation: \uFFFD");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S5", "Annotation: \uD800\uDFFF");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S6", "Annotation: \uDBFF\uDFFF");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S7", "Annotation: \uDBFF\uDC00");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S8", "Annotation: \uD800\uDC00");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell S9", "Annotation: " + new string('x', 4095) + "...");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellL();

            Label.Checkpoint("cell_L", "cell_T", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell L thread 1", "Annotation: cell L thread 1 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell L thread 2", "Annotation: cell L copy store annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellT();

            Label.Checkpoint("cell_T", "cell_N2", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int other = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell T other thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell T",
                                        "Annotation: cell T test assembly store annotation",
                                        "Annotation (recorded on thread " + other + "): cell T copy store annotation");
                Context.Continue(@"__FILE__:__LINE__");
            });

            CellN2();

            Label.Checkpoint("cell_N2", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell N2", "Annotation: cell N2 annotation");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExceptionStop(@"__FILE__:__LINE__");
                int recorder = Context.ThreadIdByName(@"__FILE__:__LINE__", "cell N2 recording thread");
                Context.TestAnnotations(@"__FILE__:__LINE__", "System.InvalidOperationException", "cell N2",
                                        "Annotation (recorded on thread " + recorder + "): cell N2 annotation");
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
