#include "tls.h"
#include "server_config.h"
#include "equipment_archive.h"
#include <commdlg.h>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <thread>

namespace {
constexpr wchar_t WindowClass[] = L"ElanciaSecureLauncher";
constexpr wchar_t AccountAlphabet[] = L"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
constexpr UINT Done = WM_APP + 1;
constexpr UINT GameClosed = WM_APP + 2;
constexpr UINT SessionRejected = WM_APP + 3;
enum Control { Browse = 100, Register, Login };
HWND hostEdit, gameEdit, accountEdit, passwordEdit, browseButton, registerButton, loginButton, statusLabel;
HFONT font;
bool busy = false;
struct GameBridge;
std::shared_ptr<GameBridge> activeGame;

struct Settings {
    std::wstring host, port, game, account, password;
    Digest pin{};
    ~Settings() { if (!password.empty()) SecureZeroMemory(password.data(), password.size() * sizeof(wchar_t)); }
};

struct Result {
    std::wstring message;
    std::shared_ptr<GameBridge> game;
    bool renewal = false;
};

std::wstring Text(HWND control) {
    int size = GetWindowTextLengthW(control);
    std::wstring value(size + 1, 0);
    value.resize(GetWindowTextW(control, value.data(), size + 1));
    return value;
}

void SetStatus(const wchar_t* value) { SetWindowTextW(statusLabel, value); }

std::string Utf8(const std::wstring& value) {
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), value.size(), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::runtime_error("The account ID and password are required.");
    std::string result(size, 0);
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), value.size(), result.data(), size, nullptr, nullptr);
    return result;
}

Digest ReadPin(const std::wstring& text) {
    Digest result{};
    size_t digits = 0;
    for (auto ch : text) {
        if (ch == L':' || ch == L' ' || ch == L'-') continue;
        if (ch >= L'0' && ch <= L'9') ch -= L'0';
        else if (ch >= L'A' && ch <= L'F') ch -= L'A' - 10;
        else if (ch >= L'a' && ch <= L'f') ch -= L'a' - 10;
        else throw std::runtime_error("Enter the server certificate's SHA-256 fingerprint.");
        if (digits >= 64) throw std::runtime_error("The fingerprint must have 64 hexadecimal digits.");
        auto& byte = result[digits / 2];
        byte = static_cast<unsigned char>(byte * 16 + ch);
        ++digits;
    }
    if (digits != 64) throw std::runtime_error("The fingerprint must have 64 hexadecimal digits.");
    return result;
}

Digest LauncherPin(const std::wstring& host) {
    if (*ServerCertificateSha256) return ReadPin(ServerCertificateSha256);
    if (host != L"127.0.0.1") throw std::runtime_error("This launcher connects to 127.0.0.1 only.");
    std::wstring executable(32768, 0);
    const DWORD length = GetModuleFileNameW(nullptr, executable.data(), executable.size());
    if (!length || length >= executable.size()) throw std::runtime_error("The launcher path could not be read.");
    executable.resize(length);
    const auto directory = std::filesystem::path(executable).parent_path();
    for (const auto& file : {directory / L"certificate.sha256",
            directory.parent_path() / L"ElanciaLocalServer" / L"certificate.sha256"}) {
        std::ifstream stream(file);
        std::string fingerprint, extra;
        if (stream >> fingerprint && fingerprint.size() == 64 && !(stream >> extra))
            return ReadPin({fingerprint.begin(), fingerprint.end()});
    }
    throw std::runtime_error("Start the local server or copy its certificate.sha256 next to this launcher.");
}

void AppendWord(Bytes& bytes, uint16_t value) {
    bytes.push_back(static_cast<unsigned char>(value >> 8));
    bytes.push_back(static_cast<unsigned char>(value));
}

void AppendDword(Bytes& bytes, uint32_t value) {
    for (int shift = 0; shift <= 24; shift += 8) bytes.push_back(static_cast<unsigned char>(value >> shift));
}

struct CredentialBytes {
    Bytes value;
    ~CredentialBytes() { if (!value.empty()) SecureZeroMemory(value.data(), value.size()); }
    void Append(const std::wstring& text) {
        auto encoded = Utf8(text);
        AppendWord(value, static_cast<uint16_t>(encoded.size()));
        value.insert(value.end(), encoded.begin(), encoded.end());
        SecureZeroMemory(encoded.data(), encoded.size());
    }
};

Digest Authenticate(const Settings& settings, bool registration) {
    Tls tls(settings.host, settings.port, settings.pin);
    CredentialBytes request;
    request.value.reserve(550);
    request.value.push_back(1);
    request.value.push_back(registration ? 1 : 2);
    request.Append(settings.account);
    request.Append(settings.password);
    tls.Write(request.value.data(), request.value.size());
    unsigned char status{};
    tls.ReadExact(&status, 1);
    if (status == 2) throw std::runtime_error("Too many attempts. Try again in one minute.");
    if (status) throw std::runtime_error(registration ? "Registration failed. The account ID may be taken."
                                                     : "Account ID or password is incorrect.");
    Digest token{};
    if (!registration) tls.ReadExact(token.data(), token.size());
    return token;
}

struct Listener {
    Socket socket{::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)};
    uint16_t port{};
    Listener() {
        BOOL exclusive = TRUE;
        if (socket == INVALID_SOCKET ||
            setsockopt(socket, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)))
            throw std::runtime_error("Could not allocate a local game proxy.");
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        int size = sizeof(address);
        if (bind(socket, reinterpret_cast<sockaddr*>(&address), size) || listen(socket, 8) ||
            getsockname(socket, reinterpret_cast<sockaddr*>(&address), &size))
            throw std::runtime_error("Could not open a local game proxy.");
        port = ntohs(address.sin_port);
    }
};

struct GameBridge {
    const std::wstring host, port, account;
    HWND window;
    std::mutex tokenLock;
    Digest pin, token;
    unsigned generation = 0;
    bool rejected = false;
    Listener listeners[3];
    std::atomic<unsigned> relays{0};
    GameBridge(const Settings& settings, const Digest& loginToken, HWND window)
        : host(settings.host), port(settings.port), account(settings.account),
          window(window), pin(settings.pin), token(loginToken) {}
    ~GameBridge() { SecureZeroMemory(token.data(), token.size()); }

    bool NeedsLogin() {
        std::lock_guard<std::mutex> lock(tokenLock);
        return rejected;
    }

    void Renew(const Digest& newToken) {
        std::lock_guard<std::mutex> lock(tokenLock);
        token = newToken;
        ++generation;
        rejected = false;
    }

    void Relay(Socket local, unsigned char role) {
        if (NeedsLogin()) return;
        // Keep the native bootstrap and login sockets open during a short restart.
        constexpr DWORD loginRetryMs = 6000;
        Tls remote(host, port, pin, role != 2 ? loginRetryMs : 0);
        Bytes route{1, 3, role};
        route.reserve(39);
        unsigned attempt;
        {
            std::lock_guard<std::mutex> lock(tokenLock);
            if (rejected) return;
            attempt = generation;
            route.insert(route.end(), token.begin(), token.end());
        }
        AppendWord(route, listeners[1].port);
        AppendWord(route, listeners[2].port);
        try { remote.Write(route.data(), route.size()); }
        catch (...) {
            SecureZeroMemory(route.data(), route.size());
            throw;
        }
        SecureZeroMemory(route.data(), route.size());
        unsigned char status{};
        remote.ReadExact(&status, 1);
        if (status) {
            std::lock_guard<std::mutex> lock(tokenLock);
            if (status == 1 && attempt == generation && !rejected) {
                rejected = true;
                PostMessageW(window, SessionRejected, 0, 0);
            }
            return;
        }
        remote.DisableTimeout();
        BOOL enabled = TRUE;
        setsockopt(local, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&enabled), sizeof(enabled));
        std::thread toServer([&] {
            try {
                unsigned char buffer[16384];
                int size;
                while ((size = recv(local, reinterpret_cast<char*>(buffer), sizeof(buffer), 0)) > 0)
                    remote.Write(buffer, size);
            } catch (const std::exception&) { }
            remote.Stop();
        });
        try {
            unsigned char buffer[16384];
            size_t size;
            while ((size = remote.Read(buffer, sizeof(buffer))))
                SendAll(local, buffer, size);
        } catch (const std::exception&) { }
        shutdown(local, SD_BOTH);
        remote.Stop();
        toServer.join();
    }

    void Run(const std::shared_ptr<GameBridge>& self, HANDLE process) {
        while (WaitForSingleObject(process, 0) == WAIT_TIMEOUT) {
            fd_set ready;
            FD_ZERO(&ready);
            for (auto& listener : listeners) FD_SET(listener.socket, &ready);
            timeval tick{0, 250000};
            if (select(0, &ready, nullptr, nullptr, &tick) <= 0) continue;
            for (unsigned char role = 0; role < 3; ++role) {
                if (!FD_ISSET(listeners[role].socket, &ready)) continue;
                Socket accepted(accept(listeners[role].socket, nullptr, nullptr));
                if (accepted == INVALID_SOCKET || relays.load() >= 12) continue;
                ++relays;
                try {
                    std::thread([self, local = std::move(accepted), role]() mutable {
                        try { self->Relay(std::move(local), role); }
                        catch (const std::exception&) { }
                        --self->relays;
                    }).detach();
                } catch (const std::exception&) { --relays; }
            }
        }
    }
};

void WriteRemote(HANDLE process, uintptr_t address, const Bytes& bytes) {
    auto target = reinterpret_cast<void*>(address);
    DWORD previous{};
    if (!VirtualProtectEx(process, target, bytes.size(), PAGE_EXECUTE_READWRITE, &previous))
        throw std::runtime_error("The client code could not be prepared.");
    SIZE_T written{};
    BOOL success = WriteProcessMemory(process, target, bytes.data(), bytes.size(), &written);
    DWORD ignored{};
    BOOL restored = VirtualProtectEx(process, target, bytes.size(), previous, &ignored);
    if (!success || written != bytes.size() || !restored || !FlushInstructionCache(process, target, bytes.size()))
        throw std::runtime_error("The client code could not be prepared.");
}

void Patch(HANDLE process, uintptr_t address, const Bytes& expected, const Bytes& replacement) {
    Bytes current(expected.size());
    SIZE_T read{};
    if (!ReadProcessMemory(process, reinterpret_cast<void*>(address), current.data(), current.size(), &read) ||
        read != current.size() || current != expected)
        throw std::runtime_error("This jElancia.exe has a different in-memory layout.");
    WriteRemote(process, address, replacement);
}

void Jump(HANDLE process, uintptr_t address, const Bytes& expected, uintptr_t destination) {
    Bytes branch{0xe9};
    AppendDword(branch, static_cast<uint32_t>(destination - address - branch.size() - 4));
    Patch(process, address, expected, branch);
}

void PrepareClient(HANDLE process, const std::wstring& archive) {
    constexpr uintptr_t news = 0x4105e6, launcherAuth = 0x410bab, passport = 0x40ed41;
    auto page = reinterpret_cast<uintptr_t>(VirtualAllocEx(process, nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!page) throw std::runtime_error("The client bootstrap could not be allocated.");
    // The native login receives a fixed placeholder. Account credentials stay in the TLS launcher.
    WriteRemote(process, page, {'l',0,'o',0,'c',0,'a',0,'l',0,0,0});
    Bytes passportReturn{0xb8};
    AppendDword(passportReturn, static_cast<uint32_t>(page));
    passportReturn.push_back(0xc3);
    Patch(process, news, {0x55,0x8b,0xec}, {0x31,0xc0,0xc2,0x04,0x00});
    Patch(process, launcherAuth, {0x55,0x8b,0xec,0x6a,0xff}, {0xb8,1,0,0,0,0xc2,0x04,0x00});
    Patch(process, passport, {0x55,0x8b,0xec,0x6a,0xff}, passportReturn);

    // Read the prepared resource archive alongside the launcher. The installed game data stays intact.
    auto archiveBytes = reinterpret_cast<const unsigned char*>(archive.c_str());
    WriteRemote(process, page + 256, Bytes(archiveBytes, archiveBytes + (archive.size() + 1) * sizeof(wchar_t)));
    Bytes archivePointer;
    AppendDword(archivePointer, static_cast<uint32_t>(page + 256));
    Patch(process, 0x43c345, {0x4c,0xad,0x53,0x00}, archivePointer);

    // Drop cached server entries immediately before the configured bootstrap list is parsed.
    // This cache stores 0x33-byte records. Its destructor takes one stdcall pointer.
    constexpr uintptr_t listParser = 0x46da01;
    Bytes clearList{
        0x9c,0x60,                                   // pushfd; pushad
        0x8b,0x35,0xdc,0xea,0x58,0x00,               // mov esi, [0x58eadc]
        0x85,0xf6,0x74,0x31,                         // if cfg is null, done
        0x8b,0xbe,0x96,0,0,0,                       // mov edi, [esi+0x96]
        0x8b,0x9e,0x9a,0,0,0,                       // mov ebx, [esi+0x9a]
        0x39,0xdf,0x77,0x21,                         // if start > end, done
        0x89,0xd8,0x29,0xf8,0x3d,0xec,0x13,0,0,    // if size > 0x33*100, done
        0x77,0x16,
        0x89,0xbe,0x9a,0,0,0,                       // set end to start
        0xc7,0x86,0xa2,0,0,0,0,0,0,0,               // clear selected index
        0x39,0xdf,0x73,0x00,                         // skip loop when empty
        0x57,0xb8,0x27,0x30,0x43,0,0xff,0xd0,       // destroy(edi)
        0x83,0xc7,0x33,0x39,0xdf,0x72,0xf1,         // continue while edi < ebx
        0x61,0x9d,0x68,0x48,0x08,0,0,0xe9          // restore, replay original push, return
    };
    // Short branch displacements are indexed relative to the start of this fixed x86 sequence.
    constexpr size_t restore = 74;
    for (auto displacement : {size_t(11), size_t(27), size_t(38)})
        clearList[displacement] = static_cast<unsigned char>(restore - displacement - 1);
    clearList[58] = static_cast<unsigned char>(restore - 59);
    AppendDword(clearList, static_cast<uint32_t>(listParser + 5 - (page + 64 + clearList.size() + 4)));
    WriteRemote(process, page + 64, clearList);
    DWORD oldProtect{};
    if (!VirtualProtectEx(process, reinterpret_cast<void*>(page), 4096, PAGE_EXECUTE_READ, &oldProtect))
        throw std::runtime_error("The client bootstrap could not be activated.");
    Jump(process, listParser, {0x68,0x48,0x08,0,0}, page + 64);
}

std::filesystem::path LauncherDirectory() {
    wchar_t executable[32768];
    auto length = GetModuleFileNameW(nullptr, executable, 32768);
    if (!length || length >= 32768) throw std::runtime_error("Could not locate the launcher folder.");
    return std::filesystem::path(std::wstring(executable, length)).parent_path();
}

PROCESS_INFORMATION Launch(const std::wstring& selectedPath, uint16_t bootstrapPort) {
    auto path = std::filesystem::absolute(std::filesystem::path(selectedPath));
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file || file.tellg() != 2077744) throw std::runtime_error("Select the supported v611 jElancia.exe.");
    Bytes image(static_cast<size_t>(file.tellg()));
    file.seekg(0);
    file.read(reinterpret_cast<char*>(image.data()), image.size());
    if (!file || Sha256(image.data(), static_cast<DWORD>(image.size())) !=
        ReadPin(L"61d79a82c676e30750c015301b0eeed5f5df2729180ed8be4f45aa2042af8c5a"))
        throw std::runtime_error("This file is not the supported v611 jElancia.exe.");
    auto archive = path.parent_path() / L"Data" / L"Yog.dat";
    const auto pack = LauncherDirectory() / L"Elancia-Orc.pack";
    if (!std::filesystem::is_regular_file(archive)) throw std::runtime_error("The installed client's Data\\Yog.dat is missing.");
    if (std::filesystem::exists(pack)) {
        wchar_t localAppData[MAX_PATH];
        const DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
        if (!length || length >= MAX_PATH) throw std::runtime_error("Could not locate the equipment archive cache.");
        archive = PrepareEquipmentArchive(archive, pack, std::filesystem::path(localAppData) / L"ElanciaLocal" / L"Equipment");
    }
    std::wstring arguments = L"\"" + path.wstring() + L"\" Addr127.0.0.1:" + std::to_wstring(bootstrapPort);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION info{};
    if (!CreateProcessW(path.c_str(), arguments.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED, nullptr,
            path.parent_path().c_str(), &startup, &info))
        throw std::runtime_error("jElancia.exe could not be started.");
    try {
        PrepareClient(info.hProcess, archive.wstring());
        if (ResumeThread(info.hThread) == DWORD(-1)) throw std::runtime_error("jElancia.exe could not be resumed.");
    } catch (...) {
        TerminateProcess(info.hProcess, 1);
        CloseHandle(info.hThread);
        CloseHandle(info.hProcess);
        throw;
    }
    CloseHandle(info.hThread);
    return info;
}

void Toggle() {
    const bool playing = activeGame != nullptr;
    SetWindowTextW(loginButton, playing && activeGame->NeedsLogin() ? L"Sign in again" : L"Log in and play");
    EnableWindow(loginButton, !busy && (!playing || activeGame->NeedsLogin()));
    EnableWindow(registerButton, !busy && !playing);
    EnableWindow(browseButton, !busy && !playing);
    EnableWindow(hostEdit, !busy && !playing);
    EnableWindow(gameEdit, !busy && !playing);
    EnableWindow(accountEdit, !busy && !playing);
}

std::wstring ErrorMessage(const std::exception& error) {
    std::string value(error.what());
    return {value.begin(), value.end()};
}

void Start(HWND window, bool registration) {
    auto game = activeGame;
    if (busy || (game && (registration || !game->NeedsLogin()))) return;
    try {
        auto settings = std::make_unique<Settings>();
        settings->host = game ? game->host : Text(hostEdit);
        settings->pin = game ? game->pin : LauncherPin(settings->host);
        settings->port = game ? game->port : L"7500";
        if (!game) settings->game = Text(gameEdit);
        settings->account = game ? game->account : Text(accountEdit);
        settings->password = Text(passwordEdit);
        if (settings->host.empty()) throw std::runtime_error("Enter the server IP address or host name.");
        if (settings->account.size() < 4 || settings->account.size() > 24 ||
            settings->account.find_first_not_of(AccountAlphabet) != std::wstring::npos)
            throw std::runtime_error("Use 4 to 24 English letters or digits for the account ID.");
        if (settings->password.size() < 4 || settings->password.size() > 128)
            throw std::runtime_error("Use a password with 4 to 128 characters.");
        if (registration && settings->password.find_first_not_of(AccountAlphabet) != std::wstring::npos)
            throw std::runtime_error("Use only English letters or digits in the new password.");
        SetWindowTextW(passwordEdit, L"");
        busy = true;
        Toggle();
        SetStatus(L"Connecting securely...");
        std::thread([window, registration, game, settings = std::move(settings)] {
            auto result = std::make_unique<Result>();
            result->renewal = game != nullptr;
            result->game = game;
            try {
                auto token = Authenticate(*settings, registration);
                SecureZeroMemory(settings->password.data(), settings->password.size() * sizeof(wchar_t));
                if (registration) {
                    result->message = L"Account created. Enter your password again to start the game.";
                } else if (game) {
                    game->Renew(token);
                    SecureZeroMemory(token.data(), token.size());
                    result->message = L"Signed in again. Click Connect again in the game.";
                } else {
                    auto bridge = std::make_shared<GameBridge>(*settings, token, window);
                    SecureZeroMemory(token.data(), token.size());
                    auto process = Launch(settings->game, bridge->listeners[0].port);
                    result->message = L"Game running. Keep this window open.";
                    result->game = bridge;
                    if (PostMessageW(window, Done, 0, reinterpret_cast<LPARAM>(result.get()))) result.release();
                    bridge->Run(bridge, process.hProcess);
                    CloseHandle(process.hProcess);
                    PostMessageW(window, GameClosed, 0, 0);
                    return;
                }
            } catch (const std::exception& error) { result->message = ErrorMessage(error); }
            if (PostMessageW(window, Done, 0, reinterpret_cast<LPARAM>(result.get()))) result.release();
        }).detach();
    } catch (const std::exception& error) {
        busy = false;
        Toggle();
        SetStatus(ErrorMessage(error).c_str());
    }
}

HWND ControlWindow(HWND window, const wchar_t* kind, const wchar_t* text, DWORD style,
                   int x, int y, int width, int height, int id = 0, DWORD extra = 0) {
    auto handle = CreateWindowExW(extra, kind, text, WS_CHILD | WS_VISIBLE | style, x, y, width, height,
        window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), GetModuleHandleW(nullptr), nullptr);
    SendMessageW(handle, WM_SETFONT, reinterpret_cast<WPARAM>(font), FALSE);
    return handle;
}

HWND Edit(HWND window, const wchar_t* label, const wchar_t* value, int y, int limit, int width = 326, DWORD flags = 0) {
    ControlWindow(window, L"STATIC", label, 0, 16, y + 4, 105, 20);
    auto edit = ControlWindow(window, L"EDIT", value, WS_TABSTOP | ES_AUTOHSCROLL | flags,
        126, y, width, 25, 0, WS_EX_CLIENTEDGE);
    SendMessageW(edit, EM_SETLIMITTEXT, limit, 0);
    return edit;
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_CREATE:
        font = static_cast<HFONT>(GetStockObject(DEFAULT_GUI_FONT));
        hostEdit = Edit(window, L"Server IP / host", L"127.0.0.1", 16, 253);
        gameEdit = Edit(window, L"jElancia.exe", L"C:\\Nexon\\Elancia\\jElancia.exe", 49, 32766, 246);
        browseButton = ControlWindow(window, L"BUTTON", L"Browse...", WS_TABSTOP, 380, 49, 72, 25, Browse);
        accountEdit = Edit(window, L"Account ID", L"", 82, 24);
        passwordEdit = Edit(window, L"PASSWORD", L"", 115, 128, 326, ES_PASSWORD);
        registerButton = ControlWindow(window, L"BUTTON", L"Register", WS_TABSTOP, 238, 155, 100, 28, Register);
        loginButton = ControlWindow(window, L"BUTTON", L"Log in and play", WS_TABSTOP | BS_DEFPUSHBUTTON, 345, 155, 107, 28, Login);
        statusLabel = ControlWindow(window, L"STATIC", L"Enter the server IP address and your account.", 0, 16, 196, 436, 45);
        return 0;
    case WM_COMMAND:
        switch (LOWORD(wParam)) {
        case Register: Start(window, true); return 0;
        case Login: Start(window, false); return 0;
        case Browse: {
            wchar_t path[32768]{};
            OPENFILENAMEW dialog{};
            dialog.lStructSize = sizeof(dialog);
            dialog.hwndOwner = window;
            dialog.lpstrFilter = L"jElancia.exe\0jElancia.exe\0Executables\0*.exe\0";
            dialog.lpstrFile = path;
            dialog.nMaxFile = 32768;
            dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
            if (GetOpenFileNameW(&dialog)) SetWindowTextW(gameEdit, path);
            return 0;
        }
        }
        break;
    case Done: {
        std::unique_ptr<Result> result(reinterpret_cast<Result*>(lParam));
        if (!result->renewal) activeGame = result->game;
        busy = false;
        Toggle();
        if (!result->renewal || activeGame == result->game)
            SetStatus(result->message.c_str());
        return 0;
    }
    case SessionRejected:
        if (!busy && activeGame && activeGame->NeedsLogin()) {
            Toggle();
            SetStatus(L"The game session ended. Enter your password and click Sign in again.");
            FLASHWINFO flash{sizeof(FLASHWINFO), window, FLASHW_TRAY, 3, 0};
            FlashWindowEx(&flash);
        }
        return 0;
    case GameClosed:
        activeGame.reset();
        Toggle();
        SetStatus(L"Game closed.");
        return 0;
    case WM_CLOSE:
        if (busy || activeGame) {
            SetStatus(activeGame ? L"Close the game first to stop the secure connection." : L"Wait for the connection attempt to finish.");
            return 0;
        }
        DestroyWindow(window);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    WSADATA sockets{};
    if (WSAStartup(MAKEWORD(2, 2), &sockets)) return 1;
    WNDCLASSW window{};
    window.lpfnWndProc = WindowProc;
    window.hInstance = instance;
    window.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    window.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE + 1);
    window.lpszClassName = WindowClass;
    if (!RegisterClassW(&window)) return 1;
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    RECT size{0, 0, 468, 244};
    AdjustWindowRect(&size, style, FALSE);
    auto main = CreateWindowExW(WS_EX_CONTROLPARENT, WindowClass, L"Elancia Secure Launcher", style,
        CW_USEDEFAULT, CW_USEDEFAULT, size.right - size.left, size.bottom - size.top,
        nullptr, nullptr, instance, nullptr);
    if (!main) return 1;
    ShowWindow(main, show);
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        if (!IsDialogMessageW(main, &message)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    return 0;
}
