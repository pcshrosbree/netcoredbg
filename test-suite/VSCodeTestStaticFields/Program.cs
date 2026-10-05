using System;
using System.IO;
using System.Collections.Generic;

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

        public void AddBreakpoint(string caller_trace, string bpName, string Condition = null)
        {
            Breakpoint bp = ControlInfo.Breakpoints[bpName];
            Assert.Equal(BreakpointType.Line, bp.Type, @"__FILE__:__LINE__"+"\n"+caller_trace);
            var lbp = (LineBreakpoint)bp;

            BreakpointSourceName = lbp.FileName;
            BreakpointList.Add(new SourceBreakpoint(lbp.NumLine, Condition));
            BreakpointLines.Add(lbp.NumLine);
        }

        public void SetBreakpoints(string caller_trace)
        {
            SetBreakpointsRequest setBreakpointsRequest = new SetBreakpointsRequest();
            setBreakpointsRequest.arguments.source.name = BreakpointSourceName;
            // NOTE this code works only with one source file
            setBreakpointsRequest.arguments.source.path = ControlInfo.SourceFilesPath;
            setBreakpointsRequest.arguments.lines.AddRange(BreakpointLines);
            setBreakpointsRequest.arguments.breakpoints.AddRange(BreakpointList);
            setBreakpointsRequest.arguments.sourceModified = false;
            Assert.True(VSCodeDebugger.Request(setBreakpointsRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasBreakpointHit(string caller_trace, string bpName)
        {
            Func<string, bool> filter = (resJSON) => {
                if (VSCodeDebugger.isResponseContainProperty(resJSON, "event", "stopped")
                    && VSCodeDebugger.isResponseContainProperty(resJSON, "reason", "breakpoint")) {
                    threadId = Convert.ToInt32(VSCodeDebugger.GetResponsePropertyValue(resJSON, "threadId"));
                    return true;
                }
                return false;
            };

            Assert.True(VSCodeDebugger.IsEventReceived(filter), @"__FILE__:__LINE__"+"\n"+caller_trace);

            StackTraceRequest stackTraceRequest = new StackTraceRequest();
            stackTraceRequest.arguments.threadId = threadId;
            stackTraceRequest.arguments.startFrame = 0;
            stackTraceRequest.arguments.levels = 20;
            var ret = VSCodeDebugger.Request(stackTraceRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            Breakpoint breakpoint = ControlInfo.Breakpoints[bpName];
            Assert.Equal(BreakpointType.Line, breakpoint.Type, @"__FILE__:__LINE__"+"\n"+caller_trace);
            var lbp = (LineBreakpoint)breakpoint;

            StackTraceResponse stackTraceResponse =
                JsonConvert.DeserializeObject<StackTraceResponse>(ret.ResponseStr);

            if (stackTraceResponse.body.stackFrames[0].line == lbp.NumLine
                && stackTraceResponse.body.stackFrames[0].source.name == lbp.FileName
                // NOTE this code works only with one source file
                && stackTraceResponse.body.stackFrames[0].source.path == ControlInfo.SourceFilesPath)
                return;

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public Int64 DetectFrameId(string caller_trace, string bpName)
        {
            StackTraceRequest stackTraceRequest = new StackTraceRequest();
            stackTraceRequest.arguments.threadId = threadId;
            stackTraceRequest.arguments.startFrame = 0;
            stackTraceRequest.arguments.levels = 20;
            var ret = VSCodeDebugger.Request(stackTraceRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            Breakpoint breakpoint = ControlInfo.Breakpoints[bpName];
            Assert.Equal(BreakpointType.Line, breakpoint.Type, @"__FILE__:__LINE__"+"\n"+caller_trace);
            var lbp = (LineBreakpoint)breakpoint;

            StackTraceResponse stackTraceResponse =
                JsonConvert.DeserializeObject<StackTraceResponse>(ret.ResponseStr);

            if (stackTraceResponse.body.stackFrames[0].line == lbp.NumLine
                && stackTraceResponse.body.stackFrames[0].source.name == lbp.FileName
                // NOTE this code works only with one source file
                && stackTraceResponse.body.stackFrames[0].source.path == ControlInfo.SourceFilesPath)
                return stackTraceResponse.body.stackFrames[0].id;

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public int GetVariablesReference(string caller_trace, Int64 frameId, string ScopeName)
        {
            ScopesRequest scopesRequest = new ScopesRequest();
            scopesRequest.arguments.frameId = frameId;
            var ret = VSCodeDebugger.Request(scopesRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            ScopesResponse scopesResponse =
                JsonConvert.DeserializeObject<ScopesResponse>(ret.ResponseStr);

            foreach (var Scope in scopesResponse.body.scopes) {
                if (Scope.name == ScopeName) {
                    return Scope.variablesReference == null ? 0 : (int)Scope.variablesReference;
                }
            }

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public int GetChildVariablesReference(string caller_trace, int VariablesReference, string VariableName)
        {
            VariablesRequest variablesRequest = new VariablesRequest();
            variablesRequest.arguments.variablesReference = VariablesReference;
            var ret = VSCodeDebugger.Request(variablesRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            VariablesResponse variablesResponse =
                JsonConvert.DeserializeObject<VariablesResponse>(ret.ResponseStr);

            foreach (var Variable in variablesResponse.body.variables) {
                if (Variable.name == VariableName)
                    return Variable.variablesReference;
            }

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public int GetStaticMembersReference(string caller_trace, Int64 frameId, string VariableName)
        {
            int localsReference = GetVariablesReference(@"__FILE__:__LINE__"+"\n"+caller_trace, frameId, "Locals");
            int variableReference = GetChildVariablesReference(@"__FILE__:__LINE__"+"\n"+caller_trace, localsReference, VariableName);
            return GetChildVariablesReference(@"__FILE__:__LINE__"+"\n"+caller_trace, variableReference, "Static members");
        }

        public void GetAndCheckValue(string caller_trace, Int64 frameId, string Expression, string ExpectedType, string ExpectedResult)
        {
            EvaluateRequest evaluateRequest = new EvaluateRequest();
            evaluateRequest.arguments.expression = Expression;
            evaluateRequest.arguments.frameId = frameId;
            var ret = VSCodeDebugger.Request(evaluateRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            EvaluateResponse evaluateResponse =
                JsonConvert.DeserializeObject<EvaluateResponse>(ret.ResponseStr);

            Assert.Equal(ExpectedType, evaluateResponse.body.type, @"__FILE__:__LINE__"+"\n"+caller_trace);
            Assert.Equal(ExpectedResult, evaluateResponse.body.result, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void CheckErrorAtRequest(string caller_trace, Int64 frameId, string Expression, string errMsgStart)
        {
            EvaluateRequest evaluateRequest = new EvaluateRequest();
            evaluateRequest.arguments.expression = Expression;
            evaluateRequest.arguments.frameId = frameId;
            var ret = VSCodeDebugger.Request(evaluateRequest);
            Assert.False(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            EvaluateResponse evaluateResponse =
                JsonConvert.DeserializeObject<EvaluateResponse>(ret.ResponseStr);

            Assert.True(evaluateResponse.message.StartsWith(errMsgStart), @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void CheckNotValue(string caller_trace, Int64 frameId, string Expression, string NotExpectedResult)
        {
            EvaluateRequest evaluateRequest = new EvaluateRequest();
            evaluateRequest.arguments.expression = Expression;
            evaluateRequest.arguments.frameId = frameId;
            var ret = VSCodeDebugger.Request(evaluateRequest);
            if (!ret.Success)
                return;

            EvaluateResponse evaluateResponse =
                JsonConvert.DeserializeObject<EvaluateResponse>(ret.ResponseStr);

            Assert.NotEqual(NotExpectedResult, evaluateResponse.body.result, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void EvalVariable(string caller_trace, int variablesReference, string Type, string Name, string Value)
        {
            VariablesRequest variablesRequest = new VariablesRequest();
            variablesRequest.arguments.variablesReference = variablesReference;
            var ret = VSCodeDebugger.Request(variablesRequest);
            Assert.True(ret.Success, @"__FILE__:__LINE__"+"\n"+caller_trace);

            VariablesResponse variablesResponse =
                JsonConvert.DeserializeObject<VariablesResponse>(ret.ResponseStr);

            foreach (var Variable in variablesResponse.body.variables) {
                if (Variable.name == Name) {
                    Assert.Equal(Type, Variable.type, @"__FILE__:__LINE__"+"\n"+caller_trace);
                    Assert.Equal(Value, Variable.value, @"__FILE__:__LINE__"+"\n"+caller_trace);
                    return;
                }
            }

            throw new ResultNotSuccessException(@"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void SetVariable(string caller_trace, int variablesReference, string Name, string Value)
        {
            SetVariableRequest setVariableRequest = new SetVariableRequest();
            setVariableRequest.arguments.variablesReference = variablesReference;
            setVariableRequest.arguments.name = Name;
            setVariableRequest.arguments.value = Value;
            Assert.True(VSCodeDebugger.Request(setVariableRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void ErrorSetVariable(string caller_trace, int variablesReference, string Name, string Value)
        {
            SetVariableRequest setVariableRequest = new SetVariableRequest();
            setVariableRequest.arguments.variablesReference = variablesReference;
            setVariableRequest.arguments.name = Name;
            setVariableRequest.arguments.value = Value;
            Assert.False(VSCodeDebugger.Request(setVariableRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void SetExpression(string caller_trace, Int64 frameId, string Expression, string Value)
        {
            SetExpressionRequest setExpressionRequest = new SetExpressionRequest();
            setExpressionRequest.arguments.expression = Expression;
            setExpressionRequest.arguments.value = Value;
            Assert.True(VSCodeDebugger.Request(setExpressionRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void ErrorSetExpression(string caller_trace, Int64 frameId, string Expression, string Value)
        {
            SetExpressionRequest setExpressionRequest = new SetExpressionRequest();
            setExpressionRequest.arguments.expression = Expression;
            setExpressionRequest.arguments.value = Value;
            Assert.False(VSCodeDebugger.Request(setExpressionRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Continue(string caller_trace)
        {
            ContinueRequest continueRequest = new ContinueRequest();
            continueRequest.arguments.threadId = threadId;
            Assert.True(VSCodeDebugger.Request(continueRequest).Success, @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public Context(ControlInfo controlInfo, NetcoreDbgTestCore.DebuggerClient debuggerClient)
        {
            ControlInfo = controlInfo;
            VSCodeDebugger = new VSCodeDebugger(debuggerClient);
        }

        ControlInfo ControlInfo;
        VSCodeDebugger VSCodeDebugger;
        int threadId = -1;
        // NOTE this code works only with one source file
        string BreakpointSourceName;
        List<SourceBreakpoint> BreakpointList = new List<SourceBreakpoint>();
        List<int> BreakpointLines = new List<int>();
    }
}

namespace VSCodeTestStaticFields
{
    // Note, types below don't have static constructor (no static field initializers and no explicit static constructor),
    // so, debugger don't need evaluation for their static fields access.

    public class RefClass
    {
        public int field_i = 1;
    }

    public struct ValStruct
    {
        public int field_i;
    }

    public class NoCctor
    {
        public static int s_int;
        public static string s_string;
        public static RefClass s_ref;
        public static ValStruct s_struct;

        [ThreadStatic] public static int t_int;
        [ThreadStatic] public static long t_long;
        [ThreadStatic] public static bool t_bool;
        [ThreadStatic] public static double t_double;
        [ThreadStatic] public static string t_string;
        [ThreadStatic] public static object t_object;
        [ThreadStatic] public static RefClass t_ref;
        [ThreadStatic] public static volatile RefClass t_volatile_ref;
        [ThreadStatic] public static List<int> t_list;
        [ThreadStatic] public static ValStruct t_struct;
        [ThreadStatic] public static int[] t_array;

        public int field_i = 2;

        public static void StaticMethod()
        {
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            int dummy1 = 1;                                                 Label.Breakpoint("bp_static_method");
            long allocStaticMethod = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            Label.Checkpoint("bp_static_method", "bp_static_method_alloc", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_static_method");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_static_method");

                // Static members of method's class.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "s_int", "int", "111");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "t_int", "int", "121");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "s_ref.field_i", "int", "1");

                Context.Continue(@"__FILE__:__LINE__");
            });

            int dummy2 = 2;                                                 Label.Breakpoint("bp_static_method_alloc");

            Label.Checkpoint("bp_static_method_alloc", "bp_worker", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_static_method_alloc");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_static_method_alloc");

                // No evaluation (no object creation in debuggee) during "bp_static_method" breakpoint stop.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "allocStaticMethod", "long", "0");

                Context.Continue(@"__FILE__:__LINE__");
            });
        }
    }

    // Thread static fields with storage, that allocated at first access by thread.
    public class NoCctorThreadStorage
    {
        [ThreadStatic] public static long t_long0;
        [ThreadStatic] public static long t_long1;
        [ThreadStatic] public static long t_long2;
        [ThreadStatic] public static long t_long3;
        [ThreadStatic] public static long t_long4;
        [ThreadStatic] public static long t_long5;
        [ThreadStatic] public static long t_long6;
        [ThreadStatic] public static long t_long7;
    }

    public struct NoCctorStruct
    {
        public static int s_int;
        [ThreadStatic] public static int t_int;

        public int field_i;
    }

    // Not used by program code at all.
    public class NoCctorUntouched
    {
        public static int s_int;
        public static RefClass s_ref;
        [ThreadStatic] public static int t_int;
    }

    public class NoCctorBase
    {
        public static int base_s_int;
    }

    public class DerivedFromNoCctor : NoCctorBase
    {
        public static int derived_s_int;
    }

    public class NoCctorMembers
    {
        public static int s_int;
        public static int s_prop { get { return s_int + 1; } }
        public static int GetInt() { return s_int + 2; }
    }

    // Generic type without static constructor, debugger use type object for generic types.
    public class NoCctorGeneric<T>
    {
        public static T s_value;
        public static int s_int;
        [ThreadStatic] public static T t_value;
        [ThreadStatic] public static int t_int;

        public T field_value;
    }

    public class Counters
    {
        public static int cctor_runs;
        public static int generic_cctor_runs;
        public static int base_cctor_runs;
    }

    // Types below have static constructor, debugger should run it before static fields access (type object creation).

    public class WithCctor
    {
        public static int s_int = 5;

        static WithCctor()
        {
            Counters.cctor_runs++;
        }
    }

    public class WithCctorThreadStorage
    {
        public static int s_int = 8;
        [ThreadStatic] public static long t_long0;
        [ThreadStatic] public static long t_long1;
        [ThreadStatic] public static long t_long2;
        [ThreadStatic] public static long t_long3;
        [ThreadStatic] public static long t_long4;
        [ThreadStatic] public static long t_long5;
        [ThreadStatic] public static long t_long6;
        [ThreadStatic] public static long t_long7;
    }

    public class WithCctorGeneric<T>
    {
        public static int s_int = 6;

        static WithCctorGeneric()
        {
            Counters.generic_cctor_runs++;
        }
    }

    public class BaseWithCctor
    {
        public static int base_s_int = 7;

        static BaseWithCctor()
        {
            Counters.base_cctor_runs++;
        }
    }

    public class DerivedNoCctor : BaseWithCctor
    {
        public static int derived_s_int;
    }

    class Program
    {
        static void Main(string[] args)
        {
            Label.Checkpoint("init", "bp_main", (Object context) => {
                Context Context = (Context)context;
                Context.PrepareStart(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_main");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_main_alloc");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_static_method");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_static_method_alloc");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_worker");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_worker_alloc");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_cctor");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp_cctor_alloc");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.PrepareEnd(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

            NoCctor.s_int = 11;
            NoCctor.s_string = "s_string";
            NoCctor.s_ref = new RefClass();
            NoCctor.s_struct.field_i = 12;
            NoCctor.t_int = 21;
            NoCctor.t_long = 22;
            NoCctor.t_bool = true;
            NoCctor.t_double = 2.5;
            NoCctor.t_string = "t_string";
            NoCctor.t_object = new RefClass();
            NoCctor.t_ref = new RefClass();
            NoCctor.t_volatile_ref = new RefClass();
            NoCctor.t_list = new List<int>();
            NoCctor.t_struct.field_i = 23;
            NoCctor.t_array = new int[] { 1, 2 };
            NoCctorStruct.s_int = 31;
            NoCctorStruct.t_int = 32;
            NoCctorThreadStorage.t_long0 = 60;
            NoCctorThreadStorage.t_long7 = 67;
            NoCctorMembers.s_int = 50;
            DerivedNoCctor.derived_s_int = 51;
            NoCctorBase.base_s_int = 70;
            DerivedFromNoCctor.derived_s_int = 71;
            NoCctorGeneric<int>.s_value = 41;
            NoCctorGeneric<int>.s_int = 42;
            NoCctorGeneric<int>.t_value = 43;
            NoCctorGeneric<int>.t_int = 44;
            NoCctorGeneric<string>.s_value = "generic_s_value";
            NoCctorGeneric<string>.t_value = "generic_t_value";

            NoCctor noCctor = new NoCctor();
            NoCctorThreadStorage noCctorThreadStorage = new NoCctorThreadStorage();
            NoCctorStruct noCctorStruct = new NoCctorStruct();
            NoCctorGeneric<int> noCctorGeneric = new NoCctorGeneric<int>();
            DerivedNoCctor derivedNoCctor = new DerivedNoCctor();
            DerivedFromNoCctor derivedFromNoCctor = new DerivedFromNoCctor();

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            int dummy1 = 1;                                                 Label.Breakpoint("bp_main");
            long allocMain = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            Label.Checkpoint("bp_main", "bp_main_alloc", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_main");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_main");

                // Watch (evaluate request), static and thread static fields with value.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_int", "int", "11");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_string", "string", "\"s_string\"");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_ref", "VSCodeTestStaticFields.RefClass", "{VSCodeTestStaticFields.RefClass}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_ref.field_i", "int", "1");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_struct", "VSCodeTestStaticFields.ValStruct", "{VSCodeTestStaticFields.ValStruct}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_struct.field_i", "int", "12");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_int", "int", "21");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_long", "long", "22");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_bool", "bool", "true");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_double", "double", "2.5");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_string", "string", "\"t_string\"");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_object", "VSCodeTestStaticFields.RefClass", "{VSCodeTestStaticFields.RefClass}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_ref", "VSCodeTestStaticFields.RefClass", "{VSCodeTestStaticFields.RefClass}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_volatile_ref", "VSCodeTestStaticFields.RefClass", "{VSCodeTestStaticFields.RefClass}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_list", "System.Collections.Generic.List<int>", "{System.Collections.Generic.List<int>}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_struct.field_i", "int", "23");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_array", "int[]", "{int[2]}");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorStruct.s_int", "int", "31");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorStruct.t_int", "int", "32");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long0", "long", "60");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long7", "long", "67");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "VSCodeTestStaticFields.NoCctor.s_int", "int", "11");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "VSCodeTestStaticFields.NoCctor.t_string", "string", "\"t_string\"");

                // Type that was never used by program code.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorUntouched.s_int", "int", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorUntouched.s_ref", "VSCodeTestStaticFields.RefClass", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorUntouched.t_int", "int", "0");

                // Variables (static members of variable).
                int staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctor");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "s_int", "11");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "string", "s_string", "\"s_string\"");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "VSCodeTestStaticFields.RefClass", "s_ref", "{VSCodeTestStaticFields.RefClass}");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "VSCodeTestStaticFields.ValStruct", "s_struct", "{VSCodeTestStaticFields.ValStruct}");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "t_int", "21");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "long", "t_long", "22");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "bool", "t_bool", "true");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "double", "t_double", "2.5");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "string", "t_string", "\"t_string\"");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int[]", "t_array", "{int[2]}");
                staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctorStruct");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "s_int", "31");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "t_int", "32");

                // Base type without static constructor.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "DerivedFromNoCctor.derived_s_int", "int", "71");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "DerivedFromNoCctor.base_s_int", "int", "70");
                staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "derivedFromNoCctor");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "derived_s_int", "71");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "base_s_int", "70");

                // Change static and thread static fields.
                staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctor");
                Context.SetVariable(@"__FILE__:__LINE__", staticsReference, "s_int", "111");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "s_int", "111");
                Context.SetExpression(@"__FILE__:__LINE__", frameId, "NoCctor.t_int", "121");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_int", "int", "121");

                Context.Continue(@"__FILE__:__LINE__");
            });

            int dummy2 = 2;                                                 Label.Breakpoint("bp_main_alloc");

            Label.Checkpoint("bp_main_alloc", "bp_static_method", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_main_alloc");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_main_alloc");

                // No evaluation (no object creation in debuggee) during "bp_main" breakpoint stop.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "allocMain", "long", "0");
                // Changed by debugger at "bp_main".
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_int", "int", "111");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_int", "int", "121");

                // Generic type without static constructor.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<int>.s_value", "int", "41");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<int>.s_int", "int", "42");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<int>.t_value", "int", "43");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<int>.t_int", "int", "44");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<string>.s_value", "string", "\"generic_s_value\"");
                // Thread static field of shared generic instantiation could be reported by debugger API as not available,
                // even if field have storage and value, debugger must not show default value for it.
                Context.CheckNotValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<string>.t_value", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorGeneric<long>.s_value", "long", "0");
                int staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctorGeneric");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "s_value", "41");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "t_value", "43");

                // Type with static constructor (type object created here, static constructor run by this thread).
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "WithCctorThreadStorage.s_int", "int", "8");

                Context.Continue(@"__FILE__:__LINE__");
            });

            NoCctor.StaticMethod();

            System.Threading.Thread workerThread = new System.Threading.Thread(WorkerThread);
            workerThread.Start();
            workerThread.Join();

            allocBefore = GC.GetAllocatedBytesForCurrentThread();
            int dummy3 = 3;                                                 Label.Breakpoint("bp_cctor");
            long allocCctor = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            Label.Checkpoint("bp_cctor", "bp_cctor_alloc", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_cctor");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_cctor");

                // Type with static constructor, that was not run yet: debugger run static constructor before static field access.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "Counters.cctor_runs", "int", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "WithCctor.s_int", "int", "5");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "Counters.cctor_runs", "int", "1");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "Counters.generic_cctor_runs", "int", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "WithCctorGeneric<int>.s_int", "int", "6");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "Counters.generic_cctor_runs", "int", "1");

                // Base type with static constructor.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "Counters.base_cctor_runs", "int", "1");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "DerivedNoCctor.derived_s_int", "int", "51");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "DerivedNoCctor.base_s_int", "int", "7");
                int staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "derivedNoCctor");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "derived_s_int", "51");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "base_s_int", "7");

                // Static property and method of type without static constructor.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorMembers.s_prop", "int", "51");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorMembers.GetInt()", "int", "52");

                Context.Continue(@"__FILE__:__LINE__");
            });

            int dummy4 = 4;                                                 Label.Breakpoint("bp_cctor_alloc");

            Label.Checkpoint("bp_cctor_alloc", "finish", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_cctor_alloc");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_cctor_alloc");

                // Evaluation (type object creation) was used during "bp_cctor" breakpoint stop.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "allocCctor > 0", "bool", "true");

                Context.Continue(@"__FILE__:__LINE__");
            });

            Label.Checkpoint("finish", "", (Object context) => {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
        }

        static void WorkerThread()
        {
            NoCctor noCctor = new NoCctor();
            NoCctorThreadStorage noCctorThreadStorage = new NoCctorThreadStorage();

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            int dummy1 = 1;                                                 Label.Breakpoint("bp_worker");
            long allocWorker = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

            Label.Checkpoint("bp_worker", "bp_worker_alloc", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_worker");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_worker");

                // Static fields are shared by all threads.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_int", "int", "111");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.s_string", "string", "\"s_string\"");

                // Watch, thread static fields never accessed by this thread have default value.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_int", "int", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_long", "long", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_bool", "bool", "false");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_double", "double", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_string", "string", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_object", "object", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_ref", "VSCodeTestStaticFields.RefClass", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctor.t_volatile_ref", "VSCodeTestStaticFields.RefClass", "null");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorStruct.t_int", "int", "0");
                // Default value of struct, array and generic type instance can't be created without evaluation.
                Context.CheckErrorAtRequest(@"__FILE__:__LINE__", frameId, "NoCctor.t_struct", "error");
                Context.CheckErrorAtRequest(@"__FILE__:__LINE__", frameId, "NoCctor.t_array", "error");
                Context.CheckErrorAtRequest(@"__FILE__:__LINE__", frameId, "NoCctor.t_list", "error");

                // Variables (static members of variable), thread static fields never accessed by this thread have default value.
                int staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctor");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "s_int", "111");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "int", "t_int", "0");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "long", "t_long", "0");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "bool", "t_bool", "false");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "double", "t_double", "0");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "string", "t_string", "null");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "object", "t_object", "null");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "VSCodeTestStaticFields.RefClass", "t_ref", "null");

                // Thread static field without storage on this thread have default value and can't be changed.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long0", "long", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long7", "long", "0");
                Context.ErrorSetExpression(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long0", "5");
                staticsReference = Context.GetStaticMembersReference(@"__FILE__:__LINE__", frameId, "noCctorThreadStorage");
                Context.EvalVariable(@"__FILE__:__LINE__", staticsReference, "long", "t_long7", "0");
                Context.ErrorSetVariable(@"__FILE__:__LINE__", staticsReference, "t_long7", "5");

                // Thread static field without storage on this thread, type with static constructor.
                Context.CheckErrorAtRequest(@"__FILE__:__LINE__", frameId, "WithCctorThreadStorage.t_long0", "error");

                Context.Continue(@"__FILE__:__LINE__");
            });

            int dummy2 = 2;                                                 Label.Breakpoint("bp_worker_alloc");

            Label.Checkpoint("bp_worker_alloc", "bp_cctor", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp_worker_alloc");
                Int64 frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp_worker_alloc");

                // No evaluation (no object creation in debuggee) during "bp_worker" breakpoint stop.
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "allocWorker", "long", "0");
                // Not changed by debugger at "bp_worker".
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long0", "long", "0");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "NoCctorThreadStorage.t_long7", "long", "0");

                Context.Continue(@"__FILE__:__LINE__");
            });
        }
    }
}
