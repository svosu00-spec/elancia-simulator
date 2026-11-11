#include "equipment_archive.h"
#include "tls.h"
#include <zlib.h>
#include <fstream>
#include <map>
#include <sstream>

namespace {
constexpr size_t DirectoryEntrySize = 68;
constexpr uint32_t MaxPackedArchive = 8 * 1024 * 1024;

struct Resource {
    std::istream* stream;
    uint32_t offset;
    uint32_t size;
};

using Directory = std::map<std::wstring, Resource>;

uint16_t ReadU16(const unsigned char* data) {
    return static_cast<uint16_t>(data[0] | (data[1] << 8));
}

uint32_t ReadU32(const unsigned char* data) {
    uint32_t value = 0;
    for (int i = 0; i < 4; ++i) value |= static_cast<uint32_t>(data[i]) << (8 * i);
    return value;
}

void WriteU32(std::ostream& output, uint32_t value) {
    for (int i = 0; i < 4; ++i) output.put(static_cast<char>(value >> (8 * i)));
}

void ReadExact(std::istream& input, void* buffer, size_t size) {
    if (!input.read(static_cast<char*>(buffer), size))
        throw std::runtime_error("An equipment archive is incomplete.");
}

void ReadDirectory(std::istream& input, uint64_t size, Directory& resources) {
    unsigned char countBytes[4];
    ReadExact(input, countBytes, sizeof(countBytes));
    const uint32_t count = ReadU32(countBytes);
    const uint64_t headerSize = 4 + static_cast<uint64_t>(count) * DirectoryEntrySize;
    if (count < 2 || count > 100000 || headerSize > size)
        throw std::runtime_error("Invalid equipment archive directory.");

    Bytes header(count * DirectoryEntrySize);
    ReadExact(input, header.data(), header.size());
    if (ReadU32(header.data()) != headerSize || ReadU32(header.data() + header.size() - DirectoryEntrySize) != size)
        throw std::runtime_error("Invalid equipment archive boundaries.");

    for (uint32_t i = 0; i + 1 < count; ++i) {
        const auto* entry = header.data() + i * DirectoryEntrySize;
        const uint32_t start = ReadU32(entry), end = ReadU32(entry + DirectoryEntrySize);
        if (end < start || end > size) throw std::runtime_error("Invalid equipment resource bounds.");
        wchar_t name[32];
        static_assert(sizeof(name) == DirectoryEntrySize - 4);
        std::memcpy(name, entry + 4, sizeof(name));
        const std::wstring key(name, std::find(name, name + 32, L'\0'));
        if (key.empty() || !resources.emplace(key, Resource{&input, start, end - start}).second)
            throw std::runtime_error("An equipment resource name is duplicated or empty.");
    }
}

[[noreturn]] void InvalidCompactEpf() {
    throw std::runtime_error("Invalid compact Orc equipment resource.");
}

std::string ExpandCompactEpf(const Resource& resource, uint32_t& remainingBytes) {
    constexpr size_t EpfHeader = 12, CompactHeader = 16, FrameEntry = 16;
    if (resource.size < CompactHeader) InvalidCompactEpf();
    Bytes packed(resource.size);
    resource.stream->seekg(resource.offset);
    ReadExact(*resource.stream, packed.data(), packed.size());
    const size_t count = ReadU16(packed.data());
    const size_t tableSize = (count + 1) * FrameEntry;
    const uint32_t frameBytes = ReadU32(packed.data() + 8);
    const uint32_t maskBytes = ReadU32(packed.data() + 12);
    if (!count || tableSize > packed.size() - CompactHeader ||
        maskBytes > packed.size() - CompactHeader - tableSize ||
        remainingBytes < EpfHeader + tableSize || frameBytes > remainingBytes - EpfHeader - tableSize)
        InvalidCompactEpf();

    std::string native(EpfHeader + frameBytes + tableSize, '\0');
    auto* output = reinterpret_cast<unsigned char*>(native.data());
    std::memcpy(output, packed.data(), EpfHeader);
    auto* table = output + EpfHeader + frameBytes;
    const auto* columns = packed.data() + CompactHeader;
    for (size_t index = 0; index <= count; ++index)
        for (size_t field = 0; field < FrameEntry; ++field)
            table[index * FrameEntry + field] = columns[field * (count + 1) + index];
    if (ReadU32(table + 8) != 0 || ReadU32(table + count * FrameEntry + 8) != frameBytes)
        InvalidCompactEpf();

    const auto* mask = columns + tableSize;
    const auto* maskEnd = mask + maskBytes;
    const auto* color = maskEnd;
    const auto* colorEnd = packed.data() + packed.size();
    for (size_t index = 0; index < count; ++index) {
        const auto* entry = table + index * FrameEntry;
        const int width = static_cast<int16_t>(ReadU16(entry + 6)) - static_cast<int16_t>(ReadU16(entry + 2));
        const int height = static_cast<int16_t>(ReadU16(entry + 4)) - static_cast<int16_t>(ReadU16(entry));
        const uint32_t start = ReadU32(entry + 8), end = ReadU32(entry + 12);
        const uint32_t following = ReadU32(entry + FrameEntry + 8);
        if (start > end || end > following || following > frameBytes || width < 0 || height < 0 ||
            static_cast<uint64_t>(width) * height != end - start ||
            following - end > static_cast<size_t>(maskEnd - mask))
            InvalidCompactEpf();

        const auto* nextMask = mask + (following - end);
        if (start != end) {
            auto* pixels = output + EpfHeader + start;
            auto* run = mask;
            for (int row = 0; row < height; ++row) {
                int column = 0;
                while (run < nextMask && *run) {
                    const unsigned char flag = *run++;
                    const int length = flag & 0x7f;
                    if (!length || column + length > width + 1) InvalidCompactEpf();
                    // The mask includes one extra column outside the stored pixel row.
                    const int visible = std::min(column + length, width) - column;
                    if (flag & 0x80) {
                        if (visible > colorEnd - color) InvalidCompactEpf();
                        std::memcpy(pixels + column, color, visible);
                        color += visible;
                    }
                    column += length;
                }
                if (run == nextMask || column != width + 1) InvalidCompactEpf();
                ++run;
                pixels += width;
            }
        }
        std::memcpy(output + EpfHeader + end, mask, nextMask - mask);
        mask = nextMask;
    }
    if (mask != maskEnd || color != colorEnd) InvalidCompactEpf();
    remainingBytes -= static_cast<uint32_t>(native.size());
    return native;
}

std::wstring Hex(const Digest& digest) {
    constexpr wchar_t digits[] = L"0123456789abcdef";
    std::wstring value;
    for (auto byte : digest) {
        value.push_back(digits[byte >> 4]);
        value.push_back(digits[byte & 15]);
    }
    return value;
}

bool HasSize(const std::filesystem::path& path, uint64_t size) {
    std::error_code error;
    return std::filesystem::file_size(path, error) == size && !error;
}

void WriteMerged(std::ostream& output, const Directory& resources, uint64_t size) {
    uint32_t offset = 4 + DirectoryEntrySize * (resources.size() + 1);
    WriteU32(output, static_cast<uint32_t>(resources.size() + 1));
    for (const auto& [name, item] : resources) {
        WriteU32(output, offset);
        wchar_t field[32]{};
        std::copy(name.begin(), name.end(), field);
        output.write(reinterpret_cast<const char*>(field), sizeof(field));
        offset += item.size;
    }
    WriteU32(output, offset);
    const char terminator[DirectoryEntrySize - 4]{};
    output.write(terminator, sizeof(terminator));
    if (offset != size) throw std::runtime_error("Unexpected merged equipment archive size.");

    Bytes chunk(1024 * 1024);
    for (const auto& entry : resources) {
        const auto& item = entry.second;
        item.stream->seekg(item.offset);
        for (uint32_t remaining = item.size; remaining;) {
            const size_t length = std::min<size_t>(remaining, chunk.size());
            ReadExact(*item.stream, chunk.data(), length);
            output.write(reinterpret_cast<const char*>(chunk.data()), length);
            if (!output) throw std::runtime_error("Could not write the cached equipment archive.");
            remaining -= static_cast<uint32_t>(length);
        }
    }
}
}

std::filesystem::path PrepareEquipmentArchive(const std::filesystem::path& source,
                                               const std::filesystem::path& pack,
                                               const std::filesystem::path& cache) {
    std::ifstream packed(pack, std::ios::binary | std::ios::ate);
    const auto packSize = packed.tellg();
    if (!packed || packSize < 9 || packSize > MaxPackedArchive)
        throw std::runtime_error("Put a valid Elancia-Orc.pack next to the launcher.");
    Bytes compressed(static_cast<size_t>(packSize));
    packed.seekg(0);
    ReadExact(packed, compressed.data(), compressed.size());
    const uint32_t overlaySize = ReadU32(compressed.data() + 4);
    const bool compact = std::memcmp(compressed.data(), "ELY2", 4) == 0;
    const size_t headerSize = compact ? 12 : 8;
    if ((!compact && std::memcmp(compressed.data(), "ELY1", 4)) || compressed.size() <= headerSize ||
        overlaySize < 4 + 2 * DirectoryEntrySize || overlaySize > MaxPackedArchive)
        throw std::runtime_error("Invalid compressed Orc equipment header.");
    const uint32_t storedSize = compact ? ReadU32(compressed.data() + 8) : overlaySize;
    if (storedSize < 4 + 2 * DirectoryEntrySize || storedSize > MaxPackedArchive)
        throw std::runtime_error("Invalid compressed Orc equipment header.");

    std::ifstream original(source, std::ios::binary | std::ios::ate);
    const auto originalSize = original.tellg();
    if (!original || originalSize < 4 + 2 * DirectoryEntrySize || originalSize > UINT32_MAX)
        throw std::runtime_error("The installed Data\\Yog.dat is missing or invalid.");
    const uint64_t mergedSize = static_cast<uint64_t>(originalSize) + overlaySize - 4 - DirectoryEntrySize;
    if (mergedSize > UINT32_MAX) throw std::runtime_error("The merged equipment archive is too large.");

    const auto identity = source.wstring() + L"|" + std::to_wstring(static_cast<uint64_t>(originalSize)) +
        L"|" + std::to_wstring(std::filesystem::last_write_time(source).time_since_epoch().count()) +
        L"|" + Hex(Sha256(compressed.data(), static_cast<DWORD>(compressed.size())));
    const auto revision = Hex(Sha256(reinterpret_cast<const unsigned char*>(identity.data()),
                                    static_cast<DWORD>(identity.size() * sizeof(wchar_t))));
    const auto target = std::filesystem::absolute(cache / (L"Yog-" + revision + L".dat"));
    if (target.wstring().size() >= MAX_PATH)
        throw std::runtime_error("The equipment archive cache path is too long.");
    if (HasSize(target, mergedSize)) return target;

    std::string decoded(storedSize, '\0');
    uLongf decodedSize = storedSize;
    uLong inputSize = static_cast<uLong>(compressed.size() - headerSize);
    if (uncompress2(reinterpret_cast<Bytef*>(decoded.data()), &decodedSize, compressed.data() + headerSize, &inputSize) != Z_OK ||
        decodedSize != storedSize || inputSize != compressed.size() - headerSize)
        throw std::runtime_error("The compressed Orc equipment file is damaged.");

    std::istringstream overlay(std::move(decoded), std::ios::binary);
    Directory resources;
    original.seekg(0);
    ReadDirectory(original, static_cast<uint64_t>(originalSize), resources);
    std::map<std::wstring, std::istringstream> expanded;
    if (compact) {
        Directory packedResources;
        ReadDirectory(overlay, storedSize, packedResources);
        const size_t directorySize = 4 + DirectoryEntrySize * (packedResources.size() + 1);
        if (directorySize > overlaySize) InvalidCompactEpf();
        uint32_t remainingBytes = overlaySize - static_cast<uint32_t>(directorySize);
        for (const auto& [name, item] : packedResources) {
            auto data = ExpandCompactEpf(item, remainingBytes);
            const auto size = static_cast<uint32_t>(data.size());
            auto entry = expanded.try_emplace(name, std::move(data), std::ios::binary).first;
            if (!resources.emplace(name, Resource{&entry->second, 0, size}).second)
                throw std::runtime_error("An equipment resource name is duplicated.");
        }
        if (remainingBytes) InvalidCompactEpf();
    } else {
        ReadDirectory(overlay, overlaySize, resources);
    }

    std::filesystem::create_directories(target.parent_path());
    wchar_t temporary[MAX_PATH];
    if (!GetTempFileNameW(target.parent_path().c_str(), L"YOG", 0, temporary))
        throw std::runtime_error("Could not open the equipment archive cache.");
    try {
        std::ofstream output(std::filesystem::path(temporary), std::ios::binary);
        if (!output) throw std::runtime_error("Could not open the cached equipment archive.");
        WriteMerged(output, resources, mergedSize);
        output.close();
        if (!output || !HasSize(temporary, mergedSize))
            throw std::runtime_error("The equipment archive cache could not be saved.");
        if (!MoveFileExW(temporary, target.c_str(), MOVEFILE_REPLACE_EXISTING) && !HasSize(target, mergedSize))
            throw std::runtime_error("Could not activate the cached equipment archive.");
    } catch (...) {
        DeleteFileW(temporary);
        throw;
    }
    DeleteFileW(temporary);
    return target;
}
