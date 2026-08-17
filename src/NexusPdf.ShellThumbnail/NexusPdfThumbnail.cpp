// Обработчик эскизов PDF для Проводника Windows.
//
// Как это работает. Проводник создаёт объект по CLSID, отдаёт ему файл через
// IInitializeWithStream и просит картинку через IThumbnailProvider. Мы читаем
// поток, отдаём его pdfium, рисуем ПЕРВУЮ страницу в DIB нужного размера и
// возвращаем HBITMAP.
//
// Зачем нативный код. Эскизы строятся на каждую папку с документами и по
// нескольку десятков разом. Среда .NET здесь означала бы запуск рантайма на
// эту работу, поэтому тут чистый C++ без зависимостей, кроме самого pdfium.
//
// Почему объявлен свой AppID с DllSurrogate. Оболочка строит эскизы НЕ внутри
// explorer.exe, а в отдельном dllhost.exe — падение обработчика на битом файле
// не должно ронять рабочий стол. Без записи AppID изолированная активация
// заканчивается REGDB_E_CLASSNOTREG, и Проводник молча показывает значок: так
// и было при первой проверке. Вариант «попросить оболочку грузить нас внутри
// себя» (DisableProcessIsolation) снял бы ту же ошибку, но ценой затаскивания
// pdfium и разбора чужих PDF прямо в Проводник — цена несоразмерная.
//
// pdfium подключается через LoadLibrary/GetProcAddress, а не через import-lib:
// заголовков и .lib в поставке нет, а нужных функций всего девять. Заодно это
// значит, что отсутствие pdfium.dll рядом даёт аккуратный отказ, а не срыв
// загрузки библиотеки в Проводнике.

#include <windows.h>
#include <shlwapi.h>
#include <shlobj.h>      // SHChangeNotify: без него оповещение оболочки не собирается
#include <thumbcache.h>
#include <propsys.h>
#include <new>

#pragma comment(lib, "shlwapi.lib")

// {BA3A32DD-59A9-44FF-B7F2-FCF184469E12}
static const CLSID CLSID_NexusPdfThumbProvider =
    { 0xba3a32dd, 0x59a9, 0x44ff, { 0xb7, 0xf2, 0xfc, 0xf1, 0x84, 0x46, 0x9e, 0x12 } };

static const wchar_t* kFriendlyName = L"NexusPDF Thumbnail Handler";

static HINSTANCE g_hInst = nullptr;
static LONG g_refModule = 0;

// ---------------------------------------------------------------- pdfium ---

typedef void* FPDF_DOCUMENT;
typedef void* FPDF_PAGE;
typedef void* FPDF_BITMAP;

// Чтение по требованию: pdfium сам решает, какие куски файла ему нужны для
// первой страницы, и в память попадают только они. Для документа на сотни
// мегабайт это разница между «сотни мегабайт на каждый эскиз» и «несколько».
struct FPDF_FILEACCESS
{
    unsigned long m_FileLen;
    int (__stdcall *m_GetBlock)(void* param, unsigned long position,
                                unsigned char* pBuf, unsigned long size);
    void* m_Param;
};

typedef void (__stdcall *PFN_InitLibrary)(void);
typedef void (__stdcall *PFN_DestroyLibrary)(void);
typedef FPDF_DOCUMENT (__stdcall *PFN_LoadMemDocument)(const void*, int, const char*);
typedef FPDF_DOCUMENT (__stdcall *PFN_LoadCustomDocument)(FPDF_FILEACCESS*, const char*);
typedef void (__stdcall *PFN_CloseDocument)(FPDF_DOCUMENT);
typedef FPDF_PAGE (__stdcall *PFN_LoadPage)(FPDF_DOCUMENT, int);
typedef void (__stdcall *PFN_ClosePage)(FPDF_PAGE);
typedef double (__stdcall *PFN_GetPageWidth)(FPDF_PAGE);
typedef double (__stdcall *PFN_GetPageHeight)(FPDF_PAGE);
typedef FPDF_BITMAP (__stdcall *PFN_BitmapCreateEx)(int, int, int, void*, int);
typedef void (__stdcall *PFN_BitmapDestroy)(FPDF_BITMAP);
typedef void (__stdcall *PFN_RenderPageBitmap)(FPDF_BITMAP, FPDF_PAGE, int, int, int, int, int, int);

struct Pdfium
{
    HMODULE module = nullptr;
    PFN_InitLibrary      Init = nullptr;
    PFN_DestroyLibrary   Destroy = nullptr;
    PFN_LoadMemDocument  LoadMem = nullptr;
    PFN_LoadCustomDocument LoadCustom = nullptr;
    PFN_CloseDocument    CloseDoc = nullptr;
    PFN_LoadPage         LoadPage = nullptr;
    PFN_ClosePage        ClosePage = nullptr;
    PFN_GetPageWidth     PageWidth = nullptr;
    PFN_GetPageHeight    PageHeight = nullptr;
    PFN_BitmapCreateEx   BitmapCreate = nullptr;
    PFN_BitmapDestroy    BitmapDestroy = nullptr;
    PFN_RenderPageBitmap RenderPage = nullptr;
    bool ready = false;
};

static Pdfium g_pdf;
static CRITICAL_SECTION g_pdfLock;
static bool g_lockReady = false;

// pdfium ищем ТОЛЬКО рядом с собой: полагаться на PATH в чужом процессе
// нельзя — там может оказаться посторонняя одноимённая библиотека.
static bool LoadPdfium()
{
    if (g_pdf.ready) return true;

    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_hInst, path, MAX_PATH)) return false;
    PathRemoveFileSpecW(path);
    if (!PathAppendW(path, L"pdfium.dll")) return false;

    g_pdf.module = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_pdf.module) return false;

    g_pdf.Init         = (PFN_InitLibrary)      GetProcAddress(g_pdf.module, "FPDF_InitLibrary");
    g_pdf.Destroy      = (PFN_DestroyLibrary)   GetProcAddress(g_pdf.module, "FPDF_DestroyLibrary");
    g_pdf.LoadMem      = (PFN_LoadMemDocument)  GetProcAddress(g_pdf.module, "FPDF_LoadMemDocument");
    g_pdf.LoadCustom   = (PFN_LoadCustomDocument) GetProcAddress(g_pdf.module, "FPDF_LoadCustomDocument");
    g_pdf.CloseDoc     = (PFN_CloseDocument)    GetProcAddress(g_pdf.module, "FPDF_CloseDocument");
    g_pdf.LoadPage     = (PFN_LoadPage)         GetProcAddress(g_pdf.module, "FPDF_LoadPage");
    g_pdf.ClosePage    = (PFN_ClosePage)        GetProcAddress(g_pdf.module, "FPDF_ClosePage");
    g_pdf.PageWidth    = (PFN_GetPageWidth)     GetProcAddress(g_pdf.module, "FPDF_GetPageWidth");
    g_pdf.PageHeight   = (PFN_GetPageHeight)    GetProcAddress(g_pdf.module, "FPDF_GetPageHeight");
    g_pdf.BitmapCreate = (PFN_BitmapCreateEx)   GetProcAddress(g_pdf.module, "FPDFBitmap_CreateEx");
    g_pdf.BitmapDestroy= (PFN_BitmapDestroy)    GetProcAddress(g_pdf.module, "FPDFBitmap_Destroy");
    g_pdf.RenderPage   = (PFN_RenderPageBitmap) GetProcAddress(g_pdf.module, "FPDF_RenderPageBitmap");

    if (!g_pdf.Init || !g_pdf.LoadCustom || !g_pdf.CloseDoc || !g_pdf.LoadPage ||
        !g_pdf.ClosePage || !g_pdf.PageWidth || !g_pdf.PageHeight ||
        !g_pdf.BitmapCreate || !g_pdf.BitmapDestroy || !g_pdf.RenderPage)
    {
        FreeLibrary(g_pdf.module);
        g_pdf.module = nullptr;
        return false;
    }

    g_pdf.Init();
    g_pdf.ready = true;
    return true;
}

// -------------------------------------------------------------- провайдер ---

class ThumbProvider : public IInitializeWithStream, public IThumbnailProvider
{
public:
    ThumbProvider() : _ref(1), _stream(nullptr) { InterlockedIncrement(&g_refModule); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB qit[] =
        {
            QITABENT(ThumbProvider, IInitializeWithStream),
            QITABENT(ThumbProvider, IThumbnailProvider),
            { nullptr, 0 },
        };
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG r = InterlockedDecrement(&_ref);
        if (r == 0) delete this;
        return r;
    }

    // IInitializeWithStream
    IFACEMETHODIMP Initialize(IStream* stream, DWORD) override
    {
        if (_stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        if (!stream) return E_INVALIDARG;
        return stream->QueryInterface(&_stream);
    }

    // IThumbnailProvider
    IFACEMETHODIMP GetThumbnail(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha) override;

private:
    ~ThumbProvider()
    {
        if (_stream) _stream->Release();
        InterlockedDecrement(&g_refModule);
    }

    LONG _ref;
    IStream* _stream;
};

// Мост между IStream Проводника и читателем pdfium. Вызывается из того же
// потока, что и GetThumbnail, — параллельного доступа к потоку тут нет.
struct StreamReader
{
    IStream* stream;
};

static int __stdcall StreamGetBlock(void* param, unsigned long position,
                                    unsigned char* buf, unsigned long size)
{
    StreamReader* r = (StreamReader*)param;
    if (!r || !r->stream || !buf || size == 0) return 0;

    LARGE_INTEGER pos;
    pos.QuadPart = (LONGLONG)position;
    if (FAILED(r->stream->Seek(pos, STREAM_SEEK_SET, nullptr))) return 0;

    unsigned long total = 0;
    while (total < size)
    {
        ULONG got = 0;
        if (FAILED(r->stream->Read(buf + total, size - total, &got)) || got == 0) return 0;
        total += got;
    }
    return 1;
}

IFACEMETHODIMP ThumbProvider::GetThumbnail(UINT cx, HBITMAP* phbmp, WTS_ALPHATYPE* pdwAlpha)
{
    if (!phbmp || !pdwAlpha) return E_POINTER;
    *phbmp = nullptr;
    *pdwAlpha = WTSAT_UNKNOWN;
    if (!_stream || cx == 0) return E_FAIL;

    if (!g_lockReady) return E_FAIL;
    EnterCriticalSection(&g_pdfLock);
    bool ok = LoadPdfium();
    LeaveCriticalSection(&g_pdfLock);
    if (!ok) return E_FAIL;

    // Размер нужен pdfium заранее; сами байты он заберёт по мере надобности.
    STATSTG st = {};
    if (FAILED(_stream->Stat(&st, STATFLAG_NONAME)) || st.cbSize.QuadPart == 0) return E_FAIL;
    if (st.cbSize.QuadPart > 0xFFFFFFFFull) return E_FAIL;   // 4 ГиБ — предел формата доступа

    StreamReader reader = { _stream };
    FPDF_FILEACCESS access = {};
    access.m_FileLen = (unsigned long)st.cbSize.QuadPart;
    access.m_GetBlock = StreamGetBlock;
    access.m_Param = &reader;

    HRESULT hr = E_FAIL;
    FPDF_DOCUMENT doc = nullptr;
    FPDF_PAGE page = nullptr;

    // pdfium не потокобезопасен на уровне библиотеки, а Проводник строит эскизы
    // параллельно. Держим один замок на всю работу с документом.
    EnterCriticalSection(&g_pdfLock);

    doc = g_pdf.LoadCustom(&access, nullptr);
    if (doc)
    {
        page = g_pdf.LoadPage(doc, 0);
        if (page)
        {
            double wPt = g_pdf.PageWidth(page);
            double hPt = g_pdf.PageHeight(page);
            if (wPt > 0.0 && hPt > 0.0)
            {
                // Вписываем страницу в квадрат cx, сохраняя пропорции: лист А4
                // должен остаться листом, а не растянуться в квадрат.
                double scale = (wPt >= hPt) ? (double)cx / wPt : (double)cx / hPt;
                int w = (int)(wPt * scale + 0.5);
                int h = (int)(hPt * scale + 0.5);
                if (w < 1) w = 1;
                if (h < 1) h = 1;

                BITMAPINFO bmi = {};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;   // сверху вниз, как у pdfium
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                void* bits = nullptr;
                HBITMAP hbmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
                if (hbmp && bits)
                {
                    const int stride = w * 4;
                    // Белая бумага под страницей: pdfium холст не очищает, и без
                    // заливки на прозрачных участках остался бы мусор памяти.
                    memset(bits, 0xFF, (size_t)stride * h);

                    FPDF_BITMAP fb = g_pdf.BitmapCreate(w, h, 4 /* BGRA */, bits, stride);
                    if (fb)
                    {
                        g_pdf.RenderPage(fb, page, 0, 0, w, h, 0, 0x10 /* FPDF_ANNOT */);
                        g_pdf.BitmapDestroy(fb);

                        // Непрозрачность: страница отдаётся как обычная картинка,
                        // иначе Проводник покажет её сквозь фон.
                        BYTE* px = (BYTE*)bits;
                        for (int i = 3; i < stride * h; i += 4) px[i] = 0xFF;

                        *phbmp = hbmp;
                        *pdwAlpha = WTSAT_RGB;
                        hbmp = nullptr;
                        hr = S_OK;
                    }
                }
                if (hbmp) DeleteObject(hbmp);
            }
            g_pdf.ClosePage(page);
        }
        g_pdf.CloseDoc(doc);
    }

    LeaveCriticalSection(&g_pdfLock);
    return hr;
}

// ------------------------------------------------------------ фабрика COM ---

class ClassFactory : public IClassFactory
{
public:
    ClassFactory() : _ref(1) { InterlockedIncrement(&g_refModule); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB qit[] = { QITABENT(ClassFactory, IClassFactory), { nullptr, 0 } };
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG r = InterlockedDecrement(&_ref);
        if (r == 0) delete this;
        return r;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        ThumbProvider* p = new (std::nothrow) ThumbProvider();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_refModule); else InterlockedDecrement(&g_refModule);
        return S_OK;
    }

private:
    ~ClassFactory() { InterlockedDecrement(&g_refModule); }
    LONG _ref;
};

// ---------------------------------------------------------------- экспорт ---

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!IsEqualCLSID(rclsid, CLSID_NexusPdfThumbProvider)) return CLASS_E_CLASSNOTAVAILABLE;
    ClassFactory* f = new (std::nothrow) ClassFactory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_refModule == 0 ? S_OK : S_FALSE;
}

static HRESULT SetKeyValue(HKEY root, const wchar_t* sub, const wchar_t* name, const wchar_t* value)
{
    HKEY key = nullptr;
    LONG rc = RegCreateKeyExW(root, sub, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (rc != ERROR_SUCCESS) return HRESULT_FROM_WIN32(rc);
    rc = RegSetValueExW(key, name, 0, REG_SZ, (const BYTE*)value,
                        (DWORD)((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(rc);
}

// Саморегистрация нужна для отладки и ручной установки; в поставке ключи пишет
// установщик, чтобы удаление программы гарантированно их убирало.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_hInst, path, MAX_PATH)) return E_FAIL;

    const wchar_t* clsid = L"CLSID\\{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}";
    const wchar_t* appid = L"AppID\\{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}";
    wchar_t inproc[MAX_PATH + 64];
    wsprintfW(inproc, L"%s\\InprocServer32", clsid);

    HRESULT hr = SetKeyValue(HKEY_CLASSES_ROOT, clsid, nullptr, kFriendlyName);
    if (SUCCEEDED(hr)) hr = SetKeyValue(HKEY_CLASSES_ROOT, inproc, nullptr, path);
    if (SUCCEEDED(hr)) hr = SetKeyValue(HKEY_CLASSES_ROOT, inproc, L"ThreadingModel", L"Apartment");
    // Пустой DllSurrogate = «размести меня в стандартном dllhost.exe». Пара
    // AppID у класса и ключ AppID с этим значением обязательны оба: без них
    // изолированная активация не находит класс.
    if (SUCCEEDED(hr)) hr = SetKeyValue(HKEY_CLASSES_ROOT, clsid, L"AppID",
                                        L"{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}");
    if (SUCCEEDED(hr)) hr = SetKeyValue(HKEY_CLASSES_ROOT, appid, nullptr, kFriendlyName);
    if (SUCCEEDED(hr)) hr = SetKeyValue(HKEY_CLASSES_ROOT, appid, L"DllSurrogate", L"");
    if (SUCCEEDED(hr))
        hr = SetKeyValue(HKEY_CLASSES_ROOT,
                         L".pdf\\ShellEx\\{E357FCCD-A995-4576-B01F-234630154E96}",
                         nullptr, L"{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}");
    if (SUCCEEDED(hr))
        hr = SetKeyValue(HKEY_CLASSES_ROOT,
                         L"NexusPdf.Document.1\\ShellEx\\{E357FCCD-A995-4576-B01F-234630154E96}",
                         nullptr, L"{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}");

    if (SUCCEEDED(hr)) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return hr;
}

STDAPI DllUnregisterServer()
{
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"CLSID\\{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}");
    RegDeleteTreeW(HKEY_CLASSES_ROOT, L"AppID\\{BA3A32DD-59A9-44FF-B7F2-FCF184469E12}");
    RegDeleteKeyW(HKEY_CLASSES_ROOT, L".pdf\\ShellEx\\{E357FCCD-A995-4576-B01F-234630154E96}");
    RegDeleteKeyW(HKEY_CLASSES_ROOT, L"NexusPdf.Document.1\\ShellEx\\{E357FCCD-A995-4576-B01F-234630154E96}");
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hInst;
        DisableThreadLibraryCalls(hInst);
        InitializeCriticalSection(&g_pdfLock);
        g_lockReady = true;
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // pdfium намеренно НЕ выгружаем: Проводник может держать обработчик
        // живым, а повторная инициализация библиотеки в том же процессе
        // надёжностью не отличается.
        if (g_lockReady) { DeleteCriticalSection(&g_pdfLock); g_lockReady = false; }
    }
    return TRUE;
}
