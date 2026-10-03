// Copyright (c) 2026 Samsung Electronics Co., LTD
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/annotations.h"

#include <algorithm>
#include "debugger/frames.h"
#include "debugger/valueprint.h"
#include "metadata/modules.h"
#include "metadata/typeprinter.h"
#include "utils/logger.h"
#include "utils/utf.h"

namespace netcoredbg
{

const char Annotations::DefaultStoreName[] = "System.Diagnostics.DebuggerAnnotationStore";

namespace
{
    // Longest annotation text shown, in UTF-16 code units.
    const ULONG32 MaxAnnotationLength = 4096;

    DWORD GetThreadOSId(ICorDebugThread *pThread)
    {
        DWORD threadId = 0;
        if (FAILED(pThread->GetID(&threadId)))
            return 0;
        return threadId;
    }

    HRESULT GetMetaDataImport(ICorDebugModule *pModule, IMetaDataImport **ppMD)
    {
        HRESULT Status;
        ToRelease<IUnknown> pMDUnknown;
        IfFailRet(pModule->GetMetaDataInterface(IID_IMetaDataImport, &pMDUnknown));
        return pMDUnknown->QueryInterface(IID_IMetaDataImport, (LPVOID*) ppMD);
    }

    // "Ns.Outer+Inner" -> type token in this module, or mdTypeDefNil.
    mdTypeDef FindTypeByName(IMetaDataImport *pMD, const std::string &fullName)
    {
        mdTypeDef typeToken = mdTypeDefNil;
        std::string::size_type start = 0;
        while (start <= fullName.size())
        {
            std::string::size_type end = fullName.find('+', start);
            const std::string part = fullName.substr(start, end == std::string::npos ? std::string::npos : end - start);
            mdTypeDef nextToken = mdTypeDefNil;
            if (part.empty() ||
                FAILED(pMD->FindTypeDefByName(reinterpret_cast<LPCWSTR>(to_utf16(part).c_str()), typeToken, &nextToken)) ||
                nextToken == mdTypeDefNil)
                return mdTypeDefNil;
            typeToken = nextToken;
            if (end == std::string::npos)
                break;
            start = end + 1;
        }
        return typeToken;
    }

    bool HasTypeInitializer(IMetaDataImport *pMD, mdTypeDef typeToken)
    {
        HCORENUM hEnum = NULL;
        mdMethodDef methodDef = mdMethodDefNil;
        ULONG count = 0;
        HRESULT Status = pMD->EnumMethodsWithName(&hEnum, typeToken, W(".cctor"), &methodDef, 1, &count);
        pMD->CloseEnum(hEnum);
        // Fail closed: a type whose methods can't be enumerated is treated as if it had one.
        return FAILED(Status) || count != 0;
    }

    bool HasGenericParameters(IMetaDataImport *pMD, mdTypeDef typeToken)
    {
        ToRelease<IMetaDataImport2> pMD2;
        if (FAILED(pMD->QueryInterface(IID_IMetaDataImport2, (LPVOID*) &pMD2)))
            return true;
        HCORENUM hEnum = NULL;
        mdGenericParam genericParam = mdGenericParamNil;
        ULONG count = 0;
        HRESULT Status = pMD2->EnumGenericParams(&hEnum, typeToken, &genericParam, 1, &count);
        pMD2->CloseEnum(hEnum);
        return FAILED(Status) || count != 0;
    }

    // Finds a field by name; it must be static (or an instance field) as asked, and not a literal.
    HRESULT FindField(IMetaDataImport *pMD, mdTypeDef typeToken, const WCHAR *name, bool isStatic,
                      mdFieldDef &fieldToken, PCCOR_SIGNATURE &sigBlob, ULONG &sigSize)
    {
        HRESULT Status;
        HCORENUM hEnum = NULL;
        ULONG count = 0;
        fieldToken = mdFieldDefNil;
        Status = pMD->EnumFieldsWithName(&hEnum, typeToken, name, &fieldToken, 1, &count);
        pMD->CloseEnum(hEnum);
        if (FAILED(Status) || count != 1)
            return E_FAIL;

        DWORD fieldAttr = 0;
        IfFailRet(pMD->GetFieldProps(fieldToken, nullptr, nullptr, 0, nullptr, &fieldAttr, &sigBlob, &sigSize,
                                     nullptr, nullptr, nullptr));
        if (((fieldAttr & fdStatic) != 0) != isStatic || (fieldAttr & fdLiteral) != 0)
            return E_FAIL;
        return S_OK;
    }

    // Reads a field signature: `FIELD elementType [token]`, nothing else (no custom modifiers).
    bool ParseFieldSignature(PCCOR_SIGNATURE sigBlob, ULONG sigSize, CorElementType expected1, CorElementType expected2,
                             mdToken *pToken)
    {
        PCCOR_SIGNATURE sigEnd = sigBlob + sigSize;
        if (sigBlob == nullptr || sigSize < 2 || *sigBlob++ != IMAGE_CEE_CS_CALLCONV_FIELD)
            return false;
        if (CorElementType(*sigBlob++) != expected1)
            return false;
        if (expected2 != ELEMENT_TYPE_END)
        {
            if (sigBlob >= sigEnd || CorElementType(*sigBlob++) != expected2)
                return false;
        }
        if (pToken != nullptr)
        {
            if (sigBlob >= sigEnd)
                return false;
            sigBlob += CorSigUncompressToken(sigBlob, pToken);
        }
        return sigBlob == sigEnd;
    }

    bool IsSystemException(IMetaDataImport *pMD, mdToken typeToken)
    {
        WCHAR name[mdNameLen];
        ULONG nameLen = 0;
        if (TypeFromToken(typeToken) == mdtTypeDef)
        {
            mdTypeDef enclosing = mdTypeDefNil;
            if (SUCCEEDED(pMD->GetNestedClassProps(typeToken, &enclosing)) && enclosing != mdTypeDefNil)
                return false;
            if (FAILED(pMD->GetTypeDefProps(typeToken, name, _countof(name), &nameLen, nullptr, nullptr)))
                return false;
        }
        else if (TypeFromToken(typeToken) == mdtTypeRef)
        {
            mdToken scope = mdTokenNil;
            if (FAILED(pMD->GetTypeRefProps(typeToken, &scope, name, _countof(name), &nameLen)) ||
                TypeFromToken(scope) == mdtTypeRef) // nested type
                return false;
        }
        else
            return false;

        // Compared by namespace and name, never by token or assembly: a library copy refers to a facade.
        return to_utf8(name) == "System.Exception";
    }

    // The field signatures of contract 1 (a store whose signatures differ is ignored, never partly read):
    //   t_annotations      SZARRAY CLASS <Entry, a TypeDef nested in the holder, in this module>
    //   t_annotationCount  I4
    //   Entry.Value        OBJECT
    //   Entry.Exception    CLASS <System.Exception>
    HRESULT CheckStoreSignatures(IMetaDataImport *pMD, mdTypeDef entryToken,
                                 PCCOR_SIGNATURE ringSig, ULONG ringSigSize, PCCOR_SIGNATURE countSig, ULONG countSigSize,
                                 PCCOR_SIGNATURE valueSig, ULONG valueSigSize, PCCOR_SIGNATURE exceptionSig, ULONG exceptionSigSize)
    {
        mdToken elementToken = mdTokenNil;
        if (!ParseFieldSignature(ringSig, ringSigSize, ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_CLASS, &elementToken) ||
            elementToken != entryToken)
            return E_FAIL;
        if (!ParseFieldSignature(countSig, countSigSize, ELEMENT_TYPE_I4, ELEMENT_TYPE_END, nullptr))
            return E_FAIL;
        if (!ParseFieldSignature(valueSig, valueSigSize, ELEMENT_TYPE_OBJECT, ELEMENT_TYPE_END, nullptr))
            return E_FAIL;
        mdToken exceptionToken = mdTokenNil;
        if (!ParseFieldSignature(exceptionSig, exceptionSigSize, ELEMENT_TYPE_CLASS, ELEMENT_TYPE_END, &exceptionToken) ||
            !IsSystemException(pMD, exceptionToken))
            return E_FAIL;
        return S_OK;
    }

    // Validates one holder type against contract 1. Returns E_FAIL, with the reason in whyNot, if it is not a contract
    // store; such a type is ignored, never partly read.
    HRESULT ValidateStore(IMetaDataImport *pMD, mdTypeDef holderToken, Annotations::Store &store, std::string &whyNot)
    {
        // A holder with a type initializer is never read directly: its fields could be read before the initializer
        // ran. Without one, a field's value before and after initialization is the same (all zero), so a direct read
        // is exact, and no func-eval is needed to "initialize" the type first.
        if (HasTypeInitializer(pMD, holderToken))
        {
            whyNot = "it has a static constructor";
            return E_FAIL;
        }
        // Defensive: the runtime can't give a generic class's static field without a type instantiation anyway.
        if (HasGenericParameters(pMD, holderToken))
        {
            whyNot = "it is generic";
            return E_FAIL;
        }

        mdTypeDef entryToken = mdTypeDefNil;
        if (FAILED(pMD->FindTypeDefByName(W("Entry"), holderToken, &entryToken)) || entryToken == mdTypeDefNil)
        {
            whyNot = "it has no nested Entry type";
            return E_FAIL;
        }

        mdFieldDef ringField, countField, valueField, exceptionField;
        PCCOR_SIGNATURE ringSig, countSig, valueSig, exceptionSig;
        ULONG ringSigSize, countSigSize, valueSigSize, exceptionSigSize;
        if (FAILED(FindField(pMD, holderToken, W("t_annotations"), true, ringField, ringSig, ringSigSize)) ||
            FAILED(FindField(pMD, holderToken, W("t_annotationCount"), true, countField, countSig, countSigSize)) ||
            FAILED(FindField(pMD, entryToken, W("Value"), false, valueField, valueSig, valueSigSize)) ||
            FAILED(FindField(pMD, entryToken, W("Exception"), false, exceptionField, exceptionSig, exceptionSigSize)))
        {
            whyNot = "a contract field is missing";
            return E_FAIL;
        }

        if (FAILED(CheckStoreSignatures(pMD, entryToken, ringSig, ringSigSize, countSig, countSigSize,
                                        valueSig, valueSigSize, exceptionSig, exceptionSigSize)))
        {
            whyNot = "its field signatures differ from contract 1";
            return E_FAIL;
        }

        store.holderToken = holderToken;
        store.entryToken = entryToken;
        store.ringField = ringField;
        store.countField = countField;
        store.valueField = valueField;
        store.exceptionField = exceptionField;
        return S_OK;
    }

    // S_OK and the address of a non-null object reference; S_FALSE for null.
    HRESULT GetObjectAddress(ICorDebugValue *pValue, CORDB_ADDRESS *pAddress)
    {
        HRESULT Status;
        ToRelease<ICorDebugReferenceValue> pRef;
        IfFailRet(pValue->QueryInterface(IID_ICorDebugReferenceValue, (LPVOID*) &pRef));
        BOOL isNull = TRUE;
        IfFailRet(pRef->IsNull(&isNull));
        if (isNull)
            return S_FALSE;
        return pRef->GetValue(pAddress);
    }

    HRESULT ReadUInt32(ICorDebugValue *pValue, uint32_t &result)
    {
        HRESULT Status;
        ToRelease<ICorDebugValue> pPlain;
        IfFailRet(DereferenceAndUnboxValue(pValue, &pPlain, nullptr));
        ULONG32 size = 0;
        IfFailRet(pPlain->GetSize(&size));
        if (size != sizeof(uint32_t))
            return E_FAIL;
        ToRelease<ICorDebugGenericValue> pGeneric;
        IfFailRet(pPlain->QueryInterface(IID_ICorDebugGenericValue, (LPVOID*) &pGeneric));
        return pGeneric->GetValue(&result);
    }

    // Contract 1: the ring length is a power of two between 1 and 64. Readers take it from the array.
    bool IsContractRingLength(ULONG32 length)
    {
        return length >= 1 && length <= 64 && (length & (length - 1)) == 0;
    }

    // A string is shown verbatim (truncated), anything else as `{TypeName}`. Never formats by running code: no
    // ToString(), no [DebuggerDisplay], no type proxy.
    HRESULT RenderValue(ICorDebugValue *pValue, std::string &text)
    {
        HRESULT Status;
        ToRelease<ICorDebugValue> pPlain;
        BOOL isNull = FALSE;
        IfFailRet(DereferenceAndUnboxValue(pValue, &pPlain, &isNull));
        if (isNull)
            return E_FAIL;

        ToRelease<ICorDebugStringValue> pString;
        if (SUCCEEDED(pPlain->QueryInterface(IID_ICorDebugStringValue, (LPVOID*) &pString)))
        {
            ULONG32 length = 0;
            IfFailRet(pString->GetLength(&length));
            const ULONG32 shown = std::min(length, MaxAnnotationLength);
            std::vector<WCHAR> buffer(shown + 1, 0);
            ULONG32 returned = 0;
            IfFailRet(pString->GetString(shown + 1, &returned, buffer.data()));
            buffer[shown] = 0;
            text = to_utf8(buffer.data());
            if (shown < length)
                text += "...";
            return S_OK;
        }

        std::string typeName;
        IfFailRet(TypePrinter::GetTypeOfValue(pValue, typeName));
        text = "{" + typeName + "}";
        return S_OK;
    }

    // Any managed frame of the thread: the runtime uses the frame of a thread-static read only to identify the thread.
    HRESULT GetAnyManagedFrame(ICorDebugThread *pThread, ICorDebugFrame **ppFrame)
    {
        ToRelease<ICorDebugFrame> pActiveFrame;
        ToRelease<ICorDebugILFrame> pILFrame;
        if (SUCCEEDED(pThread->GetActiveFrame(&pActiveFrame)) && pActiveFrame != nullptr &&
            SUCCEEDED(pActiveFrame->QueryInterface(IID_ICorDebugILFrame, (LPVOID*) &pILFrame)))
        {
            *ppFrame = pActiveFrame.Detach();
            return S_OK;
        }

        // No active managed frame (for example, a thread blocked in a native wait): walk the stack for one.
        WalkFrames(pThread, [&](FrameType frameType, std::uintptr_t, ICorDebugFrame *pFrame, NativeFrame *)
        {
            if (frameType != FrameCLRManaged || pFrame == nullptr)
                return S_OK;
            pFrame->AddRef();
            *ppFrame = pFrame;
            return E_ABORT; // Fast exit from cycle.
        });
        LOGI("Annotations: thread %u has no active managed frame; stack walk %s", (unsigned)GetThreadOSId(pThread),
             *ppFrame != nullptr ? "found one" : "found none");

        return *ppFrame != nullptr ? S_OK : E_FAIL;
    }

} // unnamed namespace

void Annotations::SetOptions(bool enable, const std::vector<std::string> &extraStoreNames, unsigned threadLimit)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_enabled = enable;
    m_extraStoreNames = extraStoreNames;
    m_threadLimit = threadLimit;
    for (auto &module : m_modules)
    {
        module.second.scanned = false;
        module.second.stores.clear();
    }
    LOGI("Annotations: %s, %zu extra store name(s), thread limit %u", enable ? "enabled" : "disabled",
         extraStoreNames.size(), threadLimit);
}

void Annotations::ManagedCallbackLoadModule(ICorDebugModule *pModule, bool isCoreLib)
{
    CORDB_ADDRESS baseAddress = 0;
    if (FAILED(pModule->GetBaseAddress(&baseAddress)))
        return;

    std::lock_guard<std::mutex> lock(m_mutex);
    ModuleStores &module = m_modules[baseAddress];
    module = ModuleStores();
    module.ordinal = m_nextOrdinal++;
    module.isCoreLib = isCoreLib;
}

void Annotations::ManagedCallbackUnloadModule(ICorDebugModule *pModule)
{
    CORDB_ADDRESS baseAddress = 0;
    if (FAILED(pModule->GetBaseAddress(&baseAddress)))
        return;

    std::lock_guard<std::mutex> lock(m_mutex);
    m_modules.erase(baseAddress);
}

void Annotations::ClearStopCache()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_stopCache.clear();
}

void Annotations::Cleanup()
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_stopCache.clear();
    m_modules.clear();
    m_nextOrdinal = 1;
}

// Caller must hold m_mutex.
void Annotations::ScanModule(ICorDebugModule *pModule, std::vector<Store> &stores)
{
    ToRelease<IMetaDataImport> pMD;
    if (FAILED(GetMetaDataImport(pModule, &pMD)))
        return;

    std::vector<std::string> names{DefaultStoreName};
    names.insert(names.end(), m_extraStoreNames.begin(), m_extraStoreNames.end());

    for (const auto &name : names)
    {
        mdTypeDef holderToken = FindTypeByName(pMD, name);
        if (holderToken == mdTypeDefNil)
            continue;

        Store store;
        store.name = name;
        std::string whyNot;
        if (FAILED(ValidateStore(pMD, holderToken, store, whyNot)))
        {
            LOGI("Annotations: type %s (token 0x%08x) is not read: %s", name.c_str(), holderToken, whyNot.c_str());
            continue;
        }
        stores.push_back(store);
        LOGI("Annotations: store %s found (token 0x%08x)", name.c_str(), holderToken);
    }
}

// Caller must hold m_mutex.
// Every store of every loaded module: the one in System.Private.CoreLib first, then in the order the modules were
// observed. The module map itself is unordered.
HRESULT Annotations::GetStores(std::vector<StoreRef> &stores)
{
    HRESULT Status;
    std::vector<ToRelease<ICorDebugModule>> modules;
    IfFailRet(m_sharedModules->ForEachModule([&](ICorDebugModule *pModule)
    {
        pModule->AddRef();
        modules.emplace_back(pModule);
        return S_OK;
    }));

    for (auto &pModule : modules)
    {
        CORDB_ADDRESS baseAddress = 0;
        if (FAILED(pModule->GetBaseAddress(&baseAddress)))
            continue;

        ModuleStores &module = m_modules[baseAddress];
        if (!module.scanned)
        {
            ScanModule(pModule, module.stores);
            module.scanned = true;
        }

        for (const auto &store : module.stores)
        {
            stores.emplace_back();
            StoreRef &ref = stores.back();
            pModule->AddRef();
            ref.pModule = pModule.GetPtr();
            ref.moduleAddress = baseAddress;
            ref.order = module.isCoreLib ? 0 : module.ordinal;
            ref.store = store;
        }
    }

    std::stable_sort(stores.begin(), stores.end(), [](const StoreRef &a, const StoreRef &b) { return a.order < b.order; });
    return S_OK;
}

// Reads one store's ring on one thread. S_OK and the text of the newest entry whose exception is the object at
// exceptionAddress; S_FALSE if there is none. Fails on any ICorDebug error, including CORDBG_E_STATIC_VAR_NOT_AVAILABLE
// (no storage for the field on this thread).
HRESULT Annotations::FindMatchOnThread(StoreRef &ref, ICorDebugThread *pThread, CORDB_ADDRESS exceptionAddress,
                                       bool stoppedThread, std::string &text)
{
    HRESULT Status;
    const Store &store = ref.store;

    ToRelease<ICorDebugFrame> pFrame;
    if (FAILED(GetAnyManagedFrame(pThread, &pFrame)))
        return S_FALSE; // no managed frame: no thread-static can be read for this thread

    ToRelease<ICorDebugClass> pHolderClass;
    IfFailRet(ref.pModule->GetClassFromToken(store.holderToken, &pHolderClass));

    ++m_ringReads;
    LOGI("Annotations: ring read %llu: store %s, %s thread %u", (unsigned long long)m_ringReads, store.name.c_str(),
         stoppedThread ? "stopped" : "other", (unsigned)GetThreadOSId(pThread));

    // The DAC looks thread-static storage up without creating it, so this read never allocates in the debuggee.
    ToRelease<ICorDebugValue> pRingValue;
    Status = pHolderClass->GetStaticFieldValue(store.ringField, pFrame, &pRingValue);
    if (FAILED(Status))
        return Status;

    ToRelease<ICorDebugValue> pRing;
    BOOL isNull = FALSE;
    IfFailRet(DereferenceAndUnboxValue(pRingValue, &pRing, &isNull));
    if (isNull)
        return S_FALSE;

    ToRelease<ICorDebugArrayValue> pArray;
    IfFailRet(pRing->QueryInterface(IID_ICorDebugArrayValue, (LPVOID*) &pArray));
    ULONG32 length = 0;
    IfFailRet(pArray->GetCount(&length));
    if (!IsContractRingLength(length))
    {
        LOGI("Annotations: store %s ignored: ring length %u is not a power of two in 1..64", store.name.c_str(), length);
        return S_FALSE;
    }

    ToRelease<ICorDebugValue> pCountValue;
    IfFailRet(pHolderClass->GetStaticFieldValue(store.countField, pFrame, &pCountValue));
    uint32_t count = 0; // wraps from 0xFFFFFFFF to 0; read as unsigned
    IfFailRet(ReadUInt32(pCountValue, count));

    ToRelease<ICorDebugClass> pEntryClass;
    IfFailRet(ref.pModule->GetClassFromToken(store.entryToken, &pEntryClass));

    // Visit every slot, newest first. The count only orders the slots; it never says which hold entries (at the wrap,
    // or on a thread stopped between the slot store and the count store, it can't).
    for (ULONG32 k = 0; k < length; k++)
    {
        const ULONG32 slot = (count - 1u - k) % length;

        ToRelease<ICorDebugValue> pElementValue;
        IfFailRet(pArray->GetElementAtPosition(slot, &pElementValue));
        ToRelease<ICorDebugValue> pElement;
        isNull = FALSE;
        IfFailRet(DereferenceAndUnboxValue(pElementValue, &pElement, &isNull));
        if (isNull)
            continue;

        // Defensive: with a validated Entry?[] ring nothing else can be in a slot.
        ToRelease<ICorDebugObjectValue> pEntry;
        IfFailRet(pElement->QueryInterface(IID_ICorDebugObjectValue, (LPVOID*) &pEntry));
        ToRelease<ICorDebugClass> pElementClass;
        IfFailRet(pEntry->GetClass(&pElementClass));
        mdTypeDef elementToken = mdTypeDefNil;
        IfFailRet(pElementClass->GetToken(&elementToken));
        ToRelease<ICorDebugModule> pElementModule;
        IfFailRet(pElementClass->GetModule(&pElementModule));
        CORDB_ADDRESS elementModuleAddress = 0;
        IfFailRet(pElementModule->GetBaseAddress(&elementModuleAddress));
        if (elementToken != store.entryToken || elementModuleAddress != ref.moduleAddress)
            continue;

        ToRelease<ICorDebugValue> pEntryException;
        IfFailRet(pEntry->GetFieldValue(pEntryClass, store.exceptionField, &pEntryException));
        CORDB_ADDRESS entryExceptionAddress = 0;
        IfFailRet(Status = GetObjectAddress(pEntryException, &entryExceptionAddress));
        if (Status == S_FALSE)
            continue; // an annotation not tied to an exception is never attributed to an exception stop

        // Attribution is by object identity only. Never "the newest annotation since the last stop": catch A, then
        // stop at an unrelated B, and that would show A's annotation at B.
        if (entryExceptionAddress != exceptionAddress)
            continue;

        ToRelease<ICorDebugValue> pEntryValue;
        IfFailRet(pEntry->GetFieldValue(pEntryClass, store.valueField, &pEntryValue));
        CORDB_ADDRESS valueAddress = 0;
        IfFailRet(Status = GetObjectAddress(pEntryValue, &valueAddress));
        if (Status == S_FALSE)
            continue; // defensive: no conforming producer stores a null value

        IfFailRet(RenderValue(pEntryValue, text));
        return S_OK;
    }

    return S_FALSE;
}

// FindMatchOnThread that never fails: an ICorDebug error while reading a store skips that store for this thread,
// and CORDBG_E_STATIC_VAR_NOT_AVAILABLE means the thread has no storage for the ring, which for a holder without a
// static constructor means it never recorded. Neither may fail or delay the exception report.
HRESULT Annotations::ReadStoreOnThread(StoreRef &ref, ICorDebugThread *pThread, CORDB_ADDRESS exceptionAddress,
                                       bool stoppedThread, std::string &text)
{
    HRESULT Status = FindMatchOnThread(ref, pThread, exceptionAddress, stoppedThread, text);
    if (SUCCEEDED(Status))
        return Status;
    if (Status == CORDBG_E_STATIC_VAR_NOT_AVAILABLE)
        LOGI("Annotations: store %s: thread %u never recorded (no storage)", ref.store.name.c_str(),
             (unsigned)GetThreadOSId(pThread));
    else
        LOGI("Annotations: store %s skipped on thread %u: %s", ref.store.name.c_str(), (unsigned)GetThreadOSId(pThread),
             errormessage(Status));
    return S_FALSE;
}

// An exception rethrown on another thread (`await`, ExceptionDispatchInfo.Throw) is the same object, so its annotation
// can be in the ring of the thread that recorded it. Best effort: that thread must still be alive, have a managed frame
// and not have evicted the entry. Each match is labelled with the thread it was recorded on.
HRESULT Annotations::SearchOtherThreads(StoreRef &ref, ICorDebugThread *pStoppedThread, CORDB_ADDRESS exceptionAddress,
                                        std::string &suffix)
{
    HRESULT Status;
    const DWORD stoppedThreadId = GetThreadOSId(pStoppedThread);

    ToRelease<ICorDebugProcess> pProcess;
    ToRelease<ICorDebugThreadEnum> pThreadEnum;
    if (FAILED(pStoppedThread->GetProcess(&pProcess)) || FAILED(pProcess->EnumerateThreads(&pThreadEnum)))
        return S_OK; // nothing to search; never fails the report

    unsigned searched = 0;
    ICorDebugThread *pRawThread = nullptr;
    ULONG fetched = 0;
    while (SUCCEEDED(pThreadEnum->Next(1, &pRawThread, &fetched)) && fetched == 1)
    {
        ToRelease<ICorDebugThread> pThread(pRawThread);
        const DWORD threadId = GetThreadOSId(pThread);
        if (threadId == stoppedThreadId)
            continue;
        if (m_threadLimit != 0 && searched >= m_threadLimit)
            break;
        searched++;

        std::string text;
        IfFailRet(ReadStoreOnThread(ref, pThread, exceptionAddress, false, text));
        if (Status == S_OK)
            suffix += "\n Annotation (recorded on thread " + std::to_string(threadId) + "): " + text;
    }

    return S_OK;
}

HRESULT Annotations::AppendExceptionAnnotations(ICorDebugThread *pThread, ExceptionInfo &exceptionInfo)
{
    HRESULT Status;
    std::lock_guard<std::mutex> lock(m_mutex);
    if (!m_enabled)
        return S_OK;

    const DWORD threadId = GetThreadOSId(pThread);
    auto cached = m_stopCache.find(threadId);
    if (cached != m_stopCache.end())
    {
        exceptionInfo.description += cached->second;
        return S_OK;
    }

    // Fetched here, after the exception info's own func-evals, which can neuter values obtained before them.
    ToRelease<ICorDebugValue> pException;
    if (FAILED(pThread->GetCurrentException(&pException)) || pException == nullptr)
        return S_OK;
    CORDB_ADDRESS exceptionAddress = 0;
    if (GetObjectAddress(pException, &exceptionAddress) != S_OK)
        return S_OK;

    std::vector<StoreRef> stores;
    if (FAILED(GetStores(stores)))
        return S_OK;

    std::string suffix;
    for (auto &ref : stores)
    {
        // The stopped thread first. The preference is per store: a match in one store never suppresses another.
        std::string text;
        IfFailRet(ReadStoreOnThread(ref, pThread, exceptionAddress, true, text));
        if (Status == S_OK)
        {
            suffix += "\n Annotation: " + text;
            continue;
        }

        IfFailRet(SearchOtherThreads(ref, pThread, exceptionAddress, suffix));
    }

    LOGI("Annotations: %zu store(s), %zu annotation line(s) for thread %u, %llu ring read(s) so far", stores.size(),
         (size_t)std::count(suffix.begin(), suffix.end(), '\n'), (unsigned)threadId, (unsigned long long)m_ringReads);

    m_stopCache[threadId] = suffix;
    exceptionInfo.description += suffix;
    return S_OK;
}

} // namespace netcoredbg
