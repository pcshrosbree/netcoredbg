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

// Like IfFailRet, but without the error log. In the reader a failed read is an expected outcome (for example a store
// whose class was never loaded, on every thread at every stop): the store is skipped for that thread, quietly.
#define IfFailRetQuiet(EXPR) do { Status = (EXPR); if (FAILED(Status)) { return Status; } } while (0)

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
        IfFailRetQuiet(pModule->GetMetaDataInterface(IID_IMetaDataImport, &pMDUnknown));
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
        HCORENUM hEnum = nullptr;
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
        HCORENUM hEnum = nullptr;
        mdGenericParam genericParam = mdGenericParamNil;
        ULONG count = 0;
        HRESULT Status = pMD2->EnumGenericParams(&hEnum, typeToken, &genericParam, 1, &count);
        pMD2->CloseEnum(hEnum);
        return FAILED(Status) || count != 0;
    }

    bool IsSealed(IMetaDataImport *pMD, mdTypeDef typeToken)
    {
        DWORD typeAttr = 0;
        if (FAILED(pMD->GetTypeDefProps(typeToken, nullptr, 0, nullptr, &typeAttr, nullptr)))
            return false;
        return (typeAttr & tdSealed) != 0;
    }

    // [ThreadStatic] is a custom attribute; it is not part of the field signature.
    bool IsThreadStatic(IMetaDataImport *pMD, mdFieldDef fieldToken)
    {
        const void *pData = nullptr;
        ULONG dataSize = 0;
        return pMD->GetCustomAttributeByName(fieldToken, W("System.ThreadStaticAttribute"), &pData, &dataSize) == S_OK;
    }

    // Finds a field by name; it must be static (or an instance field) as asked, and not a literal.
    HRESULT FindField(IMetaDataImport *pMD, mdTypeDef typeToken, const WCHAR *name, bool isStatic,
                      mdFieldDef &fieldToken, PCCOR_SIGNATURE &sigBlob, ULONG &sigSize)
    {
        HRESULT Status;
        HCORENUM hEnum = nullptr;
        ULONG count = 0;
        fieldToken = mdFieldDefNil;
        Status = pMD->EnumFieldsWithName(&hEnum, typeToken, name, &fieldToken, 1, &count);
        pMD->CloseEnum(hEnum);
        if (FAILED(Status) || count != 1)
            return E_FAIL;

        DWORD fieldAttr = 0;
        IfFailRetQuiet(pMD->GetFieldProps(fieldToken, nullptr, nullptr, 0, nullptr, &fieldAttr, &sigBlob, &sigSize,
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
            // A compressed token takes 1, 2 or 4 bytes (ECMA-335 II.23.2); never read past the blob.
            if (sigBlob >= sigEnd)
                return false;
            const ptrdiff_t tokenSize = (*sigBlob & 0x80) == 0 ? 1 : (*sigBlob & 0xC0) == 0x80 ? 2 : (*sigBlob & 0xE0) == 0xC0 ? 4 : 0;
            if (tokenSize == 0 || sigEnd - sigBlob < tokenSize)
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
        // Entry is sealed, so an entry is exactly the store's own Entry and its fields are the ones validated here.
        if (!IsSealed(pMD, entryToken))
        {
            whyNot = "its Entry type is not sealed";
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

        // The ring is per thread. An ordinary static of the same name and type would be one ring for the whole
        // process, and an entry recorded on one thread would read as recorded on every thread.
        if (!IsThreadStatic(pMD, ringField) || !IsThreadStatic(pMD, countField))
        {
            whyNot = "its fields are not [ThreadStatic]";
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
        IfFailRetQuiet(pValue->QueryInterface(IID_ICorDebugReferenceValue, (LPVOID*) &pRef));
        BOOL isNull = TRUE;
        IfFailRetQuiet(pRef->IsNull(&isNull));
        if (isNull)
            return S_FALSE;
        return pRef->GetValue(pAddress);
    }

    HRESULT ReadUInt32(ICorDebugValue *pValue, uint32_t &result)
    {
        HRESULT Status;
        ToRelease<ICorDebugValue> pPlain;
        IfFailRetQuiet(DereferenceAndUnboxValue(pValue, &pPlain, nullptr));
        ULONG32 size = 0;
        IfFailRetQuiet(pPlain->GetSize(&size));
        if (size != sizeof(uint32_t))
            return E_FAIL;
        ToRelease<ICorDebugGenericValue> pGeneric;
        IfFailRetQuiet(pPlain->QueryInterface(IID_ICorDebugGenericValue, (LPVOID*) &pGeneric));
        return pGeneric->GetValue(&result);
    }

    // Contract 1: the ring length is a power of two between 1 and 64. Readers take it from the array.
    bool IsContractRingLength(ULONG32 length)
    {
        return length >= 1 && length <= 64 && (length & (length - 1)) == 0;
    }

    bool IsHighSurrogate(uint32_t unit) { return unit >= 0xD800 && unit <= 0xDBFF; }
    bool IsLowSurrogate(uint32_t unit) { return unit >= 0xDC00 && unit <= 0xDFFF; }

    // UTF-16 to UTF-8 by explicit length; never throws. An embedded NUL is kept (the description is a JSON string,
    // which carries it as \u0000), and an unpaired surrogate becomes U+FFFD. (to_utf8 stops at the first NUL and
    // throws std::range_error on an unpaired surrogate, and a .NET string may contain both.)
    std::string Utf16ToUtf8(const WCHAR *units, size_t count)
    {
        std::string out;
        out.reserve(count);
        for (size_t i = 0; i < count; i++)
        {
            uint32_t cp = units[i];
            if (IsHighSurrogate(cp) && i + 1 < count && IsLowSurrogate(units[i + 1]))
            {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (uint32_t(units[i + 1]) - 0xDC00);
                i++;
            }
            else if (IsHighSurrogate(cp) || IsLowSurrogate(cp))
            {
                cp = 0xFFFD;
            }

            if (cp < 0x80)
            {
                out += char(cp);
            }
            else if (cp < 0x800)
            {
                out += char(0xC0 | (cp >> 6));
                out += char(0x80 | (cp & 0x3F));
            }
            else if (cp < 0x10000)
            {
                out += char(0xE0 | (cp >> 12));
                out += char(0x80 | ((cp >> 6) & 0x3F));
                out += char(0x80 | (cp & 0x3F));
            }
            else
            {
                out += char(0xF0 | (cp >> 18));
                out += char(0x80 | ((cp >> 12) & 0x3F));
                out += char(0x80 | ((cp >> 6) & 0x3F));
                out += char(0x80 | (cp & 0x3F));
            }
        }
        return out;
    }

    // A string is shown verbatim, truncated after MaxAnnotationLength code units (never inside a surrogate pair);
    // anything else as `{TypeName}`. Never formats by running code: no ToString(), no [DebuggerDisplay], no type proxy.
    HRESULT RenderValue(ICorDebugValue *pValue, std::string &text)
    {
        HRESULT Status;
        ToRelease<ICorDebugValue> pPlain;
        BOOL isNull = FALSE;
        IfFailRetQuiet(DereferenceAndUnboxValue(pValue, &pPlain, &isNull));
        if (isNull)
            return E_FAIL;

        ToRelease<ICorDebugStringValue> pString;
        if (SUCCEEDED(pPlain->QueryInterface(IID_ICorDebugStringValue, (LPVOID*) &pString)))
        {
            ULONG32 length = 0;
            IfFailRetQuiet(pString->GetLength(&length));
            // One unit past the limit, to see a surrogate pair that straddles it.
            const ULONG32 wanted = std::min(length, MaxAnnotationLength + 1);
            std::vector<WCHAR> buffer(wanted + 1, 0);
            ULONG32 fullLength = 0;
            IfFailRetQuiet(pString->GetString(wanted + 1, &fullLength, buffer.data()));
            ULONG32 shown = std::min(length, MaxAnnotationLength);
            if (shown < length && shown > 0 && IsHighSurrogate(buffer[shown - 1]) && IsLowSurrogate(buffer[shown]))
                shown--;
            text = Utf16ToUtf8(buffer.data(), shown);
            if (shown < length)
                text += "...";
            return S_OK;
        }

        std::string typeName;
        IfFailRetQuiet(TypePrinter::GetTypeOfValue(pValue, typeName));
        text = "{" + typeName + "}";
        return S_OK;
    }

    // Any managed frame of the thread: the runtime uses the frame of a thread-static read only to identify the thread.
    HRESULT GetAnyManagedFrame(ICorDebugThread *pThread, ICorDebugFrame **ppFrame)
    {
        *ppFrame = nullptr;
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

        return *ppFrame != nullptr ? S_OK : E_FAIL;
    }

    const char *ReadOutcome(HRESULT status)
    {
        if (status == S_OK)
            return "match";
        if (status == S_FALSE)
            return "no match";
        if (status == CORDBG_E_STATIC_VAR_NOT_AVAILABLE)
            return "no storage (never recorded)";
        if (status == CORDBG_E_CLASS_NOT_LOADED)
            return "class not loaded";
        return "skipped";
    }

} // unnamed namespace

void Annotations::SetOptions(bool enable, const std::vector<std::string> &extraStoreNames, unsigned threadLimit)
{
    std::lock_guard<std::mutex> lock(m_mutex);
    m_enabled = enable;
    m_extraStoreNames.clear();
    for (const auto &name : extraStoreNames)
    {
        if (name != DefaultStoreName &&
            std::find(m_extraStoreNames.begin(), m_extraStoreNames.end(), name) == m_extraStoreNames.end())
            m_extraStoreNames.push_back(name);
    }
    m_threadLimit = threadLimit;
    for (auto &module : m_modules)
    {
        module.second.scanned = false;
        module.second.stores.clear();
    }
    LOGI("Annotations: %s, %zu extra store name(s), thread limit %u", enable ? "enabled" : "disabled",
         m_extraStoreNames.size(), threadLimit);
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

// Caller must hold m_mutex. Returns false if the module's metadata could not be read (scan again at the next stop).
bool Annotations::ScanModule(ICorDebugModule *pModule, std::vector<Store> &stores)
{
    ToRelease<IMetaDataImport> pMD;
    if (FAILED(GetMetaDataImport(pModule, &pMD)))
        return false;

    std::vector<std::string> names{DefaultStoreName};
    names.insert(names.end(), m_extraStoreNames.begin(), m_extraStoreNames.end());

    for (const auto &name : names)
    {
        mdTypeDef holderToken = FindTypeByName(pMD, name);
        if (holderToken == mdTypeDefNil)
            continue;
        if (std::any_of(stores.begin(), stores.end(), [&](const Store &store) { return store.holderToken == holderToken; }))
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
    return true;
}

// Caller must hold m_mutex.
// Every store of every loaded module: the one in System.Private.CoreLib first, then in the order the modules were
// observed. The module map itself is unordered.
HRESULT Annotations::GetStores(std::vector<StoreRef> &stores)
{
    HRESULT Status;
    std::vector<ToRelease<ICorDebugModule>> modules;
    IfFailRetQuiet(m_sharedModules->ForEachModule([&](ICorDebugModule *pModule)
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
            module.stores.clear();
            module.scanned = ScanModule(pModule, module.stores);
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

// Reads one store's ring on the thread that pFrame belongs to. S_OK and the text of the newest entry whose exception is
// the object at exceptionAddress; S_FALSE if there is none. Fails on any ICorDebug error, including
// CORDBG_E_STATIC_VAR_NOT_AVAILABLE (no storage for the field on this thread).
HRESULT Annotations::FindMatchOnThread(StoreRef &ref, ICorDebugFrame *pFrame, CORDB_ADDRESS exceptionAddress,
                                       bool stoppedThread, std::string &text)
{
    HRESULT Status;
    const Store &store = ref.store;

    ToRelease<ICorDebugClass> pHolderClass;
    IfFailRetQuiet(ref.pModule->GetClassFromToken(store.holderToken, &pHolderClass));

    // The DAC looks thread-static storage up without creating it, so this read never allocates in the debuggee.
    ToRelease<ICorDebugValue> pRingValue;
    IfFailRetQuiet(pHolderClass->GetStaticFieldValue(store.ringField, pFrame, &pRingValue));

    ToRelease<ICorDebugValue> pRing;
    BOOL isNull = FALSE;
    IfFailRetQuiet(DereferenceAndUnboxValue(pRingValue, &pRing, &isNull));
    if (isNull)
        return S_FALSE;

    ToRelease<ICorDebugArrayValue> pArray;
    IfFailRetQuiet(pRing->QueryInterface(IID_ICorDebugArrayValue, (LPVOID*) &pArray));
    ULONG32 length = 0;
    IfFailRetQuiet(pArray->GetCount(&length));
    if (!IsContractRingLength(length))
        return S_FALSE; // not contract 1: the store is ignored

    ToRelease<ICorDebugValue> pCountValue;
    IfFailRetQuiet(pHolderClass->GetStaticFieldValue(store.countField, pFrame, &pCountValue));
    uint32_t count = 0; // wraps from 0xFFFFFFFF to 0; read as unsigned
    IfFailRetQuiet(ReadUInt32(pCountValue, count));

    ToRelease<ICorDebugClass> pEntryClass;
    IfFailRetQuiet(ref.pModule->GetClassFromToken(store.entryToken, &pEntryClass));

    // Visit every slot, newest first. The count only orders the slots; it never says which hold entries (at the wrap,
    // or on a thread stopped between the slot store and the count store, it can't).
    for (ULONG32 k = 0; k < length; k++)
    {
        const ULONG32 slot = (count - 1u - k) % length;

        ToRelease<ICorDebugValue> pElementValue;
        IfFailRetQuiet(pArray->GetElementAtPosition(slot, &pElementValue));
        ToRelease<ICorDebugValue> pElement;
        isNull = FALSE;
        IfFailRetQuiet(DereferenceAndUnboxValue(pElementValue, &pElement, &isNull));
        if (isNull)
            continue;

        // Defensive: with a validated Entry?[] ring and a sealed Entry nothing else can be in a slot.
        ToRelease<ICorDebugObjectValue> pEntry;
        IfFailRetQuiet(pElement->QueryInterface(IID_ICorDebugObjectValue, (LPVOID*) &pEntry));
        ToRelease<ICorDebugClass> pElementClass;
        IfFailRetQuiet(pEntry->GetClass(&pElementClass));
        mdTypeDef elementToken = mdTypeDefNil;
        IfFailRetQuiet(pElementClass->GetToken(&elementToken));
        ToRelease<ICorDebugModule> pElementModule;
        IfFailRetQuiet(pElementClass->GetModule(&pElementModule));
        CORDB_ADDRESS elementModuleAddress = 0;
        IfFailRetQuiet(pElementModule->GetBaseAddress(&elementModuleAddress));
        if (elementToken != store.entryToken || elementModuleAddress != ref.moduleAddress)
            continue;

        ToRelease<ICorDebugValue> pEntryException;
        IfFailRetQuiet(pEntry->GetFieldValue(pEntryClass, store.exceptionField, &pEntryException));
        CORDB_ADDRESS entryExceptionAddress = 0;
        IfFailRetQuiet(GetObjectAddress(pEntryException, &entryExceptionAddress));
        if (Status == S_FALSE)
            continue; // an annotation not tied to an exception is never attributed to an exception stop

        // Attribution is by object identity only. Never "the newest annotation since the last stop": catch A, then
        // stop at an unrelated B, and that would show A's annotation at B.
        if (entryExceptionAddress != exceptionAddress)
            continue;

        ToRelease<ICorDebugValue> pEntryValue;
        IfFailRetQuiet(pEntry->GetFieldValue(pEntryClass, store.valueField, &pEntryValue));
        CORDB_ADDRESS valueAddress = 0;
        IfFailRetQuiet(GetObjectAddress(pEntryValue, &valueAddress));
        if (Status == S_FALSE)
            continue; // defensive: no conforming producer stores a null value

        IfFailRetQuiet(RenderValue(pEntryValue, text));
        return S_OK;
    }

    return S_FALSE;
}

// FindMatchOnThread that never fails: an ICorDebug error while reading a store skips that store for this thread,
// and CORDBG_E_STATIC_VAR_NOT_AVAILABLE means the thread has no storage for the ring, which for a holder without a
// static constructor means it never recorded. Neither may fail or delay the exception report. Every call is one ring
// read, and is logged as one (the log is how a test sees when the reader reads).
HRESULT Annotations::ReadStoreOnThread(StoreRef &ref, ICorDebugFrame *pFrame, DWORD threadId,
                                       CORDB_ADDRESS exceptionAddress, bool stoppedThread, std::string &text,
                                       HRESULT *pReadStatus)
{
    HRESULT Status = FindMatchOnThread(ref, pFrame, exceptionAddress, stoppedThread, text);
    if (pReadStatus != nullptr)
        *pReadStatus = Status;

    ++m_ringReads;
    LOGI("Annotations: ring read %llu: store %s, %s thread %u: %s", (unsigned long long)m_ringReads,
         ref.store.name.c_str(), stoppedThread ? "stopped" : "other", (unsigned)threadId, ReadOutcome(Status));

    if (SUCCEEDED(Status))
        return Status;
    return S_FALSE;
}

// Caller must hold m_mutex. The managed threads other than the stopped one, each with any managed frame of it;
// threads without one are left out (no thread-static of theirs can be read).
void Annotations::GetOtherThreads(ICorDebugThread *pStoppedThread, std::vector<OtherThread> &threads)
{
    const DWORD stoppedThreadId = GetThreadOSId(pStoppedThread);
    ToRelease<ICorDebugProcess> pProcess;
    ToRelease<ICorDebugThreadEnum> pThreadEnum;
    if (FAILED(pStoppedThread->GetProcess(&pProcess)) || FAILED(pProcess->EnumerateThreads(&pThreadEnum)))
        return;

    ICorDebugThread *pRawThread = nullptr;
    ULONG fetched = 0;
    while (SUCCEEDED(pThreadEnum->Next(1, &pRawThread, &fetched)) && fetched == 1)
    {
        ToRelease<ICorDebugThread> pThread(pRawThread);
        const DWORD threadId = GetThreadOSId(pThread);
        if (threadId == stoppedThreadId)
            continue;
        OtherThread other;
        if (FAILED(GetAnyManagedFrame(pThread, &other.pFrame)))
            continue;
        other.threadId = threadId;
        threads.push_back(std::move(other));
    }
}

// An exception rethrown on another thread (`await`, ExceptionDispatchInfo.Throw) is the same object, so its annotation
// can be in the ring of the thread that recorded it. Best effort: that thread must still be alive, have a managed frame
// and not have evicted the entry. Each match is labelled with the thread it was recorded on.
HRESULT Annotations::SearchOtherThreads(StoreRef &ref, std::vector<OtherThread> &threads, CORDB_ADDRESS exceptionAddress,
                                        std::string &suffix)
{
    HRESULT Status;
    unsigned searched = 0;
    for (auto &other : threads)
    {
        if (m_threadLimit != 0 && searched >= m_threadLimit)
            break;
        searched++;

        std::string text;
        IfFailRetQuiet(ReadStoreOnThread(ref, other.pFrame, other.threadId, exceptionAddress, false, text));
        if (Status == S_OK)
            suffix += "\n Annotation (recorded on thread " + std::to_string(other.threadId) + "): " + text;
    }

    return S_OK;
}

// Caller must hold m_mutex.
HRESULT Annotations::AppendExceptionAnnotationsImpl(ICorDebugThread *pThread, ExceptionInfo &exceptionInfo)
{
    HRESULT Status;
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

    ToRelease<ICorDebugFrame> pFrame;
    const bool stoppedThreadHasFrame = SUCCEEDED(GetAnyManagedFrame(pThread, &pFrame));
    std::vector<OtherThread> otherThreads;
    bool otherThreadsListed = false;

    std::string suffix;
    for (auto &ref : stores)
    {
        // The stopped thread first. The preference is per store: a match in one store never suppresses another.
        HRESULT readStatus = S_FALSE;
        if (stoppedThreadHasFrame)
        {
            std::string text;
            IfFailRetQuiet(ReadStoreOnThread(ref, pFrame, threadId, exceptionAddress, true, text, &readStatus));
            if (Status == S_OK)
            {
                suffix += "\n Annotation: " + text;
                continue;
            }
        }

        // A holder class the runtime never loaded has no storage on any thread: nothing to search.
        if (readStatus == CORDBG_E_CLASS_NOT_LOADED)
            continue;

        if (!otherThreadsListed)
        {
            GetOtherThreads(pThread, otherThreads);
            otherThreadsListed = true;
        }
        IfFailRetQuiet(SearchOtherThreads(ref, otherThreads, exceptionAddress, suffix));
    }

    m_stopCache[threadId] = suffix;
    exceptionInfo.description += suffix;
    return S_OK;
}

HRESULT Annotations::AppendExceptionAnnotations(ICorDebugThread *pThread, ExceptionInfo &exceptionInfo)
{
    // Nothing here may fail or delay the exception report: on any C++ exception, the description is left as it was.
    const std::string description = exceptionInfo.description;
    try
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        return AppendExceptionAnnotationsImpl(pThread, exceptionInfo);
    }
    catch (const std::exception &e)
    {
        LOGE("Annotations: reader failed: %s", e.what());
    }
    catch (...)
    {
        LOGE("Annotations: reader failed");
    }
    exceptionInfo.description = description;
    return S_OK;
}

} // namespace netcoredbg
