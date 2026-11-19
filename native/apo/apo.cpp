// RedTuneApo.dll: Red Tune's sound effect for Windows (an "audio processing object", the same kind of plug-in
// Nahimic, Realtek and Dolby use). Windows loads it into its sound process (audiodg.exe) for every output device the
// page «Звук» switched it on for, and passes the mixed sound of that device through APOProcess below.
// The processing itself is rtdsp.h; the settings come from the shared file (shared.h).
//
// Rules of this file: APOProcess runs on the sound thread, so it never allocates, locks, waits or touches files.
// Everything it reads from the shared file is checked before use (rtd_sanitize): a bad file must not break the sound.
#include <windows.h>
#include <shlobj.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>
#include <xmmintrin.h>
#include <new>
#include "rtdsp.h"

#define RTA_VERSION 1u

// {E4B6C2A1-7F3D-4C58-9B1E-5A2D8C6F0E37}
static const GUID CLSID_RedTuneApo = { 0xe4b6c2a1, 0x7f3d, 0x4c58, { 0x9b, 0x1e, 0x5a, 0x2d, 0x8c, 0x6f, 0x0e, 0x37 } };
static const wchar_t* const kClsid = L"{E4B6C2A1-7F3D-4C58-9B1E-5A2D8C6F0E37}";

static HMODULE g_module;
static volatile long g_objects;
// counted here because an object has no shared file yet when it is made; written into the file by Note()
static volatile long g_created, g_inits, g_locks;

// the test bench (apotest.exe) points the effect at its own file and device; audiodg never calls this
static wchar_t g_testFile[MAX_PATH];
static GUID g_testEndpoint; static bool g_haveTestEndpoint;
extern "C" __declspec(dllexport) void __stdcall RtaTestHook(const wchar_t* file, const GUID* endpoint)
{
    if (file) lstrcpynW(g_testFile, file, MAX_PATH); else g_testFile[0] = 0;
    g_haveTestEndpoint = endpoint != NULL; if (endpoint) g_testEndpoint = *endpoint;
}

// A journal of what Windows asks of the effect: %ProgramData%\RedTune\apo.log. The program makes the file (with the
// right for the sound process to write into it) and shows it in --audio-status; without the file nothing is written.
// Never called from the sound thread. Stops at 256 KB (the program empties the file when it registers a new build).
static void Trace(const wchar_t* fmt, ...)
{
    if (g_testFile[0]) return;
    wchar_t path[MAX_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_COMMON_APPDATA, NULL, SHGFP_TYPE_CURRENT, path)) || lstrlenW(path) + 24 >= MAX_PATH) return;
    lstrcatW(path, L"\\RedTune\\apo.log");
    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    LARGE_INTEGER size, zero; zero.QuadPart = 0;
    if (GetFileSizeEx(h, &size))
    {
        // full: it starts again from empty (the last calls are the ones that matter)
        if (size.QuadPart >= 256 * 1024) { SetFilePointerEx(h, zero, NULL, FILE_BEGIN); SetEndOfFile(h); }
        else SetFilePointerEx(h, zero, NULL, FILE_END);
        wchar_t line[1200]; char utf[3600]; SYSTEMTIME t; GetLocalTime(&t);
        int n = wsprintfW(line, L"%02d:%02d:%02d.%03d pid %u  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
        va_list a; va_start(a, fmt); n += wvsprintfW(line + n, fmt, a); va_end(a); // wvsprintf writes at most 1024 characters
        line[n++] = L'\r'; line[n++] = L'\n';
        int b = WideCharToMultiByte(CP_UTF8, 0, line, n, utf, sizeof(utf), NULL, NULL);
        DWORD done; if (b > 0) WriteFile(h, utf, (DWORD)b, &done, NULL);
    }
    CloseHandle(h);
}

struct GuidText { wchar_t s[40]; GuidText(REFGUID g) { if (!StringFromGUID2(g, s, 40)) s[0] = 0; } };

// rate, channels, bits and kind of a format Windows offers
static void FormatText(IAudioMediaType* m, wchar_t* out)
{
    const WAVEFORMATEX* w = m ? m->GetAudioFormat() : NULL;
    if (!w) { lstrcpyW(out, L"none"); return; }
    bool flt = w->wFormatTag == WAVE_FORMAT_IEEE_FLOAT || (w->wFormatTag == WAVE_FORMAT_EXTENSIBLE && w->cbSize >= 22 && ((const WAVEFORMATEXTENSIBLE*)w)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    wsprintfW(out, L"%u Hz %u ch %u bit %s", w->nSamplesPerSec, w->nChannels, w->wBitsPerSample, flt ? L"float" : L"integer");
}

static const CRegAPOProperties<1> g_reg(CLSID_RedTuneApo, L"Red Tune Audio Effect", L"Red Tune, GPL-3.0", 1, 0,
    __uuidof(IAudioProcessingObject),
    (APO_FLAG)(APO_FLAG_SAMPLESPERFRAME_MUST_MATCH | APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH | APO_FLAG_INPLACE));

struct INonDelegatingUnknown
{
    virtual HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID iid, void** out) = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingAddRef() = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingRelease() = 0;
};

class RedTuneApo : public CBaseAudioProcessingObject, public IAudioSystemEffects2, public INonDelegatingUnknown
{
    volatile long refs;
    IUnknown* outer;                // set when another object contains this one (COM aggregation)
    GUID endpoint; bool haveEndpoint;
    HANDLE file, mapping; RtaShared* shared; bool canWrite;
    RtaSlot* slot; long lastSeq; uint32_t sinceLook;
    RtDsp* dsp;                     // made in LockForProcess, off the sound thread
    uint32_t tail;                  // frames of real sound still inside the effect after the input went silent
    uint32_t ringPos;
    uint32_t misses;                // interfaces asked for and not there (for the journal)

public:
    RedTuneApo(IUnknown* unkOuter) : CBaseAudioProcessingObject(g_reg), refs(1), outer(unkOuter), haveEndpoint(false),
        file(INVALID_HANDLE_VALUE), mapping(NULL), shared(NULL), canWrite(false), slot(NULL), lastSeq(-1), sinceLook(0), dsp(NULL), tail(0), ringPos(0), misses(0)
    {
        memset(&endpoint, 0, sizeof(endpoint));
        InterlockedIncrement(&g_objects);
        InterlockedIncrement(&g_created);
    }

    virtual ~RedTuneApo()
    {
        Trace(L"object %p released", this);
        Unmap();
        if (dsp) { VirtualFree(dsp, 0, MEM_RELEASE); dsp = NULL; }
        InterlockedDecrement(&g_objects);
    }

    // IUnknown: to the containing object when there is one
    STDMETHODIMP QueryInterface(REFIID iid, void** out) { return outer ? outer->QueryInterface(iid, out) : NonDelegatingQueryInterface(iid, out); }
    STDMETHODIMP_(ULONG) AddRef() { return outer ? outer->AddRef() : NonDelegatingAddRef(); }
    STDMETHODIMP_(ULONG) Release() { return outer ? outer->Release() : NonDelegatingRelease(); }

    STDMETHODIMP NonDelegatingQueryInterface(REFIID iid, void** out)
    {
        if (!out) return E_POINTER;
        if (iid == __uuidof(IUnknown)) *out = static_cast<INonDelegatingUnknown*>(this);
        else if (iid == __uuidof(IAudioProcessingObject)) *out = static_cast<IAudioProcessingObject*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectRT)) *out = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) *out = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == __uuidof(IAudioSystemEffects)) *out = static_cast<IAudioSystemEffects*>(this);
        else if (iid == __uuidof(IAudioSystemEffects2)) *out = static_cast<IAudioSystemEffects2*>(this);
        else
        {
            // Windows asks every object for some forty optional interfaces: the first few are enough for the journal
            *out = NULL; if (++misses <= 3) Trace(L"object %p: asked for an interface it does not have, %s", this, GuidText(iid).s);
            return E_NOINTERFACE;
        }
        reinterpret_cast<IUnknown*>(*out)->AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) NonDelegatingAddRef() { return InterlockedIncrement(&refs); }
    STDMETHODIMP_(ULONG) NonDelegatingRelease() { long n = InterlockedDecrement(&refs); if (n == 0) delete this; return n; }

    // IAudioSystemEffects2: no named effects to list for the Windows settings page (the program has its own page)
    STDMETHODIMP GetEffectsList(LPGUID* ids, UINT* count, HANDLE)
    {
        if (!ids || !count) return E_POINTER;
        Trace(L"object %p: GetEffectsList", this);
        *ids = NULL; *count = 0; return S_OK;
    }

    STDMETHODIMP GetRegistrationProperties(APO_REG_PROPERTIES** props)
    {
        HRESULT hr = CBaseAudioProcessingObject::GetRegistrationProperties(props);
        Trace(L"object %p: GetRegistrationProperties 0x%08X", this, hr);
        return hr;
    }

    STDMETHODIMP UnlockForProcess()
    {
        HRESULT hr = CBaseAudioProcessingObject::UnlockForProcess();
        Trace(L"object %p: UnlockForProcess 0x%08X", this, hr);
        return hr;
    }

    // which device this copy serves: the last item of the collection Windows passes is the endpoint itself
    STDMETHODIMP Initialize(UINT32 cbDataSize, BYTE* pbyData)
    {
        if (!pbyData && cbDataSize) return E_INVALIDARG;
        if (pbyData && !cbDataSize) return E_POINTER;
        InterlockedIncrement(&g_inits);
        uint32_t info = 0;
        HRESULT hr = CBaseAudioProcessingObject::Initialize(cbDataSize, pbyData);
        Trace(L"object %p: Initialize, data %u bytes (system effects: %u, system effects 2: %u), the base class 0x%08X", this, cbDataSize, (UINT)sizeof(APOInitSystemEffects), (UINT)sizeof(APOInitSystemEffects2), hr);
        if (FAILED(hr)) { Map(); Note(cbDataSize, 0x400, true); return hr; }

        IMMDeviceCollection* devices = NULL;
        if (cbDataSize == sizeof(APOInitSystemEffects2))
        {
            devices = ((APOInitSystemEffects2*)pbyData)->pDeviceCollection;
            if (((APOInitSystemEffects2*)pbyData)->InitializeForDiscoveryOnly) info |= 0x200;
        }
        else if (cbDataSize == sizeof(APOInitSystemEffects)) devices = ((APOInitSystemEffects*)pbyData)->pDeviceCollection;
        if (devices)
        {
            UINT n = 0; IMMDevice* dev = NULL; LPWSTR id = NULL;
            if (SUCCEEDED(devices->GetCount(&n))) info |= n & 0xff;
            for (UINT k = 0; k < n && k < 8; k++)
            {
                IMMDevice* d = NULL; LPWSTR s = NULL;
                if (SUCCEEDED(devices->Item(k, &d)) && d) { if (SUCCEEDED(d->GetId(&s)) && s) { Trace(L"object %p:   device %u of %u: %.200s", this, k + 1, n, s); CoTaskMemFree(s); } d->Release(); }
            }
            if (n > 0 && SUCCEEDED(devices->Item(n - 1, &dev)) && dev)
            {
                if (SUCCEEDED(dev->GetId(&id)) && id)
                {
                    // "{0.0.0.00000000}.{b561b899-5dc9-41da-b99e-4668057c9e68}": the second pair of braces
                    const wchar_t* last = wcsrchr(id, L'{');
                    GUID g;
                    if (last && SUCCEEDED(CLSIDFromString(last, &g))) { endpoint = g; haveEndpoint = true; }
                    CoTaskMemFree(id);
                }
                dev->Release();
            }
        }
        if (g_haveTestEndpoint) { endpoint = g_testEndpoint; haveEndpoint = true; }
        Map();
        Note(cbDataSize, info | (haveEndpoint ? 0x100 : 0), true);
        Trace(L"object %p: Initialize done%s, device %s, shared file %s", this, (info & 0x200) ? L" (only to list the effects)" : L"", haveEndpoint ? GuidText(endpoint).s : L"NOT READ", shared ? (canWrite ? L"open" : L"read only") : L"NOT OPEN");
        return S_OK;
    }

    // a format Windows offers and the effect (its base class) does not take: remembered for --audio-status
    STDMETHODIMP IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* asked, IAudioMediaType** supported)
    {
        HRESULT hr = CBaseAudioProcessingObject::IsInputFormatSupported(opposite, asked, supported);
        if (hr != S_OK && shared && canWrite) shared->diag.formatHr = (uint32_t)hr;
        wchar_t a[80], b[80]; FormatText(asked, a); FormatText(opposite, b);
        Trace(L"object %p: input format %s (output: %s) 0x%08X", this, a, b, hr);
        return hr;
    }
    STDMETHODIMP IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* asked, IAudioMediaType** supported)
    {
        HRESULT hr = CBaseAudioProcessingObject::IsOutputFormatSupported(opposite, asked, supported);
        if (hr != S_OK && shared && canWrite) shared->diag.formatHr = (uint32_t)hr;
        wchar_t a[80], b[80]; FormatText(asked, a); FormatText(opposite, b);
        Trace(L"object %p: output format %s (input: %s) 0x%08X", this, a, b, hr);
        return hr;
    }

    STDMETHODIMP LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** in, UINT32 nOut, APO_CONNECTION_DESCRIPTOR** out)
    {
        HRESULT hr = CBaseAudioProcessingObject::LockForProcess(nIn, in, nOut, out);
        InterlockedIncrement(&g_locks);
        if (!shared) Map();
        if (shared && canWrite) { shared->diag.locks = (uint32_t)g_locks; shared->diag.lockHr = (uint32_t)hr; shared->diag.pid = GetCurrentProcessId(); }
        Trace(L"object %p: LockForProcess 0x%08X, %u Hz %u ch", this, hr, SUCCEEDED(hr) ? (UINT)GetFramesPerSecond() : 0, SUCCEEDED(hr) ? (UINT)GetSamplesPerFrame() : 0);
        if (FAILED(hr)) return hr;
        if (!dsp)
        {
            // its own pages, locked into memory: the sound thread must not wait for the disk
            dsp = (RtDsp*)VirtualAlloc(NULL, sizeof(RtDsp), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (!dsp) { CBaseAudioProcessingObject::UnlockForProcess(); return E_OUTOFMEMORY; }
            VirtualLock(dsp, sizeof(RtDsp));
        }
        dsp->Init(GetFramesPerSecond(), (int)GetSamplesPerFrame());
        if (!shared) Map();
        slot = NULL; lastSeq = -1; sinceLook = 0; tail = 0;
        Find();
        Poll();
        return S_OK;
    }

    STDMETHODIMP GetLatency(HNSTIME* t)
    {
        if (!t) return E_POINTER;
        *t = (HNSTIME)((RTD_AHEAD + RTD_LOOK + RTD_TPLATE - 1) * 10000000.0 / 48000.0); // the two look-aheads (compressor, limiter), the same at every rate
        return S_OK;
    }

    STDMETHODIMP_(void) APOProcess(UINT32, APO_CONNECTION_PROPERTY** in, UINT32, APO_CONNECTION_PROPERTY** out)
    {
        APO_CONNECTION_PROPERTY* i = in[0]; APO_CONNECTION_PROPERTY* o = out[0];
        UINT32 frames = i->u32ValidFrameCount;
        if (i->u32BufferFlags == BUFFER_INVALID || !dsp || m_u32SamplesPerFrame > RTD_MAXCH)
        {
            // more channels than the effect handles (never seen: Windows mixes to at most 8): the sound goes through as it is
            if (i->u32BufferFlags != BUFFER_INVALID && o->pBuffer != i->pBuffer) memcpy((void*)o->pBuffer, (void*)i->pBuffer, (size_t)frames * m_u32SamplesPerFrame * sizeof(float));
            o->u32ValidFrameCount = frames; o->u32BufferFlags = i->u32BufferFlags;
            return;
        }
        float* pi = (float*)i->pBuffer; float* po = (float*)o->pBuffer;
        int ch = dsp->ch;
        if (i->u32BufferFlags == BUFFER_SILENT)
        {
            // silence coming in: the effect still holds the last 1.3 ms and the filters' tails, so it keeps running
            // on zeros for half a second, then says «silent» itself and Windows can rest
            if (tail == 0) { o->u32ValidFrameCount = frames; o->u32BufferFlags = BUFFER_SILENT; Beat(frames, true); return; }
            memset(pi, 0, (size_t)frames * ch * sizeof(float));
            tail = tail > frames ? tail - frames : 0;
        }
        else tail = (uint32_t)(dsp->rate * 0.5);

        unsigned csr = _mm_getcsr();
        _mm_setcsr(csr | 0x8040); // tiny numbers count as zero: a filter fading into silence does not slow the thread down
        Poll();
        dsp->Process(pi, po, (int)frames);
        _mm_setcsr(csr);

        o->u32ValidFrameCount = frames; o->u32BufferFlags = BUFFER_VALID;
        if (slot && canWrite)
        {
            // the output, mono, for the spectrum on the page
            uint32_t p = ringPos;
            if (ch >= 2) for (UINT32 f = 0; f < frames; f++) slot->ring[p++ & (RTA_RING - 1)] = (po[f * ch] + po[f * ch + 1]) * 0.5f;
            else for (UINT32 f = 0; f < frames; f++) slot->ring[p++ & (RTA_RING - 1)] = po[f];
            ringPos = p;
        }
        Beat(frames, false);
    }

private:
    // what Windows just asked, into the shared file (never on the sound thread)
    void Note(UINT32 size, uint32_t info, bool init)
    {
        if (!shared || !canWrite) return;
        RtaDiag* d = &shared->diag;
        d->created = (uint32_t)g_created; d->inits = (uint32_t)g_inits; d->pid = GetCurrentProcessId();
        if (init) { d->initSize = size; d->initInfo = info; memcpy(d->endpoint, &endpoint, 16); }
    }

    void Beat(UINT32 frames, bool silent)
    {
        if (silent) { Poll(); dsp->Meters(0, 0, 0, 1, NULL, NULL); }
        if (!slot || !canWrite) return;
        RtaMeters* m = &slot->m;
        m->rate = (uint32_t)dsp->rate; m->channels = (uint32_t)dsp->ch; m->frames = frames;
        m->inPeak = dsp->inPeak; m->outPeak = dsp->outPeak; m->grDb = dsp->grMin; m->limDb = dsp->limMin;
        for (int b = 0; b < 3; b++) { m->bandGr[b] = dsp->bandGr[b]; m->bandLv[b] = dsp->bandLv[b]; }
        m->pid = GetCurrentProcessId(); m->version = RTA_VERSION; m->faults = dsp->faults; m->ringPos = ringPos;
        m->beat++;
    }

    // the device's slot in the shared file (16 short compares; safe on the sound thread)
    void Find()
    {
        slot = NULL;
        if (!shared || !haveEndpoint) return;
        for (int k = 0; k < RTA_SLOTS; k++)
            if (memcmp(shared->slots[k].endpoint, &endpoint, 16) == 0) { slot = &shared->slots[k]; lastSeq = -1; return; }
    }

    // new settings, when the program has finished writing them
    void Poll()
    {
        if (!dsp) return;
        if (slot && memcmp(slot->endpoint, &endpoint, 16) != 0) { slot = NULL; RtaParams off; memset(&off, 0, sizeof(off)); dsp->Set(&off); }
        if (!slot)
        {
            if (++sinceLook < 100) return; // look again about once a second
            sinceLook = 0;
            Find();
            if (!slot) return;
        }
        long s = slot->seq;
        if (s == lastSeq || (s & 1)) return;
        RtaParams p; memcpy(&p, &slot->p, sizeof(p));
        MemoryBarrier();
        if (slot->seq != s) return; // the program wrote meanwhile: next block
        dsp->Set(&p); lastSeq = s;
    }

    void Map()
    {
        Unmap();
        wchar_t path[MAX_PATH];
        if (g_testFile[0]) lstrcpynW(path, g_testFile, MAX_PATH);
        else
        {
            if (FAILED(SHGetFolderPathW(NULL, CSIDL_COMMON_APPDATA, NULL, SHGFP_TYPE_CURRENT, path))) return;
            if (lstrlenW(path) + 24 >= MAX_PATH) return;
            lstrcatW(path, L"\\RedTune\\audio.bin");
        }
        // with the right to write (the meters); without it the effect still works, the page just shows no levels
        canWrite = true;
        file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (file == INVALID_HANDLE_VALUE)
        {
            canWrite = false;
            file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
            if (file == INVALID_HANDLE_VALUE) return;
        }
        LARGE_INTEGER size;
        if (!GetFileSizeEx(file, &size) || size.QuadPart < (LONGLONG)sizeof(RtaShared)) { Unmap(); return; }
        mapping = CreateFileMappingW(file, NULL, canWrite ? PAGE_READWRITE : PAGE_READONLY, 0, 0, NULL);
        if (!mapping) { Unmap(); return; }
        shared = (RtaShared*)MapViewOfFile(mapping, canWrite ? FILE_MAP_ALL_ACCESS : FILE_MAP_READ, 0, 0, sizeof(RtaShared));
        if (!shared) { Unmap(); return; }
        if (shared->magic != RTA_MAGIC || shared->layout != RTA_LAYOUT || shared->slotSize != sizeof(RtaSlot) || shared->slotCount != RTA_SLOTS) { Unmap(); return; }
        VirtualLock(shared, sizeof(RtaShared));
    }

    void Unmap()
    {
        slot = NULL;
        if (shared) { UnmapViewOfFile(shared); shared = NULL; }
        if (mapping) { CloseHandle(mapping); mapping = NULL; }
        if (file != INVALID_HANDLE_VALUE) { CloseHandle(file); file = INVALID_HANDLE_VALUE; }
    }
};

class Factory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID iid, void** out)
    {
        if (!out) return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IClassFactory)) { *out = static_cast<IClassFactory*>(this); return S_OK; }
        *out = NULL; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return 2; }   // one static object, never freed
    STDMETHODIMP_(ULONG) Release() { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* unkOuter, REFIID iid, void** out)
    {
        if (!out) return E_POINTER;
        *out = NULL;
        if (unkOuter && iid != __uuidof(IUnknown)) { Trace(L"CreateInstance inside another object asked for %s: refused", GuidText(iid).s); return E_NOINTERFACE; }
        RedTuneApo* apo = new (std::nothrow) RedTuneApo(unkOuter);
        if (!apo) return E_OUTOFMEMORY;
        HRESULT hr = apo->NonDelegatingQueryInterface(iid, out);
        Trace(L"CreateInstance%s for %s: object %p, 0x%08X", unkOuter ? L" inside another object" : L"", GuidText(iid).s, apo, hr);
        apo->NonDelegatingRelease();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) { if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects); return S_OK; }
};
static Factory g_factory;

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** out)
{
    if (clsid != CLSID_RedTuneApo) return CLASS_E_CLASSNOTAVAILABLE;
    Trace(L"DllGetClassObject for %s", GuidText(iid).s);
    return g_factory.QueryInterface(iid, out);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

static LONG SetStr(HKEY root, const wchar_t* key, const wchar_t* name, const wchar_t* value)
{
    HKEY h;
    LONG r = RegCreateKeyExW(root, key, 0, NULL, 0, KEY_WRITE, NULL, &h, NULL);
    if (r != ERROR_SUCCESS) return r;
    r = RegSetValueExW(h, name, 0, REG_SZ, (const BYTE*)value, (DWORD)((lstrlenW(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(h);
    return r;
}

// regsvr32 RedTuneApo.dll: the COM class and its entry in the list of Windows' sound effects. It does not attach
// the effect to any device: that is done by the page «Звук» (src\AudioFx.cs), per device, with a backup.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH], key[128];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    wsprintfW(key, L"SOFTWARE\\Classes\\CLSID\\%s", kClsid);
    LONG r = SetStr(HKEY_LOCAL_MACHINE, key, NULL, L"Red Tune Audio Effect");
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    wsprintfW(key, L"SOFTWARE\\Classes\\CLSID\\%s\\InprocServer32", kClsid);
    r = SetStr(HKEY_LOCAL_MACHINE, key, NULL, path);
    if (r == ERROR_SUCCESS) r = SetStr(HKEY_LOCAL_MACHINE, key, L"ThreadingModel", L"Both");
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    return RegisterAPO(g_reg);
}

STDAPI DllUnregisterServer()
{
    wchar_t key[128];
    UnregisterAPO(CLSID_RedTuneApo);
    wsprintfW(key, L"SOFTWARE\\Classes\\CLSID\\%s", kClsid);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD why, LPVOID)
{
    if (why == DLL_PROCESS_ATTACH) { g_module = inst; DisableThreadLibraryCalls(inst); }
    return TRUE;
}
