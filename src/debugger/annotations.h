// Copyright (c) 2026 Samsung Electronics Co., LTD
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.
#pragma once

#include "cor.h"
#include "cordebug.h"

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#include "interfaces/types.h"
#include "utils/torelease.h"

namespace netcoredbg
{

class Modules;

// Reader for debugger annotations (contract 1).
//
// A program records, per thread, short annotations that describe exceptions it is about to throw. They are kept in a
// small ring held in thread-static fields of a holder type, `System.Diagnostics.DebuggerAnnotationStore`:
//
//     internal static class DebuggerAnnotationStore        // no static constructor, not generic
//     {
//         [ThreadStatic] static Entry?[]? t_annotations;   // the ring, length a power of two in 1..64 (8 today)
//         [ThreadStatic] static int t_annotationCount;     // entries ever recorded on the thread, modulo 2^32
//         internal sealed class Entry { readonly object Value; readonly Exception? Exception; }
//     }
//
// At an exception stop the reader appends to the DAP `exceptionInfo` description the newest entry, in each store,
// whose `Exception` is the current exception object (compared by address). It searches the stopped thread first and,
// for a store with no match there, every other managed thread (an exception can be rethrown on another thread).
//
// The reader never runs code in the debuggee: it reads fields with ICorDebugClass::GetStaticFieldValue and
// ICorDebugObjectValue::GetFieldValue only, never through the expression evaluator (which creates a type object
// with a func-eval the first time a session touches a type's statics). That is exact only for a holder without a
// static constructor, so a holder that has one is never read. A string value is shown verbatim (truncated), any
// other value as its type name; ToString() is never called. Any ICorDebug error while reading a store skips that
// store for that thread; it never fails or delays the report.
class Annotations
{
public:

    static const char DefaultStoreName[];

    Annotations(std::shared_ptr<Modules> &sharedModules) :
        m_sharedModules(sharedModules),
        m_enabled(true),
        m_threadLimit(0),
        m_nextOrdinal(1),
        m_ringReads(0)
    {}

    // enable: the reader is on by default. extraStoreNames: holder type names to search besides the default one
    // (namespace-qualified, `+` for nested types); they must satisfy the same contract. threadLimit: at most this
    // many other threads are searched per store and stop (0: all).
    void SetOptions(bool enable, const std::vector<std::string> &extraStoreNames, unsigned threadLimit);

    // Stores are ordered by the order modules were observed, so record it as modules load (on attach, the runtime
    // reports already loaded modules first, in its own order).
    void ManagedCallbackLoadModule(ICorDebugModule *pModule, bool isCoreLib);
    void ManagedCallbackUnloadModule(ICorDebugModule *pModule);

    // Appends the annotations of the current exception of pThread to exceptionInfo.description.
    // Must be called for the DAP `exceptionInfo` request only, after the exception info was built (so after its
    // func-evals), with the process stopped at an exception stop of pThread.
    HRESULT AppendExceptionAnnotations(ICorDebugThread *pThread, ExceptionInfo &exceptionInfo);

    // Call when the process continues (the per-stop cache is valid for one stop only).
    void ClearStopCache();
    // Call at session end.
    void Cleanup();

    // A validated store: the metadata tokens of one holder type in one module.
    struct Store
    {
        std::string name;                       // holder type name, for the log
        mdTypeDef holderToken = mdTypeDefNil;
        mdFieldDef ringField = mdFieldDefNil;   // t_annotations
        mdFieldDef countField = mdFieldDefNil;  // t_annotationCount
        mdTypeDef entryToken = mdTypeDefNil;    // nested Entry
        mdFieldDef valueField = mdFieldDefNil;  // Entry.Value
        mdFieldDef exceptionField = mdFieldDefNil; // Entry.Exception
    };

private:

    struct ModuleStores
    {
        uint64_t ordinal = UINT64_MAX;          // the order the module was observed in; UINT64_MAX: not observed
        bool isCoreLib = false;
        bool scanned = false;
        std::vector<Store> stores;
    };

    struct StoreRef
    {
        ToRelease<ICorDebugModule> pModule;
        CORDB_ADDRESS moduleAddress = 0;
        uint64_t order = 0;
        Store store;
    };

    std::shared_ptr<Modules> m_sharedModules;

    std::mutex m_mutex;
    bool m_enabled;
    std::vector<std::string> m_extraStoreNames;
    unsigned m_threadLimit;
    uint64_t m_nextOrdinal;
    std::unordered_map<CORDB_ADDRESS, ModuleStores> m_modules;
    // Per stop: the text appended for each thread (by OS thread id) that exceptionInfo was asked for.
    std::unordered_map<DWORD, std::string> m_stopCache;
    // Every ring read since the session started; written to the log, so a test can see when reads happen.
    uint64_t m_ringReads;

    HRESULT GetStores(std::vector<StoreRef> &stores);
    void ScanModule(ICorDebugModule *pModule, std::vector<Store> &stores);
    HRESULT FindMatchOnThread(StoreRef &store, ICorDebugThread *pThread, CORDB_ADDRESS exceptionAddress, bool stoppedThread,
                              std::string &text);
    HRESULT ReadStoreOnThread(StoreRef &store, ICorDebugThread *pThread, CORDB_ADDRESS exceptionAddress, bool stoppedThread,
                              std::string &text);
    HRESULT SearchOtherThreads(StoreRef &store, ICorDebugThread *pStoppedThread, CORDB_ADDRESS exceptionAddress,
                               std::string &suffix);
};

} // namespace netcoredbg
