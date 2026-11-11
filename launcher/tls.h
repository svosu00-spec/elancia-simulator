#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#define SECURITY_WIN32
#include <security.h>
#include <schannel.h>
#include <wincrypt.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

using Bytes = std::vector<unsigned char>;
using Digest = std::array<unsigned char, 32>;

class Socket {
    SOCKET handle;
public:
    explicit Socket(SOCKET value = INVALID_SOCKET) : handle(value) {}
    ~Socket() { if (handle != INVALID_SOCKET) closesocket(handle); }
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
    Socket(Socket&& other) noexcept : handle(other.handle) { other.handle = INVALID_SOCKET; }
    operator SOCKET() const { return handle; }
};

inline void SendAll(SOCKET socket, const unsigned char* data, size_t length) {
    while (length) {
        int count = send(socket, reinterpret_cast<const char*>(data), static_cast<int>(length), 0);
        if (count <= 0) throw std::runtime_error("The connection closed while sending.");
        data += count;
        length -= count;
    }
}

inline void SetTimeout(SOCKET socket, DWORD milliseconds) {
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&milliseconds), sizeof(milliseconds));
    setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&milliseconds), sizeof(milliseconds));
}

inline Socket TryConnect(const ADDRINFOW& address, long timeoutMs) {
    Socket socket(::socket(address.ai_family, address.ai_socktype, address.ai_protocol));
    if (socket == INVALID_SOCKET) return Socket{};
    u_long nonblocking = 1;
    if (ioctlsocket(socket, FIONBIO, &nonblocking)) return Socket{};
    if (connect(socket, address.ai_addr, static_cast<int>(address.ai_addrlen))) {
        if (WSAGetLastError() != WSAEWOULDBLOCK) return Socket{};
        fd_set ready, failed;
        FD_ZERO(&ready);
        FD_ZERO(&failed);
        FD_SET(socket, &ready);
        FD_SET(socket, &failed);
        timeval timeout{timeoutMs / 1000, timeoutMs % 1000 * 1000};
        if (select(0, nullptr, &ready, &failed, &timeout) <= 0 || !FD_ISSET(socket, &ready))
            return Socket{};
        int error = 0;
        int size = sizeof(error);
        if (getsockopt(socket, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &size) || error)
            return Socket{};
    }
    nonblocking = 0;
    if (ioctlsocket(socket, FIONBIO, &nonblocking)) return Socket{};
    BOOL enabled = TRUE;
    setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&enabled), sizeof(enabled));
    SetTimeout(socket, 12000);
    return socket;
}

inline Socket Connect(const std::wstring& host, const std::wstring& port, DWORD retryWindowMs) {
    ADDRINFOW hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    PADDRINFOW result{};
    if (GetAddrInfoW(host.c_str(), port.c_str(), &hints, &result))
        throw std::runtime_error("The server address could not be resolved.");
    const auto deadline = GetTickCount64() + retryWindowMs;
    for (;;) {
        for (auto address = result; address; address = address->ai_next) {
            const auto now = GetTickCount64();
            if (retryWindowMs && now >= deadline) break;
            auto timeout = retryWindowMs ? std::min<ULONGLONG>(5000, deadline - now) : 5000;
            auto socket = TryConnect(*address, static_cast<long>(timeout));
            if (socket != INVALID_SOCKET) {
                FreeAddrInfoW(result);
                return socket;
            }
        }
        const auto now = GetTickCount64();
        if (!retryWindowMs || now >= deadline) break;
        Sleep(static_cast<DWORD>(std::min<ULONGLONG>(200, deadline - now)));
    }
    FreeAddrInfoW(result);
    throw std::runtime_error("Unable to connect to the server.");
}

inline Digest Sha256(const unsigned char* data, DWORD length) {
    Digest digest{};
    DWORD size = static_cast<DWORD>(digest.size());
    if (!CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr, data, length, digest.data(), &size) ||
        size != digest.size()) throw std::runtime_error("Windows could not calculate SHA-256.");
    return digest;
}

class Tls {
    Socket socket;
    CredHandle credentials{};
    CtxtHandle context{};
    bool hasCredentials = false;
    bool hasContext = false;
    SecPkgContext_StreamSizes sizes{};
    Bytes encrypted;
    Bytes plain;
    size_t plainOffset = 0;

    bool Receive() {
        if (encrypted.size() > 256 * 1024) throw std::runtime_error("TLS input exceeded the allowed size.");
        unsigned char buffer[16384];
        int size = recv(socket, reinterpret_cast<char*>(buffer), sizeof(buffer), 0);
        if (size < 0) throw std::runtime_error("TLS receive failed.");
        if (size == 0) return false;
        encrypted.insert(encrypted.end(), buffer, buffer + size);
        return true;
    }

    void KeepExtra(SecBuffer* buffers, size_t count) {
        size_t extra = 0;
        for (size_t i = 0; i < count; ++i)
            if (buffers[i].BufferType == SECBUFFER_EXTRA) extra = buffers[i].cbBuffer;
        if (extra > encrypted.size()) throw std::runtime_error("Malformed TLS record.");
        encrypted.erase(encrypted.begin(), encrypted.end() - extra);
    }

    void Handshake(const std::wstring& host, const Digest& pin) {
        SCHANNEL_CRED config{};
        config.dwVersion = SCHANNEL_CRED_VERSION;
        config.grbitEnabledProtocols = SP_PROT_TLS1_2_CLIENT;
        config.dwFlags = SCH_CRED_MANUAL_CRED_VALIDATION | SCH_CRED_NO_DEFAULT_CREDS;
        TimeStamp expiry{};
        if (AcquireCredentialsHandleW(nullptr, const_cast<wchar_t*>(UNISP_NAME_W), SECPKG_CRED_OUTBOUND,
                nullptr, &config, nullptr, nullptr, &credentials, &expiry) != SEC_E_OK)
            throw std::runtime_error("TLS 1.2 is unavailable. Check Windows updates and Schannel settings.");
        hasCredentials = true;

        constexpr ULONG flags = ISC_REQ_ALLOCATE_MEMORY | ISC_REQ_STREAM | ISC_REQ_CONFIDENTIALITY |
            ISC_REQ_REPLAY_DETECT | ISC_REQ_SEQUENCE_DETECT | ISC_REQ_EXTENDED_ERROR;
        while (true) {
            SecBuffer input[] = {{static_cast<ULONG>(encrypted.size()), SECBUFFER_TOKEN, encrypted.data()},
                                 {0, SECBUFFER_EMPTY, nullptr}};
            SecBufferDesc inDesc{SECBUFFER_VERSION, 2, input};
            SecBuffer output{0, SECBUFFER_TOKEN, nullptr};
            SecBufferDesc outDesc{SECBUFFER_VERSION, 1, &output};
            ULONG attributes{};
            SECURITY_STATUS status = InitializeSecurityContextW(&credentials, hasContext ? &context : nullptr,
                const_cast<wchar_t*>(host.c_str()), flags, 0, SECURITY_NATIVE_DREP, hasContext ? &inDesc : nullptr,
                0, &context, &outDesc, &attributes, &expiry);
            if (status == SEC_E_OK || status == SEC_I_CONTINUE_NEEDED) hasContext = true;
            if (output.pvBuffer) {
                try { SendAll(socket, static_cast<unsigned char*>(output.pvBuffer), output.cbBuffer); }
                catch (...) { FreeContextBuffer(output.pvBuffer); throw; }
                FreeContextBuffer(output.pvBuffer);
            }
            if (status != SEC_E_INCOMPLETE_MESSAGE) KeepExtra(input, 2);
            if (status == SEC_E_OK) break;
            if (status != SEC_I_CONTINUE_NEEDED && status != SEC_E_INCOMPLETE_MESSAGE)
                throw std::runtime_error("TLS 1.2 negotiation failed.");
            if ((encrypted.empty() || status == SEC_E_INCOMPLETE_MESSAGE) && !Receive())
                throw std::runtime_error("The server closed the TLS handshake.");
        }

        PCCERT_CONTEXT certificate{};
        if (QueryContextAttributesW(&context, SECPKG_ATTR_REMOTE_CERT_CONTEXT, &certificate) != SEC_E_OK)
            throw std::runtime_error("The server did not supply a certificate.");
        bool valid = false;
        try {
            valid = CertVerifyTimeValidity(nullptr, certificate->pCertInfo) == 0 &&
                Sha256(certificate->pbCertEncoded, certificate->cbCertEncoded) == pin;
        } catch (...) { CertFreeCertificateContext(certificate); throw; }
        CertFreeCertificateContext(certificate);
        if (!valid) throw std::runtime_error("The certificate fingerprint or validity does not match.");
        if (QueryContextAttributesW(&context, SECPKG_ATTR_STREAM_SIZES, &sizes) != SEC_E_OK)
            throw std::runtime_error("TLS record limits could not be read.");
    }

    void Free() {
        if (hasContext) DeleteSecurityContext(&context);
        if (hasCredentials) FreeCredentialsHandle(&credentials);
    }

public:
    Tls(const std::wstring& host, const std::wstring& port, const Digest& pin, DWORD retryWindowMs = 0)
        : socket(Connect(host, port, retryWindowMs)) {
        try { Handshake(host, pin); }
        catch (...) { Free(); throw; }
    }
    ~Tls() { Free(); }
    void Stop() { shutdown(socket, SD_BOTH); }
    void DisableTimeout() { SetTimeout(socket, 0); }

    void Write(const unsigned char* data, size_t size) {
        while (size) {
            ULONG length = static_cast<ULONG>(std::min<size_t>(size, sizes.cbMaximumMessage));
            Bytes record(sizes.cbHeader + length + sizes.cbTrailer);
            std::memcpy(record.data() + sizes.cbHeader, data, length);
            SecBuffer buffers[] = {{sizes.cbHeader, SECBUFFER_STREAM_HEADER, record.data()},
                {length, SECBUFFER_DATA, record.data() + sizes.cbHeader},
                {sizes.cbTrailer, SECBUFFER_STREAM_TRAILER, record.data() + sizes.cbHeader + length},
                {0, SECBUFFER_EMPTY, nullptr}};
            SecBufferDesc desc{SECBUFFER_VERSION, 4, buffers};
            auto status = EncryptMessage(&context, 0, &desc, 0);
            if (status == SEC_E_OK) {
                try { SendAll(socket, record.data(), buffers[0].cbBuffer + buffers[1].cbBuffer + buffers[2].cbBuffer); }
                catch (...) { SecureZeroMemory(record.data(), record.size()); throw; }
            }
            SecureZeroMemory(record.data(), record.size());
            if (status != SEC_E_OK) throw std::runtime_error("TLS encryption failed.");
            size -= length;
            data += length;
        }
    }

    size_t Read(unsigned char* destination, size_t capacity) {
        while (plainOffset == plain.size()) {
            plain.clear();
            plainOffset = 0;
            if (encrypted.empty() && !Receive()) return 0;
            SecBuffer buffers[] = {{static_cast<ULONG>(encrypted.size()), SECBUFFER_DATA, encrypted.data()},
                {0, SECBUFFER_EMPTY, nullptr}, {0, SECBUFFER_EMPTY, nullptr}, {0, SECBUFFER_EMPTY, nullptr}};
            SecBufferDesc desc{SECBUFFER_VERSION, 4, buffers};
            auto status = DecryptMessage(&context, &desc, 0, nullptr);
            if (status == SEC_E_INCOMPLETE_MESSAGE) {
                if (!Receive()) throw std::runtime_error("The TLS record was truncated.");
                continue;
            }
            if (status == SEC_I_CONTEXT_EXPIRED) return 0;
            if (status != SEC_E_OK) throw std::runtime_error("TLS decryption failed.");
            for (auto& buffer : buffers) {
                if (buffer.BufferType != SECBUFFER_DATA || !buffer.cbBuffer) continue;
                auto start = static_cast<unsigned char*>(buffer.pvBuffer);
                plain.assign(start, start + buffer.cbBuffer);
            }
            KeepExtra(buffers, 4);
        }
        size_t count = std::min(capacity, plain.size() - plainOffset);
        std::memcpy(destination, plain.data() + plainOffset, count);
        plainOffset += count;
        return count;
    }

    void ReadExact(unsigned char* destination, size_t size) {
        while (size) {
            size_t count = Read(destination, size);
            if (!count) throw std::runtime_error("The server closed the connection.");
            destination += count;
            size -= count;
        }
    }
};
